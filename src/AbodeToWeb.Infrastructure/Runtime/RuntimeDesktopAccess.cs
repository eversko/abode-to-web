using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AbodeToWeb.Infrastructure.Runtime;

/// <summary>
/// Allows the dedicated runtime identity to connect only to AbodeToWeb's
/// non-interactive service window station and desktop. CreateProcessWithLogonW
/// otherwise inherits objects whose DACL permits the service logon SID but not
/// the different application identity, causing user32-dependent executables to
/// terminate during DLL initialization.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RuntimeDesktopAccess
{
    private const int DaclSecurityInformation = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumUserObjectNameBytes = 4 * 1024;
    private const int UoiName = 2;
    // The runtime connects to the service desktop only to initialize its own
    // user32-dependent process. It must never administer the service desktop's
    // DACL or owner, terminate Windows, or install hooks into the service UI.
    internal const int WindowStationRuntimeRights =
        0x0002 // WINSTA_READATTRIBUTES
        | 0x0008 // WINSTA_CREATEDESKTOP
        | 0x0020; // WINSTA_ACCESSGLOBALATOMS
    internal const int DesktopRuntimeRights =
        0x0001 // DESKTOP_READOBJECTS
        | 0x0002 // DESKTOP_CREATEWINDOW
        | 0x0004 // DESKTOP_CREATEMENU
        | 0x0040 // DESKTOP_ENUMERATE
        | 0x0080; // DESKTOP_WRITEOBJECTS
    private static readonly Lock Sync = new();

    internal static string EnsureAccess(SecurityIdentifier runtimeSid)
    {
        ArgumentNullException.ThrowIfNull(runtimeSid);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Runtime desktop isolation requires Windows.");

        var station = NativeMethods.GetProcessWindowStation();
        var desktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
        if (station == IntPtr.Zero || desktop == IntPtr.Zero)
            throw new RuntimeCredentialException("AbodeToWeb could not identify its non-interactive Windows service desktop.");

        try
        {
            var stationName = ReadObjectName(station, "window station");
            var desktopName = ReadObjectName(desktop, "desktop");
            lock (Sync)
            {
                // Revalidate on every infrequent launch so an unexpected DACL
                // change cannot be hidden behind an in-memory success cache.
                GrantAccess(station, runtimeSid, WindowStationRuntimeRights, "window station");
                GrantAccess(desktop, runtimeSid, DesktopRuntimeRights, "desktop");
            }
            return $"{stationName}\\{desktopName}";
        }
        catch (RuntimeCredentialException) { throw; }
        catch (Exception exception) when (exception is Win32Exception or ArgumentException
                                            or System.Security.SecurityException)
        {
            throw new RuntimeCredentialException(
                "AbodeToWeb could not grant the dedicated runtime identity access to its non-interactive service desktop.", exception);
        }
    }

    private static string ReadObjectName(IntPtr handle, string kind)
    {
        _ = NativeMethods.GetUserObjectInformation(handle, UoiName, null, 0, out var requiredBytes);
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer
            || requiredBytes <= sizeof(char)
            || requiredBytes > MaximumUserObjectNameBytes
            || requiredBytes % sizeof(char) != 0)
            throw new Win32Exception(error, $"Windows could not size the AbodeToWeb service {kind} name.");

        var name = new char[requiredBytes / sizeof(char)];
        if (!NativeMethods.GetUserObjectInformation(handle, UoiName, name, requiredBytes, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not read the AbodeToWeb service {kind} name.");
        var terminator = Array.IndexOf(name, '\0');
        if (terminator <= 0)
            throw new RuntimeCredentialException($"The AbodeToWeb service {kind} name is invalid.");
        var value = new string(name, 0, terminator);
        if (value.Length == 0 || value.Any(character => char.IsControl(character) || character == '\\'))
            throw new RuntimeCredentialException($"The AbodeToWeb service {kind} name is invalid.");
        return value;
    }

    private static void GrantAccess(IntPtr handle, SecurityIdentifier sid, int requiredAccess, string kind)
    {
        var requested = DaclSecurityInformation;
        _ = NativeMethods.GetUserObjectSecurity(handle, ref requested, null, 0, out var requiredBytes);
        var error = Marshal.GetLastWin32Error();
        if (requiredBytes == 0 || error != ErrorInsufficientBuffer)
            throw new Win32Exception(error, $"Windows could not size the AbodeToWeb service {kind} security descriptor.");

        var currentBytes = new byte[requiredBytes];
        if (!NativeMethods.GetUserObjectSecurity(handle, ref requested, currentBytes,
                checked((uint)currentBytes.Length), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not read the AbodeToWeb service {kind} permissions.");

        var current = new RawSecurityDescriptor(currentBytes, 0);
        var dacl = current.DiscretionaryAcl
            ?? throw new RuntimeCredentialException($"The AbodeToWeb service {kind} has an unrestricted permission list and cannot be shared safely.");
        foreach (var ace in dacl.OfType<CommonAce>())
        {
            if (ace.SecurityIdentifier.Equals(sid)
                && ace.AceQualifier == AceQualifier.AccessDenied
                && (ace.AccessMask & requiredAccess) != 0)
                throw new RuntimeCredentialException($"The AbodeToWeb service {kind} explicitly denies the dedicated runtime identity.");
            if (ace.SecurityIdentifier.Equals(sid)
                && ace.AceQualifier == AceQualifier.AccessAllowed
                && (ace.AccessMask & requiredAccess) == requiredAccess)
                return;
        }

        var insertionIndex = Enumerable.Range(0, dacl.Count)
            .FirstOrDefault(index => (dacl[index].AceFlags & AceFlags.Inherited) != 0, dacl.Count);
        dacl.InsertAce(insertionIndex, new CommonAce(
            AceFlags.None, AceQualifier.AccessAllowed, requiredAccess, sid, isCallback: false, opaque: null));
        var updated = new RawSecurityDescriptor(
            current.ControlFlags | ControlFlags.DiscretionaryAclPresent,
            current.Owner,
            current.Group,
            current.SystemAcl,
            dacl);
        var updatedBytes = new byte[updated.BinaryLength];
        updated.GetBinaryForm(updatedBytes, 0);
        if (!NativeMethods.SetUserObjectSecurity(handle, ref requested, updatedBytes))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not update the AbodeToWeb service {kind} permissions.");
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformation(
            IntPtr handle,
            int index,
            [Out] char[]? information,
            int length,
            out int needed);

        [DllImport("user32.dll", EntryPoint = "GetUserObjectSecurity", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectSecurity(
            IntPtr handle,
            ref int requestedInformation,
            byte[]? securityDescriptor,
            uint length,
            out uint needed);

        [DllImport("user32.dll", EntryPoint = "SetUserObjectSecurity", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetUserObjectSecurity(
            IntPtr handle,
            ref int requestedInformation,
            byte[] securityDescriptor);
    }
}
