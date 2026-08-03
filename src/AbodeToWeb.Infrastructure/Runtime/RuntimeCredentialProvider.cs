using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AbodeToWeb.Infrastructure.Operations;

namespace AbodeToWeb.Infrastructure.Runtime;

/// <summary>
/// Signals a safe, actionable failure while loading or using the dedicated runtime identity.
/// Messages must never contain the password or the protected credential payload.
/// </summary>
public sealed class RuntimeCredentialException : Exception, ISafeOperationError
{
    public RuntimeCredentialException(string message) : base(message) { }

    public RuntimeCredentialException(string message, Exception innerException) : base(message, innerException) { }

    public string Code => "runtime.identity_unavailable";
    public string SafeMessage => Message;
}

[SupportedOSPlatform("windows")]
internal sealed class RuntimeCredential : IDisposable
{
    private char[]? _password;

    internal RuntimeCredential(string username, string domain, SecurityIdentifier accountSid, char[] password)
    {
        Username = username;
        Domain = domain;
        AccountSid = accountSid;
        _password = password;
    }

    internal string Username { get; }
    internal string Domain { get; }
    internal SecurityIdentifier AccountSid { get; }

    internal char[] GetPassword()
    {
        ObjectDisposedException.ThrowIf(_password is null, this);
        return _password;
    }

    public void Dispose()
    {
        if (_password is null) return;
        Array.Clear(_password);
        _password = null;
    }
}

[SupportedOSPlatform("windows")]
internal static class RuntimeCredentialProvider
{
    internal const string CredentialPathEnvironmentVariable = "ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH";
    internal const string ProtectionName = "dpapi-local-machine-v1";
    private const int MaximumEnvelopeBytes = 32 * 1024;
    private const int MaximumProtectedPasswordBytes = 16 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AbodeToWeb.RuntimeCredential.v1");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ExpectedProperties = new(StringComparer.Ordinal)
    {
        "version", "protection", "username", "domain", "protectedPassword"
    };

    internal static RuntimeCredential Load()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The AbodeToWeb runtime credential provider requires Windows.");

        var configuredPath = Environment.GetEnvironmentVariable(CredentialPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new RuntimeCredentialException(
                $"The dedicated runtime identity is not configured. Set {CredentialPathEnvironmentVariable} to the protected credential file and restart AbodeToWeb.");
        if (!Path.IsPathFullyQualified(configuredPath) || configuredPath.Contains('\0', StringComparison.Ordinal))
            throw new RuntimeCredentialException(
                $"{CredentialPathEnvironmentVariable} must contain a fully qualified path to the protected credential file.");

        string path;
        try
        {
            path = Path.GetFullPath(configuredPath);
            EnsurePathHasNoReparsePoints(path);
            EnsureCredentialAclIsRestricted(path);
        }
        catch (RuntimeCredentialException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            throw new RuntimeCredentialException("AbodeToWeb could not securely access the dedicated runtime credential file.", exception);
        }

        byte[] envelopeBytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumEnvelopeBytes)
                throw new RuntimeCredentialException("The dedicated runtime credential file has an invalid size.");
            envelopeBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(envelopeBytes);
        }
        catch (RuntimeCredentialException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeCredentialException("AbodeToWeb could not read the dedicated runtime credential file.", exception);
        }

        try
        {
            return ParseAndUnprotect(envelopeBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeBytes);
        }
    }

    private static RuntimeCredential ParseAndUnprotect(byte[] envelopeBytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(envelopeBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
        }
        catch (JsonException exception)
        {
            throw new RuntimeCredentialException("The dedicated runtime credential file is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidEnvelope();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!ExpectedProperties.Contains(property.Name) || !seen.Add(property.Name))
                    throw InvalidEnvelope();
            }
            if (seen.Count != ExpectedProperties.Count)
                throw InvalidEnvelope();

            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != 1)
                throw InvalidEnvelope();
            if (!TryReadRequiredString(root, "protection", 64, out var protection)
                || !string.Equals(protection, ProtectionName, StringComparison.Ordinal))
                throw InvalidEnvelope();
            if (!TryReadRequiredString(root, "username", 104, out var username)
                || !IsSafeAccountComponent(username, allowDotOnly: false))
                throw InvalidEnvelope();
            if (!TryReadRequiredString(root, "domain", 255, out var domain)
                || !IsSafeAccountComponent(domain, allowDotOnly: true))
                throw InvalidEnvelope();
            if (!TryReadRequiredString(root, "protectedPassword", MaximumEnvelopeBytes, out var protectedPasswordText))
                throw InvalidEnvelope();

            byte[] protectedPassword;
            try { protectedPassword = Convert.FromBase64String(protectedPasswordText); }
            catch (FormatException exception) { throw new RuntimeCredentialException("The dedicated runtime credential file has an invalid protected password.", exception); }
            if (protectedPassword.Length is <= 0 or > MaximumProtectedPasswordBytes)
            {
                CryptographicOperations.ZeroMemory(protectedPassword);
                throw InvalidEnvelope();
            }

            byte[] passwordBytes;
            try
            {
                passwordBytes = ProtectedData.Unprotect(protectedPassword, Entropy, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException exception)
            {
                throw new RuntimeCredentialException(
                    "Windows could not decrypt the dedicated runtime credential. Recreate it on this computer and restart AbodeToWeb.", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedPassword);
            }

            try
            {
                if (passwordBytes.Length is < 32 or > 1024)
                    throw InvalidEnvelope();
                char[]? password = null;
                try
                {
                    password = new char[StrictUtf8.GetCharCount(passwordBytes)];
                    StrictUtf8.GetChars(passwordBytes, password);
                    if (password.Length == 0 || password.Any(character => character == '\0' || char.IsControl(character)))
                        throw InvalidEnvelope();

                    var sid = ResolveAccountSid(username, domain);
                    var currentSid = WindowsIdentity.GetCurrent().User;
                    if (currentSid is not null && currentSid.Equals(sid))
                        throw new RuntimeCredentialException(
                            "The runtime identity must be a dedicated Windows account distinct from the AbodeToWeb service identity.");
                    var credential = new RuntimeCredential(username, domain, sid, password);
                    password = null;
                    return credential;
                }
                finally
                {
                    if (password is not null) Array.Clear(password);
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new RuntimeCredentialException("The dedicated runtime credential password has an invalid encoding.", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
    }

    private static SecurityIdentifier ResolveAccountSid(string username, string domain)
    {
        var resolvedDomain = domain == "." ? Environment.MachineName : domain;
        try
        {
            return (SecurityIdentifier)new NTAccount(resolvedDomain, username).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException exception)
        {
            throw new RuntimeCredentialException(
                "The configured dedicated runtime account does not exist on this computer.", exception);
        }
    }

    private static bool TryReadRequiredString(JsonElement root, string name, int maximumLength, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return value.Length is > 0 && value.Length <= maximumLength
            && !value.Any(character => character == '\0' || char.IsControl(character));
    }

    private static bool IsSafeAccountComponent(string value, bool allowDotOnly)
    {
        if (allowDotOnly && value == ".") return true;
        if (value is "." or ".." || value.EndsWith('.')) return false;
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static void EnsureCredentialAclIsRestricted(string path)
    {
        if (!File.Exists(path))
            throw new RuntimeCredentialException("The dedicated runtime credential file does not exist.");

        var allowed = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.LocalServiceSid, null)
        };
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null) allowed.Add(current);

        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected)
            throw new RuntimeCredentialException(
                "The dedicated runtime credential file inherits permissions. Restrict it to AbodeToWeb, Administrators, and SYSTEM.");
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || (!allowed.Contains(owner) && !owner.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal)))
            throw new RuntimeCredentialException(
                "The dedicated runtime credential file owner is not trusted. Repair its permissions before starting AbodeToWeb.");

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid
                || (!allowed.Contains(sid) && !sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal)))
                throw new RuntimeCredentialException(
                    "The dedicated runtime credential file permissions are too broad. Restrict it to AbodeToWeb, Administrators, and SYSTEM.");
        }
    }

    private static void EnsurePathHasNoReparsePoints(string path)
    {
        var root = Path.GetPathRoot(path) ?? throw new RuntimeCredentialException("The runtime credential path has no volume root.");
        var current = root;
        foreach (var segment in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new RuntimeCredentialException("The dedicated runtime credential file does not exist.");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new RuntimeCredentialException("The dedicated runtime credential path cannot traverse a junction or symbolic link.");
        }
    }

    private static RuntimeCredentialException InvalidEnvelope() =>
        new("The dedicated runtime credential file does not match the supported version 1 schema.");
}
