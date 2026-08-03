using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AbodeToWeb.Infrastructure.Runtime;

public sealed record OwnedTcpListener(
    IPAddress LocalAddress,
    int LocalPort,
    int OwningProcessId);

public sealed record ManagedProcessTreeSnapshot(
    int RootProcessId,
    IReadOnlySet<int> ProcessIds);

public interface ITcpListenerSnapshotProvider
{
    IReadOnlyList<OwnedTcpListener> Capture();
}

public interface IManagedProcessTreeSnapshotProvider
{
    ValueTask<ManagedProcessTreeSnapshot?> CaptureProcessTreeAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}

public sealed record HostedEndpointEvidence(
    string InstanceId,
    int Port,
    int RootProcessId,
    IReadOnlySet<int> ManagedProcessIds,
    IReadOnlyList<OwnedTcpListener> ExpectedPortListeners,
    IReadOnlyList<OwnedTcpListener> ManagedListeners);

public sealed record HostedEndpointVerification(
    bool IsVerified,
    string Code,
    string SafeMessage,
    HostedEndpointEvidence? Evidence = null);

public static class HostedEndpointVerificationCodes
{
    public const string Verified = "endpoint.verified";
    public const string UnsupportedPlatform = "endpoint.unsupported_platform";
    public const string InspectionFailed = "endpoint.inspection_failed";
    public const string ProcessTreeUnavailable = "endpoint.process_tree_unavailable";
    public const string ListenerMissing = "endpoint.listener_missing";
    public const string ListenerNotLoopback = "endpoint.listener_not_loopback";
    public const string ListenerNotOwned = "endpoint.listener_not_owned";
    public const string ManagedListenerNotLoopback = "endpoint.managed_listener_not_loopback";
    public const string FinalProbeFailed = "endpoint.final_probe_failed";
    public const string EvidenceChanged = "endpoint.evidence_changed";
}

public sealed class HostedEndpointVerifier(
    ITcpListenerSnapshotProvider listeners,
    IManagedProcessTreeSnapshotProvider processTrees)
{
    public async Task<HostedEndpointVerification> VerifyAsync(
        string instanceId,
        int port,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(instanceId, port);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var processTree = await processTrees.CaptureProcessTreeAsync(instanceId, cancellationToken);
            var processIds = processTree?.ProcessIds;
            if (processTree is null
                || processIds is null
                || processTree.RootProcessId <= 0
                || processIds.Count == 0
                || !processIds.Contains(processTree.RootProcessId)
                || processIds.Any(processId => processId <= 0))
            {
                return Failure(
                    HostedEndpointVerificationCodes.ProcessTreeUnavailable,
                    "AbodeToWeb could not prove the managed application process tree.");
            }

            var snapshot = listeners.Capture()
                ?? throw new InvalidDataException("The TCP listener snapshot was unavailable.");
            if (snapshot.Any(listener => listener is null
                                         || listener.LocalPort is < 1 or > 65_535
                                         || listener.OwningProcessId <= 0
                                         || listener.LocalAddress is null))
            {
                return Failure(
                    HostedEndpointVerificationCodes.InspectionFailed,
                    "Windows returned invalid TCP listener information.");
            }

            var expected = snapshot
                .Where(listener => listener.LocalPort == port)
                .OrderBy(ListenerKey, StringComparer.Ordinal)
                .ToArray();
            if (expected.Length == 0)
            {
                return Failure(
                    HostedEndpointVerificationCodes.ListenerMissing,
                    "The managed application is not listening on its expected port.");
            }

            if (expected.Any(listener => !IsLoopback(listener.LocalAddress)))
            {
                return Failure(
                    HostedEndpointVerificationCodes.ListenerNotLoopback,
                    "The expected application port is not restricted to loopback.");
            }

            if (expected.Any(listener => !processIds.Contains(listener.OwningProcessId)))
            {
                return Failure(
                    HostedEndpointVerificationCodes.ListenerNotOwned,
                    "The expected application port is not owned by the managed process tree.");
            }

            var managed = snapshot
                .Where(listener => processIds.Contains(listener.OwningProcessId))
                .OrderBy(ListenerKey, StringComparer.Ordinal)
                .ToArray();
            if (managed.Any(listener => !IsLoopback(listener.LocalAddress)))
            {
                return Failure(
                    HostedEndpointVerificationCodes.ManagedListenerNotLoopback,
                    "The managed process tree opened a TCP listener outside loopback.");
            }

            var evidence = new HostedEndpointEvidence(
                instanceId,
                port,
                processTree.RootProcessId,
                processIds.ToHashSet(),
                expected,
                managed);
            return new HostedEndpointVerification(
                true,
                HostedEndpointVerificationCodes.Verified,
                "The expected loopback endpoint is owned by the managed process tree.",
                evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(
                HostedEndpointVerificationCodes.InspectionFailed,
                "Windows could not prove TCP listener ownership for the managed application.");
        }
        catch (PlatformNotSupportedException)
        {
            return Failure(
                HostedEndpointVerificationCodes.UnsupportedPlatform,
                "This platform cannot prove TCP listener ownership for managed applications.");
        }
        catch (Exception exception) when (IsUnsupportedInspectionFailure(exception))
        {
            return Failure(
                HostedEndpointVerificationCodes.UnsupportedPlatform,
                "This platform cannot prove TCP listener ownership for managed applications.");
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return Failure(
                HostedEndpointVerificationCodes.InspectionFailed,
                "Windows could not prove TCP listener ownership for the managed application.");
        }
    }

    public async Task<HostedEndpointVerification> VerifyStableAsync(
        string instanceId,
        int port,
        Func<CancellationToken, Task<bool>> finalProbe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalProbe);
        var before = await VerifyAsync(instanceId, port, cancellationToken);
        if (!before.IsVerified) return before;

        bool probeSucceeded;
        try
        {
            probeSucceeded = await finalProbe(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            probeSucceeded = false;
        }
        catch (Exception exception) when (IsProbeFailure(exception))
        {
            probeSucceeded = false;
        }

        if (!probeSucceeded)
        {
            return Failure(
                HostedEndpointVerificationCodes.FinalProbeFailed,
                "The managed endpoint did not pass its final health probe.");
        }

        var after = await VerifyAsync(instanceId, port, cancellationToken);
        if (!after.IsVerified) return after;
        if (!EvidenceMatches(before.Evidence!, after.Evidence!))
        {
            return Failure(
                HostedEndpointVerificationCodes.EvidenceChanged,
                "TCP listener ownership changed during the final health probe.");
        }

        return after;
    }

    private static HostedEndpointVerification Failure(string code, string safeMessage) =>
        new(false, code, safeMessage);

    private static void ValidateArguments(string instanceId, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (port is < 1 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
    }

    private static bool EvidenceMatches(HostedEndpointEvidence before, HostedEndpointEvidence after) =>
        before.InstanceId.Equals(after.InstanceId, StringComparison.OrdinalIgnoreCase)
        && before.Port == after.Port
        && before.RootProcessId == after.RootProcessId
        && before.ManagedProcessIds.Order().SequenceEqual(after.ManagedProcessIds.Order())
        && before.ExpectedPortListeners.Select(ListenerKey).SequenceEqual(
            after.ExpectedPortListeners.Select(ListenerKey), StringComparer.Ordinal)
        && before.ManagedListeners.Select(ListenerKey).SequenceEqual(
            after.ManagedListeners.Select(ListenerKey), StringComparer.Ordinal);

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address)
        || (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));

    private static string ListenerKey(OwnedTcpListener listener)
    {
        var scope = listener.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? listener.LocalAddress.ScopeId
            : 0;
        return $"{(int)listener.LocalAddress.AddressFamily}:{Convert.ToHexString(listener.LocalAddress.GetAddressBytes())}:{scope}:{listener.LocalPort}:{listener.OwningProcessId}";
    }

    private static bool IsInspectionFailure(Exception exception) =>
        exception is Win32Exception
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or MarshalDirectiveException
            or ObjectDisposedException
            or OverflowException;

    private static bool IsUnsupportedInspectionFailure(Exception exception) =>
        exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException;

    private static bool IsProbeFailure(Exception exception) =>
        exception is HttpRequestException
            or IOException
            or SocketException
            or TimeoutException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or Win32Exception;
}

public sealed class WindowsTcpListenerSnapshotProvider : ITcpListenerSnapshotProvider
{
    private const int AddressFamilyInternet = 2;
    private const int AddressFamilyInternetV6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;
    private const int MaximumTableBytes = 16 * 1024 * 1024;
    private const int MaximumReadAttempts = 4;

    public IReadOnlyList<OwnedTcpListener> Capture()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("TCP listener ownership inspection requires Windows.");

        return ReadTable(AddressFamilyInternet)
            .Concat(ReadTable(AddressFamilyInternetV6))
            .OrderBy(listener => listener.LocalPort)
            .ThenBy(listener => listener.OwningProcessId)
            .ThenBy(listener => listener.LocalAddress.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static List<OwnedTcpListener> ReadTable(int addressFamily)
    {
        var size = 0;
        var status = NativeMethods.GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            false,
            addressFamily,
            TcpTableOwnerPidListener,
            0);
        if (status is not 0 and not ErrorInsufficientBuffer)
            throw new Win32Exception(unchecked((int)status), "Windows could not size the TCP listener table.");

        for (var attempt = 0; attempt < MaximumReadAttempts; attempt++)
        {
            if (size < sizeof(uint) || size > MaximumTableBytes)
                throw new InvalidDataException("Windows returned an invalid TCP listener table size.");

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var available = size;
                status = NativeMethods.GetExtendedTcpTable(
                    buffer,
                    ref available,
                    false,
                    addressFamily,
                    TcpTableOwnerPidListener,
                    0);
                if (status == ErrorInsufficientBuffer)
                {
                    size = available;
                    continue;
                }
                if (status != 0)
                    throw new Win32Exception(unchecked((int)status), "Windows could not read the TCP listener table.");
                if (available < sizeof(uint) || available > size)
                    throw new InvalidDataException("Windows returned an invalid TCP listener table length.");

                return addressFamily == AddressFamilyInternet
                    ? ParseIpv4Table(buffer, available)
                    : ParseIpv6Table(buffer, available);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException("The TCP listener table changed too quickly to inspect safely.");
    }

    private static List<OwnedTcpListener> ParseIpv4Table(IntPtr buffer, int bufferSize)
    {
        var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
        var count = ReadAndValidateCount(buffer, bufferSize, rowSize);
        var result = new List<OwnedTcpListener>(count);
        var firstRow = buffer + sizeof(uint);
        for (var index = 0; index < count; index++)
        {
            var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(firstRow + index * rowSize);
            if (row.State != NativeMethods.MibTcpStateListen) continue;
            result.Add(new OwnedTcpListener(
                new IPAddress(BitConverter.GetBytes(row.LocalAddress)),
                DecodePort(row.LocalPort),
                DecodeProcessId(row.OwningProcessId)));
        }
        return result;
    }

    private static List<OwnedTcpListener> ParseIpv6Table(IntPtr buffer, int bufferSize)
    {
        var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
        var count = ReadAndValidateCount(buffer, bufferSize, rowSize);
        var result = new List<OwnedTcpListener>(count);
        var firstRow = buffer + sizeof(uint);
        for (var index = 0; index < count; index++)
        {
            var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(firstRow + index * rowSize);
            if (row.State != NativeMethods.MibTcpStateListen) continue;
            result.Add(new OwnedTcpListener(
                new IPAddress(
                    row.LocalAddress
                        ?? throw new InvalidDataException("Windows returned an invalid IPv6 listener address."),
                    BinaryPrimitives.ReverseEndianness(row.LocalScopeId)),
                DecodePort(row.LocalPort),
                DecodeProcessId(row.OwningProcessId)));
        }
        return result;
    }

    private static int ReadAndValidateCount(IntPtr buffer, int bufferSize, int rowSize)
    {
        var count = Marshal.ReadInt32(buffer);
        if (count < 0 || sizeof(uint) + (long)count * rowSize > bufferSize)
            throw new InvalidDataException("Windows returned a malformed TCP listener table.");
        return count;
    }

    private static int DecodePort(uint networkPort) =>
        BinaryPrimitives.ReverseEndianness((ushort)(networkPort & ushort.MaxValue));

    private static int DecodeProcessId(uint processId) =>
        processId is > 0 and <= int.MaxValue
            ? (int)processId
            : throw new InvalidDataException("Windows returned an invalid TCP listener process identifier.");

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        internal uint State;
        internal uint LocalAddress;
        internal uint LocalPort;
        internal uint RemoteAddress;
        internal uint RemotePort;
        internal uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        internal byte[] LocalAddress;
        internal uint LocalScopeId;
        internal uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        internal byte[] RemoteAddress;
        internal uint RemoteScopeId;
        internal uint RemotePort;
        internal uint State;
        internal uint OwningProcessId;
    }

    private static class NativeMethods
    {
        internal const uint MibTcpStateListen = 2;

        [DllImport("iphlpapi.dll")]
        internal static extern uint GetExtendedTcpTable(
            IntPtr table,
            ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order,
            int ipVersion,
            int tableClass,
            uint reserved);
    }
}
