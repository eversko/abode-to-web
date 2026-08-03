using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AbodeToWeb.Infrastructure.Runtime;

[SupportedOSPlatform("windows")]
internal static class RuntimeProcessIsolation
{
    private const FileSystemRights RuntimeDirectoryRights =
        FileSystemRights.Modify | FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    internal static void Prepare(ProcessStartInfo startInfo, string isolationKey, RuntimeCredential credential)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(credential);
        ValidateIsolationKey(isolationKey);

        var workingDirectory = Path.GetFullPath(startInfo.WorkingDirectory);
        EnsureNoReparsePoints(workingDirectory);

        foreach (var key in new[] { "TEMP", "TMP", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA" })
        {
            if (!startInfo.Environment.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new RuntimeCredentialException($"The isolated runtime environment is missing {key}.");
            if (!Path.IsPathFullyQualified(value))
                throw new RuntimeCredentialException($"The isolated runtime environment variable {key} must contain a fully qualified path.");

            var directory = Path.GetFullPath(value);
            if (!IsWithin(directory, workingDirectory))
                throw new RuntimeCredentialException($"The isolated runtime environment variable {key} must remain inside the deployment directory.");
            EnsureExistingParentsHaveNoReparsePoints(directory);
            Directory.CreateDirectory(directory);
        }

        GrantWorkingDirectoryAccess(workingDirectory, credential.AccountSid);
    }

    internal static void GrantDirectoryAccess(string path, string isolationKey)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Dedicated AbodeToWeb runtime identities require Windows.");
        ValidateIsolationKey(isolationKey);
        using var credential = RuntimeCredentialProvider.Load();
        EnsureNoReparsePoints(path);
        // The installer owns runtime-account ACL grants. Mutating ACLs here would require WRITE_DAC in the
        // control-plane service and could permanently broaden access to a user's source repository.
    }

    internal static void RevokeDirectoryAccess(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Dedicated AbodeToWeb runtime identities require Windows.");
        if (!Directory.Exists(path)) return;

        try
        {
            EnsureNoReparsePoints(path);
            using var credential = RuntimeCredentialProvider.Load();
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl(AccessControlSections.Access);
            var changed = false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference is not SecurityIdentifier sid || !sid.Equals(credential.AccountSid)
                    || rule.AccessControlType != AccessControlType.Allow
                    || rule.FileSystemRights != RuntimeDirectoryRights
                    || rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
                    || rule.PropagationFlags != PropagationFlags.None)
                    continue;

                security.RemoveAccessRuleSpecific(rule);
                changed = true;
            }
            if (changed) directory.SetAccessControl(security);
        }
        catch (DirectoryNotFoundException) { }
        catch (RuntimeCredentialException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException or IdentityNotMappedException)
        {
            throw new RuntimeCredentialException(
                "AbodeToWeb could not revoke the dedicated runtime identity's deployment-directory access. Run AbodeToWeb repair.", exception);
        }
    }

    private static void ValidateIsolationKey(string isolationKey)
    {
        if (string.IsNullOrWhiteSpace(isolationKey) || isolationKey.Length > 128
            || isolationKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Process isolation key is invalid.", nameof(isolationKey));
    }

    private static void GrantWorkingDirectoryAccess(string path, SecurityIdentifier accountSid)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl(AccessControlSections.Access);
            var expectedInheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var hasExpectedRule = false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference is not SecurityIdentifier sid || !sid.Equals(accountSid)
                    || rule.AccessControlType != AccessControlType.Allow)
                    continue;

                if (rule.IsInherited
                    || rule.FileSystemRights != RuntimeDirectoryRights
                    || rule.InheritanceFlags != expectedInheritance
                    || rule.PropagationFlags != PropagationFlags.None)
                    throw new RuntimeCredentialException(
                        "The deployment directory has an unexpected runtime-account permission. Run AbodeToWeb repair before starting the application.");
                hasExpectedRule = true;
            }

            if (hasExpectedRule) return;
            security.AddAccessRule(new FileSystemAccessRule(accountSid, RuntimeDirectoryRights,
                expectedInheritance, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        catch (RuntimeCredentialException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException or IdentityNotMappedException)
        {
            throw new RuntimeCredentialException(
                "AbodeToWeb could not grant the dedicated runtime identity access to this deployment directory. Run AbodeToWeb repair.", exception);
        }
    }

    private static bool IsWithin(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureExistingParentsHaveNoReparsePoints(string path)
    {
        var existing = path;
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent)) break;
            existing = parent;
        }
        EnsureNoReparsePoints(existing);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException("Runtime path does not have a volume root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                throw new DirectoryNotFoundException("A required runtime path does not exist.");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runtime paths cannot traverse junctions or symbolic links.");
        }
    }
}
