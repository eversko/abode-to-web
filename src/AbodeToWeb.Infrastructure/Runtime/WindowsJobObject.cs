using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AbodeToWeb.Infrastructure.Runtime;

public sealed class WindowsJobObject : IDisposable
{
    private const int InitialProcessIdCapacity = 64;
    private const int MaximumProcessIdCapacity = 16_384;
    private const int ProcessIdListHeaderSize = sizeof(uint) * 2;
    private readonly SafeJobHandle? _handle;

    public WindowsJobObject(string name, bool killOnClose = true)
    {
        Name = NormalizeName(name);
        if (!OperatingSystem.IsWindows()) return;
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, Name);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Windows Job Object.");

        if (killOnClose)
        {
            var information = new NativeMethods.JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
                {
                    LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose
                }
            };
            var size = Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, pointer, false);
                if (!NativeMethods.SetInformationJobObject(_handle, 9, pointer, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the Windows Job Object.");
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
    }

    public string Name { get; }

    public void Assign(SafeProcessHandle processHandle)
    {
        if (!OperatingSystem.IsWindows() || _handle is null) return;
        if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the application to its Windows Job Object.");
    }

    public void Terminate(uint exitCode = 1)
    {
        if (!OperatingSystem.IsWindows() || _handle is null) return;
        if (!NativeMethods.TerminateJobObject(_handle, exitCode))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 6) throw new Win32Exception(error, "Could not terminate the application process tree.");
        }
    }

    public IReadOnlySet<int> GetProcessIds()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Job Object inspection requires Windows.");
        ObjectDisposedException.ThrowIf(_handle is null || _handle.IsClosed || _handle.IsInvalid, this);

        var capacity = InitialProcessIdCapacity;
        while (capacity <= MaximumProcessIdCapacity)
        {
            var bufferSize = checked(ProcessIdListHeaderSize + (capacity * IntPtr.Size));
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                for (var offset = 0; offset < ProcessIdListHeaderSize; offset += sizeof(int))
                    Marshal.WriteInt32(buffer, offset, 0);

                if (!NativeMethods.QueryInformationJobObject(
                        _handle,
                        NativeMethods.JobObjectBasicProcessIdList,
                        buffer,
                        (uint)bufferSize,
                        out var returnedLength))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is NativeMethods.ErrorMoreData or NativeMethods.ErrorInsufficientBuffer)
                    {
                        capacity = checked(capacity * 2);
                        continue;
                    }

                    throw new Win32Exception(error, "Could not inspect the Windows Job Object process tree.");
                }

                if (returnedLength < ProcessIdListHeaderSize || returnedLength > bufferSize)
                    throw new InvalidDataException("Windows returned an invalid Job Object process list length.");

                var assignedCount = unchecked((uint)Marshal.ReadInt32(buffer));
                var listedCount = unchecked((uint)Marshal.ReadInt32(buffer, sizeof(uint)));
                if (assignedCount > MaximumProcessIdCapacity || listedCount > MaximumProcessIdCapacity)
                    throw new InvalidDataException("The Windows Job Object process tree exceeded the inspection limit.");
                if (listedCount > capacity)
                {
                    capacity = checked(capacity * 2);
                    continue;
                }
                if (listedCount != assignedCount)
                    throw new InvalidDataException("Windows returned an incomplete Job Object process list.");

                var requiredLength = checked(ProcessIdListHeaderSize + ((long)listedCount * IntPtr.Size));
                if (requiredLength > returnedLength)
                    throw new InvalidDataException("Windows returned a truncated Job Object process list.");

                var processIds = new HashSet<int>();
                for (var index = 0; index < listedCount; index++)
                {
                    var processId = Marshal.ReadIntPtr(
                        buffer,
                        checked(ProcessIdListHeaderSize + ((int)index * IntPtr.Size))).ToInt64();
                    if (processId is <= 0 or > int.MaxValue)
                        throw new InvalidDataException("Windows returned an invalid Job Object process identifier.");
                    if (!processIds.Add((int)processId))
                        throw new InvalidDataException("Windows returned a duplicate Job Object process identifier.");
                }

                return processIds;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidDataException("The Windows Job Object process tree exceeded the inspection limit.");
    }

    public void Dispose() => _handle?.Dispose();

    private static string NormalizeName(string name)
    {
        var safe = new string(name.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
        if (safe.Length > 100) safe = safe[..100];
        return "AbodeToWeb-" + safe;
    }

    internal static class NativeMethods
    {
        internal const int JobObjectBasicProcessIdList = 3;
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
        internal const int ErrorInsufficientBuffer = 122;
        internal const int ErrorMoreData = 234;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, IntPtr information, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength,
            out uint returnLength);

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSetSize;
            internal nuint MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            internal JobObjectBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal nuint ProcessMemoryLimit;
            internal nuint JobMemoryLimit;
            internal nuint PeakProcessMemoryUsed;
            internal nuint PeakJobMemoryUsed;
        }
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeJobHandle() : base(true) { }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
