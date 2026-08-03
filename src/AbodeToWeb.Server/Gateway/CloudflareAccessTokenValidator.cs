using System.Buffers;
using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AbodeToWeb.Server.Gateway;

public enum CloudflareAccessValidationFailure
{
    None,
    ConfigurationInvalid,
    TokenMalformed,
    SigningKeysUnavailable,
    SigningKeyNotFound,
    SignatureInvalid,
    ClaimsInvalid
}

public readonly record struct CloudflareAccessValidationResult(
    bool IsValid,
    CloudflareAccessValidationFailure Failure,
    string? Email = null)
{
    public static CloudflareAccessValidationResult Success(string? email) =>
        new(true, CloudflareAccessValidationFailure.None, email);

    public static CloudflareAccessValidationResult Failed(CloudflareAccessValidationFailure failure) =>
        new(false, failure);
}

public interface ICloudflareAccessTokenValidator
{
    ValueTask<CloudflareAccessValidationResult> ValidateAsync(
        string assertion,
        string teamDomain,
        string applicationAudience,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates Cloudflare Access application assertions against the account's rotating
/// RS256 signing keys. Signing keys are fetched from Cloudflare's documented certs
/// endpoint and retained only for a bounded period.
/// </summary>
public sealed class CloudflareAccessTokenValidator : ICloudflareAccessTokenValidator, IDisposable
{
    private static readonly TimeSpan SigningKeyFreshness = TimeSpan.FromHours(1);
    private static readonly TimeSpan SigningKeyMaximumAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan SigningKeyFailureBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);
    private const int MaximumAssertionCharacters = 16 * 1024;
    private const int MaximumJsonSegmentBytes = 8 * 1024;
    private const int MaximumCertDocumentBytes = 256 * 1024;
    private const int MaximumSigningKeys = 8;

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private SigningKeyCache? _cache;
    private DateTimeOffset _nextFetchAttemptUtc = DateTimeOffset.MinValue;

    public CloudflareAccessTokenValidator(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CloudflareAccessValidationResult> ValidateAsync(
        string assertion,
        string teamDomain,
        string applicationAudience,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeConfiguration(teamDomain, applicationAudience, out var issuer, out var audience))
        {
            return CloudflareAccessValidationResult.Failed(CloudflareAccessValidationFailure.ConfigurationInvalid);
        }

        if (!TryParseAssertion(assertion, out var token))
        {
            return CloudflareAccessValidationResult.Failed(CloudflareAccessValidationFailure.TokenMalformed);
        }

        var keyResult = await GetSigningKeyAsync(issuer, token.KeyId, cancellationToken);
        if (keyResult.Failure != CloudflareAccessValidationFailure.None)
        {
            return CloudflareAccessValidationResult.Failed(keyResult.Failure);
        }

        if (!VerifySignature(token, keyResult.Key!.Value))
        {
            return CloudflareAccessValidationResult.Failed(CloudflareAccessValidationFailure.SignatureInvalid);
        }

        return ValidateClaims(token.Payload, issuer, audience);
    }

    private async ValueTask<SigningKeyResult> GetSigningKeyAsync(
        string issuer,
        string keyId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var snapshot = _cache;
        if (snapshot is not null
            && snapshot.Issuer.Equals(issuer, StringComparison.Ordinal)
            && now <= snapshot.FreshUntilUtc
            && snapshot.Keys.TryGetValue(keyId, out var freshKey))
        {
            return SigningKeyResult.Success(freshKey);
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            snapshot = _cache;
            if (snapshot is not null
                && snapshot.Issuer.Equals(issuer, StringComparison.Ordinal)
                && now <= snapshot.FreshUntilUtc
                && snapshot.Keys.TryGetValue(keyId, out freshKey))
            {
                return SigningKeyResult.Success(freshKey);
            }

            var mayFetch = now >= _nextFetchAttemptUtc;
            if (mayFetch)
            {
                try
                {
                    var keys = await FetchSigningKeysAsync(issuer, cancellationToken);
                    snapshot = new SigningKeyCache(
                        issuer,
                        keys,
                        now.Add(SigningKeyFreshness),
                        now.Add(SigningKeyMaximumAge));
                    _cache = snapshot;
                    // A previously unseen kid may signal a signing-key rotation. Permit
                    // one early refresh per backoff window without allowing arbitrary
                    // attacker-supplied kids to turn every request into an HTTP fetch.
                    _nextFetchAttemptUtc = now.Add(SigningKeyFailureBackoff);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _nextFetchAttemptUtc = now.Add(SigningKeyFailureBackoff);
                }
                catch (Exception exception) when (exception is HttpRequestException
                                                     or IOException
                                                     or JsonException
                                                     or CryptographicException
                                                     or InvalidDataException)
                {
                    _nextFetchAttemptUtc = now.Add(SigningKeyFailureBackoff);
                }
            }

            if (snapshot is not null
                && snapshot.Issuer.Equals(issuer, StringComparison.Ordinal)
                && now <= snapshot.StaleUntilUtc
                && snapshot.Keys.TryGetValue(keyId, out var cachedKey))
            {
                return SigningKeyResult.Success(cachedKey);
            }

            return snapshot is not null && snapshot.Issuer.Equals(issuer, StringComparison.Ordinal)
                ? SigningKeyResult.Failed(CloudflareAccessValidationFailure.SigningKeyNotFound)
                : SigningKeyResult.Failed(CloudflareAccessValidationFailure.SigningKeysUnavailable);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, RSAParameters>> FetchSigningKeysAsync(
        string issuer,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(issuer + "/", UriKind.Absolute), "cdn-cgi/access/certs");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaximumCertDocumentBytes)
        {
            throw new InvalidDataException("Cloudflare Access signing-key response is too large.");
        }

        var payload = await ReadBoundedAsync(response.Content, MaximumCertDocumentBytes, timeout.Token);
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !TryGetUniqueProperty(document.RootElement, "keys", out var keysElement)
            || keysElement.ValueKind != JsonValueKind.Array
            || keysElement.GetArrayLength() is < 1 or > MaximumSigningKeys)
        {
            throw new InvalidDataException("Cloudflare Access signing-key response is invalid.");
        }

        var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        foreach (var element in keysElement.EnumerateArray())
        {
            if (!TryReadSigningKey(element, out var keyId, out var parameters)
                || !keys.TryAdd(keyId, parameters))
            {
                throw new InvalidDataException("Cloudflare Access signing-key response contains an invalid or duplicate key.");
            }
        }

        return new ReadOnlyDictionary<string, RSAParameters>(keys);
    }

    private static bool TryReadSigningKey(JsonElement element, out string keyId, out RSAParameters parameters)
    {
        keyId = string.Empty;
        parameters = default;
        if (element.ValueKind != JsonValueKind.Object
            || HasDuplicateProperties(element)
            || !TryReadString(element, "kid", out keyId)
            || keyId.Length is < 1 or > 256
            || !TryReadString(element, "kty", out var keyType)
            || keyType != "RSA"
            || !TryReadString(element, "alg", out var algorithm)
            || algorithm != "RS256"
            || !TryReadString(element, "use", out var use)
            || use != "sig"
            || !TryReadString(element, "n", out var modulusText)
            || !TryReadString(element, "e", out var exponentText)
            || !TryDecodeBase64Url(modulusText, 1024, out var modulus)
            || !TryDecodeBase64Url(exponentText, 8, out var exponent)
            || modulus.Length < 256
            || exponent.Length is < 1 or > 8)
        {
            return false;
        }

        parameters = new RSAParameters { Modulus = modulus, Exponent = exponent };
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return rsa.KeySize >= 2048;
        }
        catch (CryptographicException)
        {
            parameters = default;
            return false;
        }
    }

    private static bool TryParseAssertion(string assertion, out ParsedAssertion token)
    {
        token = default;
        if (string.IsNullOrWhiteSpace(assertion)
            || assertion.Length > MaximumAssertionCharacters
            || assertion.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var firstDot = assertion.IndexOf('.');
        var secondDot = firstDot < 0 ? -1 : assertion.IndexOf('.', firstDot + 1);
        if (firstDot <= 0 || secondDot <= firstDot + 1 || secondDot >= assertion.Length - 1
            || assertion.IndexOf('.', secondDot + 1) >= 0)
        {
            return false;
        }

        var headerSegment = assertion[..firstDot];
        var payloadSegment = assertion[(firstDot + 1)..secondDot];
        var signatureSegment = assertion[(secondDot + 1)..];
        if (!TryDecodeBase64Url(headerSegment, MaximumJsonSegmentBytes, out var headerBytes)
            || !TryDecodeBase64Url(payloadSegment, MaximumJsonSegmentBytes, out var payloadBytes)
            || !TryDecodeBase64Url(signatureSegment, 1024, out var signature))
        {
            return false;
        }

        try
        {
            using var header = JsonDocument.Parse(headerBytes, StrictTokenJsonOptions);
            using var payload = JsonDocument.Parse(payloadBytes, StrictTokenJsonOptions);
            if (header.RootElement.ValueKind != JsonValueKind.Object
                || payload.RootElement.ValueKind != JsonValueKind.Object
                || HasDuplicateProperties(header.RootElement)
                || HasDuplicateProperties(payload.RootElement)
                || !TryReadString(header.RootElement, "alg", out var algorithm)
                || algorithm != "RS256"
                || !TryReadString(header.RootElement, "kid", out var keyId)
                || keyId.Length is < 1 or > 256
                || (TryGetUniqueProperty(header.RootElement, "typ", out var type)
                    && (type.ValueKind != JsonValueKind.String || type.GetString() != "JWT")))
            {
                return false;
            }

            token = new ParsedAssertion(
                keyId,
                Encoding.ASCII.GetBytes(assertion[..secondDot]),
                signature,
                payload.RootElement.Clone());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool VerifySignature(ParsedAssertion assertion, RSAParameters parameters)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return rsa.VerifyData(
                assertion.SigningInput,
                assertion.Signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private CloudflareAccessValidationResult ValidateClaims(JsonElement payload, string issuer, string audience)
    {
        if (!TryReadString(payload, "iss", out var tokenIssuer)
            || !tokenIssuer.Equals(issuer, StringComparison.Ordinal)
            || !TryReadString(payload, "type", out var tokenType)
            || tokenType != "app"
            || !ContainsAudience(payload, audience)
            || !TryReadUnixTime(payload, "exp", required: true, out var expiresUtc))
        {
            return CloudflareAccessValidationResult.Failed(CloudflareAccessValidationFailure.ClaimsInvalid);
        }

        var now = _timeProvider.GetUtcNow();
        if (expiresUtc <= now.Subtract(ClockSkew)
            || !TryReadUnixTime(payload, "nbf", required: false, out var notBeforeUtc)
            || (notBeforeUtc != DateTimeOffset.MinValue && notBeforeUtc > now.Add(ClockSkew))
            || !TryReadUnixTime(payload, "iat", required: false, out var issuedUtc)
            || (issuedUtc != DateTimeOffset.MinValue && issuedUtc > now.Add(ClockSkew))
            || (issuedUtc != DateTimeOffset.MinValue && expiresUtc <= issuedUtc))
        {
            return CloudflareAccessValidationResult.Failed(CloudflareAccessValidationFailure.ClaimsInvalid);
        }

        var email = TryReadString(payload, "email", out var claimedEmail)
                    && claimedEmail.Length <= 320
            ? claimedEmail
            : null;
        return CloudflareAccessValidationResult.Success(email);
    }

    private static bool ContainsAudience(JsonElement payload, string expectedAudience)
    {
        if (!TryGetUniqueProperty(payload, "aud", out var audience))
        {
            return false;
        }

        if (audience.ValueKind == JsonValueKind.String)
        {
            return audience.GetString() == expectedAudience;
        }

        if (audience.ValueKind != JsonValueKind.Array || audience.GetArrayLength() is < 1 or > 16)
        {
            return false;
        }

        return audience.EnumerateArray().Any(value =>
            value.ValueKind == JsonValueKind.String && value.GetString() == expectedAudience);
    }

    private static bool TryReadUnixTime(
        JsonElement payload,
        string name,
        bool required,
        out DateTimeOffset value)
    {
        value = DateTimeOffset.MinValue;
        if (!TryGetUniqueProperty(payload, name, out var property))
        {
            return !required;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var seconds))
        {
            return false;
        }

        try
        {
            value = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryNormalizeConfiguration(
        string teamDomain,
        string applicationAudience,
        out string issuer,
        out string audience)
    {
        issuer = string.Empty;
        audience = string.Empty;
        if (string.IsNullOrWhiteSpace(teamDomain)
            || !Uri.TryCreate(teamDomain, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IdnHost.EndsWith(".cloudflareaccess.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Length <= ".cloudflareaccess.com".Length
            || applicationAudience is not { Length: 64 }
            || !applicationAudience.All(Uri.IsHexDigit))
        {
            return false;
        }

        issuer = $"https://{uri.IdnHost.ToLowerInvariant()}";
        audience = applicationAudience;
        return true;
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetUniqueProperty(element, name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetUniqueProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.NameEquals(name))
            {
                continue;
            }

            if (found)
            {
                return false;
            }

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().Any(property => !names.Add(property.Name));
    }

    private static bool TryDecodeBase64Url(string encoded, int maximumBytes, out byte[] decoded)
    {
        decoded = [];
        if (string.IsNullOrEmpty(encoded)
            || encoded.Length > checked(maximumBytes * 4 / 3 + 4)
            || encoded.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var remainder = encoded.Length % 4;
        if (remainder == 1)
        {
            return false;
        }

        var paddedLength = encoded.Length + (remainder == 0 ? 0 : 4 - remainder);
        var rented = ArrayPool<char>.Shared.Rent(paddedLength);
        try
        {
            for (var index = 0; index < encoded.Length; index++)
            {
                rented[index] = encoded[index] switch { '-' => '+', '_' => '/', var character => character };
            }

            for (var index = encoded.Length; index < paddedLength; index++)
            {
                rented[index] = '=';
            }

            var buffer = new byte[maximumBytes];
            if (!Convert.TryFromBase64Chars(rented.AsSpan(0, paddedLength), buffer, out var bytesWritten)
                || bytesWritten <= 0
                || bytesWritten > maximumBytes)
            {
                return false;
            }

            decoded = buffer[..bytesWritten];
            return true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented, clearArray: true);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maximumBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (count == 0)
            {
                break;
            }

            total += count;
        }

        if (total > maximumBytes)
        {
            throw new InvalidDataException("Cloudflare Access signing-key response is too large.");
        }

        return buffer[..total];
    }

    private static JsonDocumentOptions StrictTokenJsonOptions { get; } = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    public void Dispose() => _refreshLock.Dispose();

    private readonly record struct ParsedAssertion(
        string KeyId,
        byte[] SigningInput,
        byte[] Signature,
        JsonElement Payload);

    private readonly record struct SigningKeyResult(
        RSAParameters? Key,
        CloudflareAccessValidationFailure Failure)
    {
        internal static SigningKeyResult Success(RSAParameters key) =>
            new(key, CloudflareAccessValidationFailure.None);

        internal static SigningKeyResult Failed(CloudflareAccessValidationFailure failure) =>
            new(null, failure);
    }

    private sealed record SigningKeyCache(
        string Issuer,
        IReadOnlyDictionary<string, RSAParameters> Keys,
        DateTimeOffset FreshUntilUtc,
        DateTimeOffset StaleUntilUtc);
}
