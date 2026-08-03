using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AbodeToWeb.Infrastructure.Runtime;

/// <summary>
/// Provides the small process surface AbodeToWeb needs while retaining the exact
/// Windows process handle returned at creation time. A restricted service may
/// not be allowed to reopen a process owned by the dedicated runtime identity,
/// and retaining this handle also avoids every PID-reuse race.
/// </summary>
public sealed class SupervisedProcess : IDisposable
{
    private readonly Process? _portableProcess;
    private SafeProcessHandle? _windowsHandle;
    private readonly DateTime _windowsStartTime;
    private int _disposed;

    internal SupervisedProcess(Process process, string? executablePath = null)
    {
        _portableProcess = process;
        Id = process.Id;
        ExecutablePath = executablePath;
    }

    internal SupervisedProcess(SafeProcessHandle processHandle, int processId, string executablePath)
    {
        _windowsHandle = processHandle;
        Id = processId;
        ExecutablePath = executablePath;
        _windowsStartTime = ReadStartTime(processHandle);
    }

    public int Id { get; }

    public string? ExecutablePath { get; }

    public DateTime StartTime
    {
        get
        {
            ThrowIfDisposed();
            return _portableProcess?.StartTime ?? _windowsStartTime;
        }
    }

    public bool HasExited
    {
        get
        {
            ThrowIfDisposed();
            if (_portableProcess is not null) return _portableProcess.HasExited;
            var result = NativeMethods.WaitForSingleObject(WindowsHandle, 0);
            return result switch
            {
                NativeMethods.WaitObject0 => true,
                NativeMethods.WaitTimeout => false,
                _ => throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not inspect the application process state.")
            };
        }
    }

    public int ExitCode
    {
        get
        {
            ThrowIfDisposed();
            if (_portableProcess is not null) return _portableProcess.ExitCode;
            if (!HasExited) throw new InvalidOperationException("The application process has not exited.");
            if (!NativeMethods.GetExitCodeProcess(WindowsHandle, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the application exit code.");
            return unchecked((int)exitCode);
        }
    }

    public bool CloseMainWindow()
    {
        ThrowIfDisposed();
        // AbodeToWeb creates Windows applications without a visible interactive
        // window. Closing stdin is the graceful signal; the Job is the fallback.
        return _portableProcess?.CloseMainWindow() ?? false;
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_portableProcess is not null)
        {
            await _portableProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (HasExited) return;
        using var waitHandle = new OwnedProcessWaitHandle(DuplicateWaitHandle(WindowsHandle));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        var registeredWait = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);
        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            using var unregisterComplete = new ManualResetEvent(false);
            if (registeredWait.Unregister(unregisterComplete)) unregisterComplete.WaitOne();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _portableProcess?.Dispose();
        _windowsHandle?.Dispose();
        _windowsHandle = null;
    }

    private SafeProcessHandle WindowsHandle =>
        _windowsHandle ?? throw new ObjectDisposedException(nameof(SupervisedProcess));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static DateTime ReadStartTime(SafeProcessHandle processHandle)
    {
        if (!NativeMethods.GetProcessTimes(processHandle, out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the application start time.");
        var fileTime = ((long)created.HighDateTime << 32) | created.LowDateTime;
        return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
    }

    private static SafeWaitHandle DuplicateWaitHandle(SafeProcessHandle processHandle)
    {
        var currentProcess = NativeMethods.GetCurrentProcess();
        if (!NativeMethods.DuplicateHandle(currentProcess, processHandle, currentProcess, out var duplicate,
                NativeMethods.Synchronize, false, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not duplicate the application wait handle.");
        return duplicate;
    }

    private sealed class OwnedProcessWaitHandle : WaitHandle
    {
        internal OwnedProcessWaitHandle(SafeWaitHandle processHandle) => SafeWaitHandle = processHandle;
    }

    private static class NativeMethods
    {
        internal const uint WaitObject0 = 0;
        internal const uint WaitTimeout = 258;
        internal const uint Synchronize = 0x00100000;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(
            SafeProcessHandle process,
            out FileTime creationTime,
            out FileTime exitTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            IntPtr sourceProcess,
            SafeProcessHandle sourceHandle,
            IntPtr targetProcess,
            out SafeWaitHandle targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            internal uint LowDateTime;
            internal uint HighDateTime;
        }
    }
}
