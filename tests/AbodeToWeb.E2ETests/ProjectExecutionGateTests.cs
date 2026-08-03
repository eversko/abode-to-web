using System.Diagnostics;
using System.Text.Json;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Deployments;
using AbodeToWeb.Core.Networking;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Core.Supervision;
using AbodeToWeb.Core.Validation;
using AbodeToWeb.Infrastructure;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Deployments;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Health;
using AbodeToWeb.Infrastructure.Logging;
using AbodeToWeb.Infrastructure.Operations;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Processes;
using AbodeToWeb.Infrastructure.Runtime;
using AbodeToWeb.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace AbodeToWeb.E2ETests;

[Collection(GitEnvironmentTests.Name)]
public sealed class ProjectExecutionGateTests
{
    [Fact(Timeout = 60_000)]
    public async Task PrebuiltStaticDeployStartRollbackAndRecoveryNeverLaunchAProjectProcess()
    {
        await using var fixture = await ExecutionGateFixture.CreateAsync(
            ExecutionFixtureKind.PrebuiltStatic, experimentalProjectExecution: false);
        await fixture.CommitAndPushAsync();

        var deployed = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-static"));
        await fixture.Engine.StartAsync("demo", OperationExecutionContext.CreateSystem("start-static"));
        await fixture.Engine.RollbackAsync(
            "demo", deployed.Deployment.Id, OperationExecutionContext.CreateSystem("rollback-static"));

        await fixture.MarkInterruptedAsync(deployed.Deployment.Id);
        await fixture.Engine.ReconcileInterruptedDeploymentsAsync();

        var recovered = await fixture.GetDeploymentAsync(deployed.Deployment.Id);
        Assert.Equal(DeploymentStatus.Succeeded.ToString(), recovered.State);
        Assert.True(recovered.IsKnownGood);
        Assert.Equal(0, fixture.ProcessLauncher.ProjectLaunchCount);
        Assert.Empty(fixture.Supervisor.GetSnapshots());
    }

    [Fact(Timeout = 60_000)]
    public async Task EngineRejectsStaticBuildAndDynamicStartWhenValidationIsBypassedWithoutOptIn()
    {
        await using var staticBuild = await ExecutionGateFixture.CreateAsync(
            ExecutionFixtureKind.StaticBuild, experimentalProjectExecution: false);
        var buildFailure = await Assert.ThrowsAsync<DeploymentException>(() => staticBuild.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("blocked-static-build")));
        Assert.Equal("deployment.execution.experimentalDisabled", buildFailure.Code);
        Assert.Equal(0, staticBuild.ProcessLauncher.ProjectLaunchCount);

        await using var dynamicStart = await ExecutionGateFixture.CreateAsync(
            ExecutionFixtureKind.DynamicStart, experimentalProjectExecution: false);
        var startFailure = await Assert.ThrowsAsync<DeploymentException>(() => dynamicStart.Engine.StartAsync(
            "demo", OperationExecutionContext.CreateSystem("blocked-dynamic-start")));
        Assert.Equal("deployment.execution.experimentalDisabled", startFailure.Code);
        Assert.Equal(0, dynamicStart.ProcessLauncher.ProjectLaunchCount);
    }

    [Fact(Timeout = 60_000)]
    public async Task OptInAllowsFreshDynamicDeployButBlocksRetainedExecutionAndAutomaticRestart()
    {
        await using var fixture = await ExecutionGateFixture.CreateAsync(
            ExecutionFixtureKind.DynamicStart, experimentalProjectExecution: true);
        await fixture.CommitAndPushAsync();

        var deployed = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("fresh-dynamic-deploy"));
        Assert.Equal(1, fixture.ProcessLauncher.ProjectLaunchCount);

        foreach (var action in new Func<Task>[]
        {
            () => fixture.Engine.StartAsync("demo", OperationExecutionContext.CreateSystem("retained-start")),
            () => fixture.Engine.RestartAsync("demo", OperationExecutionContext.CreateSystem("retained-restart")),
            () => fixture.Engine.RollbackAsync(
                "demo", deployed.Deployment.Id, OperationExecutionContext.CreateSystem("retained-rollback"))
        })
        {
            var failure = await Assert.ThrowsAsync<DeploymentException>(action);
            Assert.Equal("artifact.redeploy_required", failure.Code);
        }

        var exited = await fixture.WaitForRuntimeStateAsync(ManagedRuntimeState.Exited);
        await Task.Delay(TimeSpan.FromMilliseconds(1_500));
        Assert.Equal(0, exited.RestartCount);
        var stillExited = Assert.Single(fixture.Supervisor.GetSnapshots());
        Assert.Equal(ManagedRuntimeState.Exited, stillExited.State);
        Assert.Equal(0, stillExited.RestartCount);
        Assert.Equal(1, fixture.ProcessLauncher.ProjectLaunchCount);

        await fixture.MarkInterruptedAsync(deployed.Deployment.Id);
        await fixture.Engine.ReconcileInterruptedDeploymentsAsync();
        var recovered = await fixture.GetDeploymentAsync(deployed.Deployment.Id);
        Assert.Equal(DeploymentStatus.Failed.ToString(), recovered.State);
        Assert.False(recovered.IsKnownGood);
        Assert.Equal(1, fixture.ProcessLauncher.ProjectLaunchCount);
    }

    private enum ExecutionFixtureKind
    {
        PrebuiltStatic,
        StaticBuild,
        DynamicStart
    }

    private sealed class ExecutionGateFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _repository;
        private readonly AbodeToWebDatabase _database;
        private readonly ManifestProvider _manifest;
        private readonly HttpClient _healthClient;

        private ExecutionGateFixture(
            string root,
            string repository,
            DeploymentEngine engine,
            AbodeToWebDatabase database,
            ManifestProvider manifest,
            ApplicationSupervisor supervisor,
            RecordingProcessLauncher processLauncher,
            HttpClient healthClient)
        {
            _root = root;
            _repository = repository;
            Engine = engine;
            _database = database;
            _manifest = manifest;
            Supervisor = supervisor;
            ProcessLauncher = processLauncher;
            _healthClient = healthClient;
        }

        internal DeploymentEngine Engine { get; }

        internal ApplicationSupervisor Supervisor { get; }

        internal RecordingProcessLauncher ProcessLauncher { get; }

        internal static async Task<ExecutionGateFixture> CreateAsync(
            ExecutionFixtureKind kind,
            bool experimentalProjectExecution)
        {
            var root = Path.Combine(Path.GetTempPath(), "AbodeToWeb-execution-gate-" + Guid.NewGuid().ToString("N"));
            var repository = Path.Combine(root, "repository");
            var remote = Path.Combine(root, "remote.git");
            Directory.CreateDirectory(root);
            RunGit(root, "init", "--bare", remote);
            RunGit(root, "init", "--initial-branch=main", repository);
            RunGit(repository, "config", "user.email", "abode-to-web-tests@example.invalid");
            RunGit(repository, "config", "user.name", "AbodeToWeb Tests");
            RunGit(repository, "remote", "add", "origin", remote);

            var paths = new AbodeToWebPaths
            {
                DataRoot = Path.Combine(root, "data"),
                ConfigurationFile = Path.Combine(root, "data", "config", "abode-to-web.yaml"),
                DatabaseFile = Path.Combine(root, "data", "state", "abode-to-web.db"),
                LogsRoot = Path.Combine(root, "data", "logs"),
                DeploymentsRoot = Path.Combine(root, "data", "deployments"),
                SecretsRoot = Path.Combine(root, "data", "secrets"),
                BackupsRoot = Path.Combine(root, "data", "backups")
            };
            paths.EnsureDirectories();
            var manifest = CreateManifest(repository, remote, kind, experimentalProjectExecution);
            await File.WriteAllTextAsync(paths.ConfigurationFile,
                JsonSerializer.Serialize(manifest, JsonManifestParser.CreateSerializerOptions()));

            // Intentionally bypass normal manifest validation. The engine is the
            // defense-in-depth boundary under test and must reject unsafe callers.
            var provider = new ManifestProvider(paths, new YamlManifestParser(), _ => new ValidationResult());
            var loaded = await provider.LoadAsync();
            Assert.True(loaded.Success);

            var database = new AbodeToWebDatabase(paths);
            await database.InitializeAsync();
            var routes = new GatewayRouteRegistry(database);
            await routes.InitializeAsync(provider.Current);
            var logs = new BoundedLogStore(paths, new LogStreamHub());
            var launcher = new RecordingProcessLauncher();
            var supervisor = new ApplicationSupervisor(logs, NullLogger<ApplicationSupervisor>.Instance, launcher);
            var runner = new StructuredCommandRunner(logs, launcher);
            var git = new GitClient(runner, provider, paths, allowLocalRepositoryRemotesForTests: true);
            var healthClient = new HttpClient(ApplicationHealthProbe.CreateHttpMessageHandler());
            var health = new ApplicationHealthProbe(healthClient);
            var endpoints = new HostedEndpointVerifier(new WindowsTcpListenerSnapshotProvider(), supervisor);
            var engine = new DeploymentEngine(
                provider,
                paths,
                database,
                git,
                runner,
                supervisor,
                health,
                endpoints,
                routes,
                new SecretFileStore(paths),
                new DeterministicPortAllocator(26_000, 29_000));
            return new ExecutionGateFixture(
                root, repository, engine, database, provider, supervisor, launcher, healthClient);
        }

        internal async Task CommitAndPushAsync()
        {
            Directory.CreateDirectory(Path.Combine(_repository, "dist"));
            await File.WriteAllTextAsync(Path.Combine(_repository, "dist", "index.html"), "reviewed release");
            RunGit(_repository, "add", "--", "dist/index.html");
            RunGit(_repository, "commit", "-m", "Reviewed release");
            RunGit(_repository, "push", "-u", "origin", "main");
        }

        internal async Task MarkInterruptedAsync(string deploymentId)
        {
            var deployment = await GetDeploymentAsync(deploymentId);
            await _database.UpdateDeploymentAsync(deployment with
            {
                State = DeploymentStatus.DrainingPrevious.ToString(),
                CompletedUtc = null,
                DurationMilliseconds = null,
                IsKnownGood = false,
                FailureReason = null
            });
        }

        internal async Task<DeploymentRecord> GetDeploymentAsync(string deploymentId) =>
            await _database.GetDeploymentAsync(deploymentId)
            ?? throw new InvalidOperationException("The deployment record is missing.");

        internal async Task<ManagedProcessSnapshot> WaitForRuntimeStateAsync(ManagedRuntimeState state)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshot = Supervisor.GetSnapshots().SingleOrDefault(item => item.AppId == "demo");
                if (snapshot?.State == state) return snapshot;
                await Task.Delay(50);
            }
            throw new TimeoutException($"The managed process did not reach {state}.");
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            _healthClient.Dispose();
            _manifest.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 10 && Directory.Exists(_root); attempt++)
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                }
                catch (Exception exception) when (attempt < 9
                                                    && exception is IOException or UnauthorizedAccessException)
                {
                    ClearReadOnlyAttributes(_root);
                    await Task.Delay(25 * (attempt + 1));
                }
            }
        }

        private static AbodeToWebManifest CreateManifest(
            string repository,
            string remote,
            ExecutionFixtureKind kind,
            bool experimentalProjectExecution) => new()
            {
                ManifestVersion = 1,
                AbodeToWeb = new AbodeToWebOptions
                {
                    ProjectsRoots = [Path.GetDirectoryName(repository)!],
                    ExperimentalProjectExecution = experimentalProjectExecution,
                    Dashboard = new ListenerOptions { Bind = "127.0.0.1", Port = 45_451 },
                    Gateway = new ListenerOptions { Bind = "127.0.0.1", Port = 45_480 },
                    Storage = new StorageOptions { RetainedDeploymentsPerApp = 3 },
                    StartupStaggerSeconds = 0
                },
                Apps =
            [
                new AppManifest
                {
                    Id = "demo",
                    Name = "Demo",
                    Enabled = true,
                    Repository = new RepositoryOptions
                    {
                        Provider = RepositoryProvider.Other,
                        LocalPath = repository,
                        Remote = "origin",
                        ExpectedRemoteUrl = remote,
                        Branch = "main"
                    },
                    Deployment = CreateDeployment(kind),
                    Runtime = new RuntimeOptions
                    {
                        RestartPolicy = RestartPolicy.Always,
                        MaxRestarts = 5,
                        RestartWindowSeconds = 30,
                        InitialBackoffSeconds = 1,
                        MaxBackoffSeconds = 1,
                        StableRuntimeSeconds = 1,
                        GracefulShutdownSeconds = 1
                    },
                    Health = new HealthCheckOptions
                    {
                        Type = kind == ExecutionFixtureKind.DynamicStart
                            ? HealthCheckType.Tcp
                            : HealthCheckType.None,
                        HealthyThreshold = 1,
                        UnhealthyThreshold = 1,
                        IntervalSeconds = 1,
                        TimeoutSeconds = 1
                    },
                    Routing = new RoutingOptions { Hostname = "demo.test" }
                }
            ]
            };

        private static DeploymentOptions CreateDeployment(ExecutionFixtureKind kind) => kind switch
        {
            ExecutionFixtureKind.PrebuiltStatic => new DeploymentOptions
            {
                Adapter = DeploymentAdapter.Static,
                Mode = DeploymentMode.Worktree,
                StaticOutputDirectory = "dist",
                CandidateStartupTimeoutSeconds = 10,
                DrainSeconds = 0
            },
            ExecutionFixtureKind.StaticBuild => new DeploymentOptions
            {
                Adapter = DeploymentAdapter.Static,
                Mode = DeploymentMode.Worktree,
                Build = new CommandSpec
                {
                    Executable = "cmd.exe",
                    Arguments = ["/d", "/c", "exit", "/b", "0"],
                    TimeoutSeconds = 10
                },
                StaticOutputDirectory = "dist",
                CandidateStartupTimeoutSeconds = 10,
                DrainSeconds = 0
            },
            ExecutionFixtureKind.DynamicStart => new DeploymentOptions
            {
                Adapter = DeploymentAdapter.Command,
                Mode = DeploymentMode.Worktree,
                Start = new CommandSpec
                {
                    Executable = "powershell.exe",
                    Arguments =
                    [
                        "-NoProfile",
                        "-NonInteractive",
                        "-Command",
                        "$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, [int]$env:PORT); " +
                        "$listener.Start(); Start-Sleep -Seconds 3"
                    ],
                    TimeoutSeconds = 30
                },
                CandidateStartupTimeoutSeconds = 10,
                DrainSeconds = 0
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        private static void RunGit(string workingDirectory, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git.exe")
                {
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Git fixture command failed ({process.ExitCode}): {output}{Environment.NewLine}{error}");
        }

        private static void ClearReadOnlyAttributes(string root)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal sealed class RecordingProcessLauncher : IProcessLauncher
    {
        private int _projectLaunchCount;

        internal int ProjectLaunchCount => Volatile.Read(ref _projectLaunchCount);

        public LaunchedProcess Start(ProcessStartInfo startInfo, string jobName, string? isolationKey)
        {
            if (isolationKey is not null) Interlocked.Increment(ref _projectLaunchCount);
            return SuspendedProcessLauncher.Start(startInfo, jobName, isolationKey: null);
        }
    }
}
