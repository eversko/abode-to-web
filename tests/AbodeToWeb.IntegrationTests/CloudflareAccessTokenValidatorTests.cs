using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AbodeToWeb.Server.Gateway;

namespace AbodeToWeb.IntegrationTests;

public sealed class CloudflareAccessTokenValidatorTests
{
    private const string Issuer = "https://abode-to-web-tests.cloudflareaccess.com";
    private const string Audience = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Validates_signature_claims_and_email_while_caching_signing_keys()
    {
        using var key = RSA.Create(2048);
        var requests = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests++;
            Assert.Equal(Issuer + "/cdn-cgi/access/certs", request.RequestUri?.AbsoluteUri);
            return JsonResponse(CreateJwks(("current", key)));
        }));
        using var validator = new CloudflareAccessTokenValidator(client, new MutableTimeProvider(Now));
        var token = CreateToken(key, "current", ValidClaims() with { Email = "owner@example.com" });

        var first = await validator.ValidateAsync(token, Issuer, Audience);
        var second = await validator.ValidateAsync(token, Issuer, Audience);

        Assert.True(first.IsValid);
        Assert.Equal("owner@example.com", first.Email);
        Assert.True(second.IsValid);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Rejects_wrong_issuer_audience_time_window_and_token_type()
    {
        using var key = RSA.Create(2048);
        using var client = new HttpClient(new StubHandler(_ => JsonResponse(CreateJwks(("current", key)))));
        using var validator = new CloudflareAccessTokenValidator(client, new MutableTimeProvider(Now));
        var invalidClaims = new[]
        {
            ValidClaims() with { Issuer = "https://other.cloudflareaccess.com" },
            ValidClaims() with { Audiences = [new string('f', 64)] },
            ValidClaims() with { Expires = Now.AddMinutes(-5).ToUnixTimeSeconds() },
            ValidClaims() with { NotBefore = Now.AddMinutes(5).ToUnixTimeSeconds() },
            ValidClaims() with { Type = "org" }
        };

        foreach (var claims in invalidClaims)
        {
            var result = await validator.ValidateAsync(CreateToken(key, "current", claims), Issuer, Audience);
            Assert.False(result.IsValid);
            Assert.Equal(CloudflareAccessValidationFailure.ClaimsInvalid, result.Failure);
        }
    }

    [Fact]
    public async Task Rejects_algorithm_substitution_and_bad_signatures()
    {
        using var trustedKey = RSA.Create(2048);
        using var attackerKey = RSA.Create(2048);
        var requests = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return JsonResponse(CreateJwks(("current", trustedKey)));
        }));
        using var validator = new CloudflareAccessTokenValidator(client, new MutableTimeProvider(Now));

        var substituted = CreateToken(attackerKey, "current", ValidClaims(), algorithm: "HS256");
        var substitutedResult = await validator.ValidateAsync(substituted, Issuer, Audience);
        Assert.Equal(CloudflareAccessValidationFailure.TokenMalformed, substitutedResult.Failure);
        Assert.Equal(0, requests);

        var badSignature = CreateToken(attackerKey, "current", ValidClaims());
        var signatureResult = await validator.ValidateAsync(badSignature, Issuer, Audience);
        Assert.Equal(CloudflareAccessValidationFailure.SignatureInvalid, signatureResult.Failure);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Refreshes_for_a_rotated_kid_after_the_bounded_backoff()
    {
        using var originalKey = RSA.Create(2048);
        using var rotatedKey = RSA.Create(2048);
        var requests = 0;
        var time = new MutableTimeProvider(Now);
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return JsonResponse(requests == 1
                ? CreateJwks(("original", originalKey))
                : CreateJwks(("original", originalKey), ("rotated", rotatedKey)));
        }));
        using var validator = new CloudflareAccessTokenValidator(client, time);

        Assert.True((await validator.ValidateAsync(
            CreateToken(originalKey, "original", ValidClaims()), Issuer, Audience)).IsValid);
        var tooSoon = await validator.ValidateAsync(
            CreateToken(rotatedKey, "rotated", ValidClaims()), Issuer, Audience);
        Assert.Equal(CloudflareAccessValidationFailure.SigningKeyNotFound, tooSoon.Failure);
        Assert.Equal(1, requests);

        time.Advance(TimeSpan.FromMinutes(2));
        var afterBackoff = await validator.ValidateAsync(
            CreateToken(rotatedKey, "rotated", ValidClaims(expiresFrom: time.GetUtcNow())), Issuer, Audience);
        Assert.True(afterBackoff.IsValid);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Signing_key_fetch_failures_are_negative_cached_and_fail_closed()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));
        using var validator = new CloudflareAccessTokenValidator(client, new MutableTimeProvider(Now));
        using var key = RSA.Create(2048);
        var token = CreateToken(key, "unknown", ValidClaims());

        var first = await validator.ValidateAsync(token, Issuer, Audience);
        var second = await validator.ValidateAsync(token, Issuer, Audience);

        Assert.Equal(CloudflareAccessValidationFailure.SigningKeysUnavailable, first.Failure);
        Assert.Equal(CloudflareAccessValidationFailure.SigningKeysUnavailable, second.Failure);
        Assert.Equal(1, requests);
    }

    private static AccessClaims ValidClaims(DateTimeOffset? expiresFrom = null)
    {
        var reference = expiresFrom ?? Now;
        return new AccessClaims(
            Issuer,
            [Audience],
            "app",
            reference.AddMinutes(10).ToUnixTimeSeconds(),
            reference.AddMinutes(-1).ToUnixTimeSeconds(),
            reference.AddMinutes(-1).ToUnixTimeSeconds(),
            null);
    }

    private static string CreateToken(
        RSA signingKey,
        string keyId,
        AccessClaims claims,
        string algorithm = "RS256")
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = algorithm,
            kid = keyId,
            typ = "JWT"
        }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = claims.Issuer,
            aud = claims.Audiences,
            type = claims.Type,
            exp = claims.Expires,
            nbf = claims.NotBefore,
            iat = claims.IssuedAt,
            email = claims.Email
        }));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + payload);
        var signature = signingKey.SignData(
            signingInput,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Base64Url(signature);
    }

    private static string CreateJwks(params (string KeyId, RSA Key)[] keys)
    {
        var values = keys.Select(item =>
        {
            var parameters = item.Key.ExportParameters(includePrivateParameters: false);
            return new
            {
                kid = item.KeyId,
                kty = "RSA",
                alg = "RS256",
                use = "sig",
                e = Base64Url(parameters.Exponent!),
                n = Base64Url(parameters.Modulus!)
            };
        });
        return JsonSerializer.Serialize(new { keys = values });
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record AccessClaims(
        string Issuer,
        string[] Audiences,
        string Type,
        long Expires,
        long NotBefore,
        long IssuedAt,
        string? Email);

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        internal void Advance(TimeSpan duration) => _current = _current.Add(duration);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }
}
