using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Core.Supervision;
using AbodeToWeb.Infrastructure.Logging;
using AbodeToWeb.Infrastructure.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace AbodeToWeb.IntegrationTests;

public sealed class HostedEndpointVerifierTests
{
    private const string InstanceId = "test-instance";
    private const int ExpectedPort = 4_545;
    private const int RootProcessId = 100;
    private const int ChildProcessId = 101;

    [Fact]
    public async Task Child_owned_ipv6_loopback_listener_is_verified()
    {
        var verifier = CreateVerifier(
            [Listener(IPAddress.IPv6Loopback, ExpectedPort, ChildProcessId)],
            ProcessTree(RootProcessId, ChildProcessId));

        var result = await verifier.VerifyAsync(InstanceId, ExpectedPort);

        Assert.True(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.Verified, result.Code);
        var evidence = Assert.IsType<HostedEndpointEvidence>(result.Evidence);
        Assert.Equal(ChildProcessId, Assert.Single(evidence.ExpectedPortListeners).OwningProcessId);
        Assert.Contains(ChildProcessId, evidence.ManagedProcessIds);
    }

    [Fact]
    public async Task Every_expected_port_listener_must_belong_to_the_process_tree()
    {
        var verifier = CreateVerifier(
            [
                Listener(IPAddress.Loopback, ExpectedPort, ChildProcessId),
                Listener(IPAddress.IPv6Loopback, ExpectedPort, 999)
            ],
            ProcessTree(RootProcessId, ChildProcessId));

        var result = await verifier.VerifyAsync(InstanceId, ExpectedPort);

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.ListenerNotOwned, result.Code);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task Ipv4_wildcard_expected_listener_is_rejected()
    {
        var verifier = CreateVerifier(
            [
                Listener(IPAddress.Loopback, ExpectedPort, ChildProcessId),
                Listener(IPAddress.Any, ExpectedPort, ChildProcessId)
            ],
            ProcessTree(RootProcessId, ChildProcessId));

        var result = await verifier.VerifyAsync(InstanceId, ExpectedPort);

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.ListenerNotLoopback, result.Code);
    }

    [Fact]
    public async Task Ipv6_wildcard_expected_listener_is_rejected()
    {
        var verifier = CreateVerifier(
            [
                Listener(IPAddress.IPv6Loopback, ExpectedPort, ChildProcessId),
                Listener(IPAddress.IPv6Any, ExpectedPort, ChildProcessId)
            ],
            ProcessTree(RootProcessId, ChildProcessId));

        var result = await verifier.VerifyAsync(InstanceId, ExpectedPort);

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.ListenerNotLoopback, result.Code);
    }

    [Fact]
    public async Task Nonloopback_listener_on_another_port_owned_by_the_tree_is_rejected()
    {
        var verifier = CreateVerifier(
            [
                Listener(IPAddress.Loopback, ExpectedPort, ChildProcessId),
                Listener(IPAddress.Parse("192.0.2.10"), ExpectedPort + 1, ChildProcessId)
            ],
            ProcessTree(RootProcessId, ChildProcessId));

        var result = await verifier.VerifyAsync(InstanceId, ExpectedPort);

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.ManagedListenerNotLoopback, result.Code);
    }

    [Fact]
    public async Task Missing_expected_listener_is_rejected()
    {
        var verifier = CreateVerifier([], ProcessTree(RootProcessId, ChildProcessId));

        var result = await verifier.VerifyAsync(InstanceId, ExpectedPort);

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.ListenerMissing, result.Code);
    }

    [Fact]
    public async Task Listener_api_failure_is_reported_without_exposing_details()
    {
        var verifier = new HostedEndpointVerifier(
            new StubListenerProvider(() => throw new Win32Exception(5, "sensitive native detail")),
            new StubProcessTreeProvider((_, _) => ProcessTree(RootProcessId, ChildProcessId)));

        var result = await verifier.VerifyAsync(InstanceId, ExpectedPort);

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.InspectionFailed, result.Code);
        Assert.DoesNotContain("sensitive", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Process_tree_api_failure_is_reported_fail_closed()
    {
        var verifier = new HostedEndpointVerifier(
            new StubListenerProvider(() => [Listener(IPAddress.Loopback, ExpectedPort, ChildProcessId)]),
            new StubProcessTreeProvider((_, _) => throw new InvalidDataException("malformed job response")));

        var result = await verifier.VerifyAsync(InstanceId, ExpectedPort);

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.InspectionFailed, result.Code);
    }

    [Fact]
    public async Task Unsupported_native_inspection_is_reported_fail_closed()
    {
        var verifier = new HostedEndpointVerifier(
            new StubListenerProvider(() => throw new PlatformNotSupportedException()),
            new StubProcessTreeProvider((_, _) => ProcessTree(RootProcessId, ChildProcessId)));

        var result = await verifier.VerifyAsync(InstanceId, ExpectedPort);

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.UnsupportedPlatform, result.Code);
    }

    [Fact]
    public async Task Stable_verification_runs_proof_probe_proof_in_order()
    {
        var events = new List<string>();
        var listeners = new StubListenerProvider(() =>
        {
            events.Add("listeners");
            return [Listener(IPAddress.Loopback, ExpectedPort, ChildProcessId)];
        });
        var processTrees = new StubProcessTreeProvider((_, _) =>
        {
            events.Add("tree");
            return ProcessTree(RootProcessId, ChildProcessId);
        });
        var verifier = new HostedEndpointVerifier(listeners, processTrees);

        var result = await verifier.VerifyStableAsync(
            InstanceId,
            ExpectedPort,
            _ =>
            {
                events.Add("probe");
                return Task.FromResult(true);
            });

        Assert.True(result.IsVerified);
        Assert.Equal(["tree", "listeners", "probe", "tree", "listeners"], events);
        Assert.Equal(2, listeners.CaptureCount);
        Assert.Equal(2, processTrees.CaptureCount);
    }

    [Fact]
    public async Task Ownership_change_between_individually_valid_proofs_is_rejected()
    {
        var ownerIds = new Queue<int>([ChildProcessId, ChildProcessId + 1]);
        var listeners = new StubListenerProvider(
            () => [Listener(IPAddress.Loopback, ExpectedPort, ownerIds.Dequeue())]);
        var processTrees = new StubProcessTreeProvider(
            (_, _) => ProcessTree(RootProcessId, ChildProcessId, ChildProcessId + 1));
        var verifier = new HostedEndpointVerifier(listeners, processTrees);

        var result = await verifier.VerifyStableAsync(
            InstanceId,
            ExpectedPort,
            _ => Task.FromResult(true));

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.EvidenceChanged, result.Code);
        Assert.Equal(2, listeners.CaptureCount);
        Assert.Equal(2, processTrees.CaptureCount);
    }

    [Fact]
    public async Task Root_process_change_with_the_same_process_set_is_rejected()
    {
        var trees = new Queue<ManagedProcessTreeSnapshot>(
        [
            ProcessTree(RootProcessId, ChildProcessId),
            ProcessTree(ChildProcessId, RootProcessId)
        ]);
        var listeners = new StubListenerProvider(
            () => [Listener(IPAddress.Loopback, ExpectedPort, ChildProcessId)]);
        var processTrees = new StubProcessTreeProvider((_, _) => trees.Dequeue());
        var verifier = new HostedEndpointVerifier(listeners, processTrees);

        var result = await verifier.VerifyStableAsync(
            InstanceId,
            ExpectedPort,
            _ => Task.FromResult(true));

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.EvidenceChanged, result.Code);
    }

    [Fact]
    public async Task Failed_final_probe_stops_before_second_proof()
    {
        var listeners = new StubListenerProvider(
            () => [Listener(IPAddress.Loopback, ExpectedPort, ChildProcessId)]);
        var processTrees = new StubProcessTreeProvider(
            (_, _) => ProcessTree(RootProcessId, ChildProcessId));
        var verifier = new HostedEndpointVerifier(listeners, processTrees);

        var result = await verifier.VerifyStableAsync(
            InstanceId,
            ExpectedPort,
            _ => Task.FromResult(false));

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.FinalProbeFailed, result.Code);
        Assert.Equal(1, listeners.CaptureCount);
        Assert.Equal(1, processTrees.CaptureCount);
    }

    [Fact]
    public async Task Timed_out_final_probe_is_reported_fail_closed()
    {
        var listeners = new StubListenerProvider(
            () => [Listener(IPAddress.Loopback, ExpectedPort, ChildProcessId)]);
        var processTrees = new StubProcessTreeProvider(
            (_, _) => ProcessTree(RootProcessId, ChildProcessId));
        var verifier = new HostedEndpointVerifier(listeners, processTrees);

        var result = await verifier.VerifyStableAsync(
            InstanceId,
            ExpectedPort,
            _ => Task.FromException<bool>(new TaskCanceledException("probe timeout")));

        Assert.False(result.IsVerified);
        Assert.Equal(HostedEndpointVerificationCodes.FinalProbeFailed, result.Code);
        Assert.Equal(1, listeners.CaptureCount);
        Assert.Equal(1, processTrees.CaptureCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var verifier = CreateVerifier(
            [Listener(IPAddress.Loopback, ExpectedPort, ChildProcessId)],
            ProcessTree(RootProcessId, ChildProcessId));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verifier.VerifyAsync(InstanceId, ExpectedPort, cancellation.Token));
    }

    private static HostedEndpointVerifier CreateVerifier(
        IReadOnlyList<OwnedTcpListener> listeners,
        ManagedProcessTreeSnapshot processTree) =>
        new(
            new StubListenerProvider(() => listeners),
            new StubProcessTreeProvider((_, _) => processTree));

    private static OwnedTcpListener Listener(IPAddress address, int port, int processId) =>
        new(address, port, processId);

    private static ManagedProcessTreeSnapshot ProcessTree(int rootProcessId, params int[] processIds) =>
        new(rootProcessId, processIds.Prepend(rootProcessId).ToHashSet());

    private sealed class StubListenerProvider(
        Func<IReadOnlyList<OwnedTcpListener>> capture) : ITcpListenerSnapshotProvider
    {
        public int CaptureCount { get; private set; }

        public IReadOnlyList<OwnedTcpListener> Capture()
        {
            CaptureCount++;
            return capture();
        }
    }

    private sealed class StubProcessTreeProvider(
        Func<string, CancellationToken, ManagedProcessTreeSnapshot?> capture)
        : IManagedProcessTreeSnapshotProvider
    {
        public int CaptureCount { get; private set; }

        public ValueTask<ManagedProcessTreeSnapshot?> CaptureProcessTreeAsync(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            return ValueTask.FromResult(capture(instanceId, cancellationToken));
        }
    }
}

public sealed class WindowsHostedEndpointVerifierIntegrationTests
{
    [Fact]
    public void Native_snapshot_reports_ipv4_listener_address_port_and_owner()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var snapshot = new WindowsTcpListenerSnapshotProvider().Capture();

        Assert.Contains(snapshot, row =>
            row.LocalAddress.Equals(IPAddress.Loopback)
            && row.LocalPort == port
            && row.OwningProcessId == Environment.ProcessId);
    }

    [Fact]
    public void Native_snapshot_reports_ipv6_listener_address_port_and_owner()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) return;
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = false;
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var snapshot = new WindowsTcpListenerSnapshotProvider().Capture();

        Assert.Contains(snapshot, row =>
            row.LocalAddress.Equals(IPAddress.IPv6Loopback)
            && row.LocalPort == port
            && row.OwningProcessId == Environment.ProcessId);
    }

    [Fact]
    public async Task Supervisor_job_attests_a_listener_owned_by_its_child_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryAbodeToWebDirectory();
        var logs = new BoundedLogStore(
            temporary.Paths,
            new LogStreamHub(),
            maxFileBytes: 1024 * 1024,
            retainedFiles: 2);
        await using var supervisor = new ApplicationSupervisor(
            logs,
            NullLogger<ApplicationSupervisor>.Instance,
            new CurrentIdentityTestProcessLauncher());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var port = IntegrationTestWait.GetAvailableLoopbackPort();
        var request = CreateChildListenerRequest(temporary, port);
        var started = await supervisor.StartAsync(request, timeout.Token);
        var verifier = new HostedEndpointVerifier(new WindowsTcpListenerSnapshotProvider(), supervisor);
        HostedEndpointVerification? attestation = null;

        try
        {
            await IntegrationTestWait.UntilAsync(
                async () =>
                {
                    attestation = await verifier.VerifyAsync(request.InstanceId, port, timeout.Token);
                    return attestation.IsVerified;
                },
                TimeSpan.FromSeconds(8),
                "The child listener was not proven as a member of the supervised Windows Job.",
                timeout.Token);

            Assert.NotNull(started.ProcessId);
            var evidence = Assert.IsType<HostedEndpointEvidence>(attestation!.Evidence);
            var endpoint = Assert.Single(evidence.ExpectedPortListeners);
            Assert.NotEqual(started.ProcessId.Value, endpoint.OwningProcessId);
            Assert.Contains(endpoint.OwningProcessId, evidence.ManagedProcessIds);
            Assert.Contains(started.ProcessId.Value, evidence.ManagedProcessIds);
        }
        finally
        {
            await supervisor.StopAsync(request.InstanceId, force: true, timeout.Token);
        }
    }

    private static ManagedProcessRequest CreateChildListenerRequest(
        TemporaryAbodeToWebDirectory temporary,
        int port) =>
        new(
            InstanceId: "child-listener-" + Guid.NewGuid().ToString("N"),
            AppId: "child-listener-fixture",
            DeploymentId: "fixture-deployment",
            Command: new CommandSpec
            {
                Executable = FindNodeExecutable(),
                Arguments = ["-e", ChildListenerParentScript],
                TimeoutSeconds = 30
            },
            WorkingDirectory: temporary.Root,
            Environment: new Dictionary<string, string?>
            {
                ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            RestartSettings: new RestartPolicySettings(
                RestartPolicy.Never,
                MaxRestarts: 0,
                Window: TimeSpan.FromMinutes(1),
                InitialDelay: TimeSpan.Zero,
                MaximumDelay: TimeSpan.Zero,
                StableRuntime: TimeSpan.FromMinutes(1)),
            GracefulShutdown: TimeSpan.FromSeconds(2),
            EnableAutomaticRestart: false);

    private static string FindNodeExecutable()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe", "node" } : new[] { "node" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        throw new Xunit.Sdk.XunitException(
            "Node.js is required for the endpoint-attestation integration fixture but was not found on PATH.");
    }

    private const string ChildListenerParentScript =
        "const {spawn}=require('child_process');" +
        "const childCode=\"const net=require('net');const server=net.createServer();" +
        "server.listen(Number(process.env.PORT),'127.0.0.1',()=>console.log('CHILD_READY'));\";" +
        "const child=spawn(process.execPath,['-e',childCode]," +
        "{env:process.env,stdio:['ignore','inherit','inherit']});" +
        "process.stdin.resume();" +
        "process.stdin.on('end',()=>{child.kill('SIGTERM');setTimeout(()=>process.exit(0),50);});" +
        "setInterval(()=>{},1000);";
}
