using System.Diagnostics;
using System.Net;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Core.Supervision;
using AbodeToWeb.Core.Validation;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Health;
using AbodeToWeb.Infrastructure.Logging;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Runtime;
using AbodeToWeb.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AbodeToWeb.IntegrationTests;

[CollectionDefinition(ApplicationHealthMonitoringTestGroup.CollectionName, DisableParallelization = true)]
public sealed class ApplicationHealthMonitoringTestGroup
{
    public const string CollectionName = "Application health monitoring SQLite lifecycle";
}

[Collection(ApplicationHealthMonitoringTestGroup.CollectionName)]
public sealed class ApplicationHealthMonitoringServiceTests
{
    [Fact]
    public async Task Attestation_violation_stops_only_matching_supervised_job_and_marks_route_unhealthy()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporary = new TemporaryAbodeToWebDirectory();
        using var manifest = new ManifestProvider(
            temporary.Paths,
            new YamlManifestParser(),
            _ => new ValidationResult());
        var port = IntegrationTestWait.GetAvailableLoopbackPort();
        var app = CreateApp(port);
        await manifest.WriteAtomicAsync(new AbodeToWebManifest
        {
            ManifestVersion = 1,
            AbodeToWeb = new AbodeToWebOptions { ProjectsRoots = [temporary.Root] },
            Apps = [app]
        });

        var database = new AbodeToWebDatabase(temporary.Paths);
        await database.InitializeAsync();
        var routes = new GatewayRouteRegistry(database);
        await routes.InitializeAsync(manifest.Current);
        await routes.SetActiveAsync(
            app,
            "deployment-a",
            port,
            staticPath: null,
            GatewayRouteStatus.Starting);

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
        var node = FindNodeExecutable();
        using var foreign = StartLongRunningProcess(node);
        var request = CreateManagedRequest(temporary.Root, node);
        await supervisor.StartAsync(request, timeout.Token);

        var listenerSnapshot = new FixedListenerSnapshotProvider(
            [new OwnedTcpListener(IPAddress.Loopback, port, foreign.Id)]);
        var verifier = new HostedEndpointVerifier(listenerSnapshot, supervisor);
        using var httpClient = new HttpClient(ApplicationHealthProbe.CreateHttpMessageHandler());
        var monitoring = new ApplicationHealthMonitoringService(
            manifest,
            routes,
            supervisor,
            verifier,
            new ApplicationHealthProbe(httpClient),
            temporary.Paths,
            NullLogger<ApplicationHealthMonitoringService>.Instance);

        await monitoring.StartAsync(timeout.Token);
        try
        {
            await IntegrationTestWait.UntilAsync(
                () => supervisor.TryGetSnapshot(request.InstanceId, out var snapshot)
                      && snapshot.State == ManagedRuntimeState.Stopped
                      && routes.TryGetByApp(app.Id, out var route)
                      && route.Status == GatewayRouteStatus.Unhealthy,
                TimeSpan.FromSeconds(8),
                "The health monitor did not quarantine the endpoint-attestation violation.",
                timeout.Token);

            Assert.True(listenerSnapshot.CaptureCount > 0);
            Assert.False(foreign.HasExited);
            Assert.True(routes.TryGetByApp(app.Id, out var quarantined));
            Assert.Equal(GatewayRouteStatus.Unhealthy, quarantined.Status);
        }
        finally
        {
            await monitoring.StopAsync(CancellationToken.None);
            await supervisor.StopAsync(request.InstanceId, force: true, CancellationToken.None);
            if (!foreign.HasExited)
            {
                foreign.Kill(entireProcessTree: true);
                await foreign.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Ordinary_health_probe_failure_marks_route_unhealthy_without_quarantining_owned_job()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporary = new TemporaryAbodeToWebDirectory();
        using var manifest = new ManifestProvider(
            temporary.Paths,
            new YamlManifestParser(),
            _ => new ValidationResult());
        var port = IntegrationTestWait.GetAvailableLoopbackPort();
        var app = CreateApp(port, HealthCheckType.Http);
        await manifest.WriteAtomicAsync(new AbodeToWebManifest
        {
            ManifestVersion = 1,
            AbodeToWeb = new AbodeToWebOptions { ProjectsRoots = [temporary.Root] },
            Apps = [app]
        });

        var database = new AbodeToWebDatabase(temporary.Paths);
        await database.InitializeAsync();
        var routes = new GatewayRouteRegistry(database);
        await routes.InitializeAsync(manifest.Current);
        await routes.SetActiveAsync(
            app,
            "deployment-a",
            port,
            staticPath: null,
            GatewayRouteStatus.Starting);
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
        var request = CreateManagedRequest(temporary.Root, FindNodeExecutable());
        var started = await supervisor.StartAsync(request, timeout.Token);
        var processId = Assert.IsType<int>(started.ProcessId);
        var listenerSnapshot = new FixedListenerSnapshotProvider(
            [new OwnedTcpListener(IPAddress.Loopback, port, processId)]);
        var verifier = new HostedEndpointVerifier(listenerSnapshot, supervisor);
        using var httpClient = new HttpClient(ApplicationHealthProbe.CreateHttpMessageHandler());
        var monitoring = new ApplicationHealthMonitoringService(
            manifest,
            routes,
            supervisor,
            verifier,
            new ApplicationHealthProbe(httpClient),
            temporary.Paths,
            NullLogger<ApplicationHealthMonitoringService>.Instance);

        await monitoring.StartAsync(timeout.Token);
        try
        {
            await IntegrationTestWait.UntilAsync(
                () => routes.TryGetByApp(app.Id, out var route)
                      && route.Status == GatewayRouteStatus.Unhealthy,
                TimeSpan.FromSeconds(8),
                "The failed direct health probe did not flow through the health threshold.",
                timeout.Token);

            Assert.True(supervisor.TryGetSnapshot(request.InstanceId, out var stillRunning));
            Assert.Equal(ManagedRuntimeState.Running, stillRunning.State);
            Assert.True(listenerSnapshot.CaptureCount > 0);
        }
        finally
        {
            await monitoring.StopAsync(CancellationToken.None);
            await supervisor.StopAsync(request.InstanceId, force: true, CancellationToken.None);
        }
    }

    private static AppManifest CreateApp(
        int port,
        HealthCheckType healthType = HealthCheckType.Process) => new()
        {
            Id = "dynamic-health-fixture",
            Name = "Dynamic health fixture",
            Deployment = new DeploymentOptions
            {
                Adapter = DeploymentAdapter.Command,
                Start = new CommandSpec { Executable = "fixture" }
            },
            Runtime = new RuntimeOptions { InternalPort = port },
            Health = new HealthCheckOptions
            {
                Type = healthType,
                IntervalSeconds = 1,
                HealthyThreshold = 1,
                UnhealthyThreshold = 1
            },
            Routing = new RoutingOptions { Hostname = "dynamic-health.abode-to-web.test" }
        };

    private static ManagedProcessRequest CreateManagedRequest(string workingDirectory, string node) => new(
        InstanceId: "dynamic-health-instance",
        AppId: "dynamic-health-fixture",
        DeploymentId: "deployment-a",
        Command: new CommandSpec
        {
            Executable = node,
            Arguments = ["-e", "setInterval(()=>{},1000);"]
        },
        WorkingDirectory: workingDirectory,
        Environment: new Dictionary<string, string?>(),
        RestartSettings: new RestartPolicySettings(
            RestartPolicy.Never,
            MaxRestarts: 0,
            Window: TimeSpan.FromMinutes(1),
            InitialDelay: TimeSpan.Zero,
            MaximumDelay: TimeSpan.Zero,
            StableRuntime: TimeSpan.FromMinutes(1)),
        GracefulShutdown: TimeSpan.FromSeconds(1),
        EnableAutomaticRestart: false);

    private static Process StartLongRunningProcess(string node)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = node,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("setInterval(()=>{},1000);");
        return Process.Start(startInfo)
               ?? throw new Xunit.Sdk.XunitException("The foreign process fixture did not start.");
    }

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
            "Node.js is required for the health-monitor integration fixture but was not found on PATH.");
    }

    private sealed class FixedListenerSnapshotProvider(IReadOnlyList<OwnedTcpListener> snapshot)
        : ITcpListenerSnapshotProvider
    {
        public int CaptureCount { get; private set; }

        public IReadOnlyList<OwnedTcpListener> Capture()
        {
            CaptureCount++;
            return snapshot;
        }
    }
}
