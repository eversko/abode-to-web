using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AbodeToWeb.Infrastructure.Runtime;

public sealed record PortAvailability(bool Available, int Port, int? OwningProcessId, string? OwningProcessName, string Message);

public static class PortCollisionDetector
{
    public static PortAvailability CheckLoopback(int port)
    {
        if (port is < 1 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return new PortAvailability(true, port, null, null, $"Port {port} is available.");
        }
        catch (SocketException)
        {
            var owner = OperatingSystem.IsWindows() ? FindWindowsOwner(port) : (null, null);
            var detail = owner.Item1 is null
                ? $"Port {port} is already in use."
                : $"Port {port} is already in use by {owner.Item2 ?? "a process"} (PID {owner.Item1}).";
            return new PortAvailability(false, port, owner.Item1, owner.Item2, detail);
        }
        finally { listener?.Stop(); }
    }

    private static (int? ProcessId, string? ProcessName) FindWindowsOwner(int port)
    {
        OwnedTcpListener? listener;
        try
        {
            listener = new WindowsTcpListenerSnapshotProvider()
                .Capture()
                .FirstOrDefault(candidate => candidate.LocalPort == port);
        }
        catch (Exception exception) when (exception is Win32Exception
                                              or InvalidDataException
                                              or InvalidOperationException
                                              or IOException
                                              or ObjectDisposedException
                                              or PlatformNotSupportedException
                                              or OverflowException)
        {
            return (null, null);
        }

        if (listener is null) return (null, null);
        try
        {
            using var process = Process.GetProcessById(listener.OwningProcessId);
            return (listener.OwningProcessId, process.ProcessName);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or NotSupportedException
                                              or Win32Exception)
        {
            return (listener.OwningProcessId, null);
        }
    }
}
