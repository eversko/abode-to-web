using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AbodeToWeb.Infrastructure.Security;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSecretFilePermissions
{
    internal const string ServiceNameEnvironmentVariable = "ABODE_TO_WEB_SERVICE_NAME";

    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalServiceSid =
        new(WellKnownSidType.LocalServiceSid, null);
    private static readonly SecurityIdentifier NetworkServiceSid =
        new(WellKnownSidType.NetworkServiceSid, null);

    private readonly SecurityIdentifier _ownerSid;
    private readonly IReadOnlyDictionary<SecurityIdentifier, FileSystemRights> _fileRules;

    private WindowsSecretFilePermissions(
        SecurityIdentifier ownerSid,
        IReadOnlyDictionary<SecurityIdentifier, FileSystemRights> fileRules)
    {
        _ownerSid = ownerSid;
        _fileRules = fileRules;
    }

    internal static WindowsSecretFilePermissions Capture(string secretsRoot)
    {
        DirectorySecurity parentSecurity;
        try
        {
            parentSecurity = new DirectoryInfo(secretsRoot).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException)
        {
            throw PermissionFailure(exception);
        }

        var serviceSid = ResolveServiceSid(parentSecurity);
        if (serviceSid is not null)
        {
            return CreateForTrustedParent(parentSecurity, serviceSid);
        }

        var currentSid = WindowsIdentity.GetCurrent().User;
        if (currentSid is null || IsSharedServiceIdentity(currentSid))
        {
            throw PermissionFailure();
        }

        // Local development does not have a Windows service SID. Keep that workflow
        // usable, but protect the file explicitly for the developer, SYSTEM, and
        // Administrators. Shared built-in service accounts are never accepted here.
        var rules = new Dictionary<SecurityIdentifier, FileSystemRights>
        {
            [SystemSid] = FileSystemRights.FullControl,
            [AdministratorsSid] = FileSystemRights.FullControl,
            [currentSid] = FileSystemRights.FullControl
        };
        return new WindowsSecretFilePermissions(currentSid, rules);
    }

    internal static WindowsSecretFilePermissions CreateForTrustedParent(
        DirectorySecurity parentSecurity,
        SecurityIdentifier serviceSid)
    {
        if (!IsUniqueServiceSid(serviceSid)
            || IsSharedServiceIdentity(serviceSid)
            || !parentSecurity.AreAccessRulesProtected
            || parentSecurity.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !owner.Equals(AdministratorsSid))
        {
            throw PermissionFailure();
        }

        var expectedDirectoryRules = new Dictionary<SecurityIdentifier, FileSystemRights>
        {
            [SystemSid] = ExpectedDirectoryRights(SystemSid, FileSystemRights.FullControl),
            [AdministratorsSid] = ExpectedDirectoryRights(AdministratorsSid, FileSystemRights.FullControl),
            [serviceSid] = ExpectedDirectoryRights(serviceSid, FileSystemRights.Modify)
        };
        ValidateRules(parentSecurity, expectedDirectoryRules, directoryRules: true);

        var fileRules = new Dictionary<SecurityIdentifier, FileSystemRights>
        {
            [SystemSid] = ExpectedFileRights(SystemSid, FileSystemRights.FullControl),
            [AdministratorsSid] = ExpectedFileRights(AdministratorsSid, FileSystemRights.FullControl),
            [serviceSid] = ExpectedFileRights(serviceSid, FileSystemRights.Modify)
        };
        return new WindowsSecretFilePermissions(serviceSid, fileRules);
    }

    internal void Apply(string path)
    {
        try
        {
            var security = CreateFileSecurity();
            new FileInfo(path).SetAccessControl(security);
            Validate(path, requireExactOwner: true);
        }
        catch (SecretFilePermissionException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException
                                            or IdentityNotMappedException)
        {
            throw PermissionFailure(exception);
        }
    }

    internal void ValidateExisting(string path) => Validate(path, requireExactOwner: false);

    private void Validate(string path, bool requireExactOwner)
    {
        FileSecurity security;
        try
        {
            security = new FileInfo(path).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException)
        {
            throw PermissionFailure(exception);
        }

        if (!security.AreAccessRulesProtected
            || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || (requireExactOwner
                ? !owner.Equals(_ownerSid)
                : !owner.Equals(_ownerSid) && !owner.Equals(AdministratorsSid) && !owner.Equals(SystemSid)))
        {
            throw PermissionFailure();
        }
        ValidateRules(security, _fileRules, directoryRules: false);
    }

    internal FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(_ownerSid);
        foreach (var (sid, rights) in _fileRules)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
        }
        return security;
    }

    private static SecurityIdentifier? ResolveServiceSid(DirectorySecurity parentSecurity)
    {
        var configuredServiceName = Environment.GetEnvironmentVariable(ServiceNameEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredServiceName))
        {
            if (configuredServiceName.Length > 128
                || configuredServiceName.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
            {
                throw PermissionFailure();
            }

            try
            {
                var resolved = (SecurityIdentifier)new NTAccount("NT SERVICE", configuredServiceName)
                    .Translate(typeof(SecurityIdentifier));
                if (!IsUniqueServiceSid(resolved)) throw PermissionFailure();
                return resolved;
            }
            catch (IdentityNotMappedException exception)
            {
                throw PermissionFailure(exception);
            }
        }

        var parentServiceSids = GetRules(parentSecurity)
            .Select(rule => rule.IdentityReference)
            .OfType<SecurityIdentifier>()
            .Where(IsUniqueServiceSid)
            .Distinct()
            .ToArray();
        var tokenServiceSids = WindowsIdentity.GetCurrent().Groups
            ?.OfType<SecurityIdentifier>()
            .Where(IsUniqueServiceSid)
            .ToHashSet() ?? [];
        var matches = parentServiceSids.Where(tokenServiceSids.Contains).ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1 || parentServiceSids.Length > 0 || tokenServiceSids.Count > 0)
            throw PermissionFailure();
        return null;
    }

    private static void ValidateRules(
        FileSystemSecurity security,
        IReadOnlyDictionary<SecurityIdentifier, FileSystemRights> expected,
        bool directoryRules)
    {
        var rules = GetRules(security);
        if (rules.Length != expected.Count) throw PermissionFailure();

        var expectedInheritance = directoryRules
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid
                || !expected.TryGetValue(sid, out var rights)
                || rule.AccessControlType != AccessControlType.Allow
                || rule.IsInherited
                || rule.InheritanceFlags != expectedInheritance
                || rule.PropagationFlags != PropagationFlags.None
                || rule.FileSystemRights != rights)
            {
                throw PermissionFailure();
            }
        }
    }

    private static FileSystemAccessRule[] GetRules(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();

    private static FileSystemRights ExpectedDirectoryRights(SecurityIdentifier sid, FileSystemRights rights) =>
        new FileSystemAccessRule(
            sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow).FileSystemRights;

    private static FileSystemRights ExpectedFileRights(SecurityIdentifier sid, FileSystemRights rights) =>
        new FileSystemAccessRule(sid, rights, AccessControlType.Allow).FileSystemRights;

    private static bool IsUniqueServiceSid(SecurityIdentifier sid) =>
        sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal)
        && sid.Value.Split('-').Length == 9;

    private static bool IsSharedServiceIdentity(SecurityIdentifier sid) =>
        sid.Equals(LocalServiceSid) || sid.Equals(NetworkServiceSid) || sid.Equals(SystemSid);

    private static SecretFilePermissionException PermissionFailure(Exception? innerException = null) =>
        new(
            "AbodeToWeb refused to access application secrets because the secrets ACL is not restricted to SYSTEM, Administrators, and the AbodeToWeb service SID. Run repair-abode-to-web.ps1 -Apply.",
            innerException);
}

internal sealed class SecretFilePermissionException(string message, Exception? innerException = null)
    : UnauthorizedAccessException(message, innerException);
