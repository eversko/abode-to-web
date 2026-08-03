using System.Diagnostics;
using System.Runtime.InteropServices;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Core.Health;

namespace AbodeToWeb.Server.Services;

public sealed class SystemStatusService(
    TunnelDiagnosticsService tunnel,
    AbodeToWeb.Infrastructure.AbodeToWebPaths paths,
    ProjectQueryService projects)
{
    private readonly Lock _cpuLock = new();
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _quietMode;

    public bool QuietMode { get { lock (_cpuLock) return _quietMode; } set { lock (_cpuLock) _quietMode = value; } }

    public async Task<SystemStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        var projectSummaries = await projects.GetProjectsAsync(cancellationToken);
        var tunnelStatus = await tunnel.GetAsync(cancellationToken);
        var memory = GetMemory();
        var diskRoot = Path.GetPathRoot(paths.DataRoot) ?? "C:\\";
        var disk = new DriveInfo(diskRoot);
        var cloudflareComponent = tunnelStatus.State switch
        {
            TunnelConnectionState.Connected => new ComponentStatus(ComponentCondition.Healthy, "Connected"),
            TunnelConnectionState.NotInstalled or TunnelConnectionState.Stopped => new ComponentStatus(ComponentCondition.Offline, "Offline", tunnelStatus.SafeDiagnostic),
            TunnelConnectionState.Degraded => new ComponentStatus(ComponentCondition.Degraded, "Degraded", tunnelStatus.SafeDiagnostic),
            _ => new ComponentStatus(ComponentCondition.Unknown, "Unknown", tunnelStatus.SafeDiagnostic)
        };
        return new SystemStatus(DateTimeOffset.UtcNow,
            new ComponentStatus(ComponentCondition.Healthy, "Online"),
            new ComponentStatus(ComponentCondition.Healthy, "Healthy"),
            new ComponentStatus(ComponentCondition.Healthy, "Healthy"),
            cloudflareComponent,
            projectSummaries.Count(project => project.RuntimeState == ProjectRuntimeState.Running),
            projectSummaries.Count(project => project.Health == HealthState.Unhealthy),
            projects.UpdatesAvailable,
            SampleCpu(), memory.Used, memory.Total, disk.AvailableFreeSpace, disk.TotalSize, QuietMode);
    }

    private double SampleCpu()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var currentIdle = idle.ToUInt64();
        var currentKernel = kernel.ToUInt64();
        var currentUser = user.ToUInt64();
        lock (_cpuLock)
        {
            if (_lastKernel == 0)
            {
                _lastIdle = currentIdle; _lastKernel = currentKernel; _lastUser = currentUser;
                return 0;
            }
            var idleDelta = currentIdle - _lastIdle;
            var totalDelta = currentKernel - _lastKernel + currentUser - _lastUser;
            _lastIdle = currentIdle; _lastKernel = currentKernel; _lastUser = currentUser;
            return totalDelta == 0 ? 0 : Math.Round(Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100), 1);
        }
    }

    private static (long Used, long Total) GetMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            var info = GC.GetGCMemoryInfo();
            return (GC.GetTotalMemory(false), info.TotalAvailableMemoryBytes);
        }
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? ((long)(status.TotalPhysical - status.AvailablePhysical), (long)status.TotalPhysical)
            : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
        internal readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
