using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Deployments;
using AbodeToWeb.Core.Networking;
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
using AbodeToWeb.Server.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Forwarder;

namespace AbodeToWeb.E2ETests;

[Collection(GitEnvironmentTests.Name)]
public sealed class DeploymentSmokeTests
{
    [Fact(Timeout = 120_000)]
    public async Task FailedCandidatePreservesAThenCSwitchesAndRetainedRollbackFailsClosed()
    {
        if (FindExecutable("node.exe") is null) return;
        await using var fixture = await DeploymentFixture.CreateAsync();

        var versionA = await fixture.CommitAndPushAsync("A", buildFails: false);
        var deploymentA = await fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-a"));
        Assert.Equal(versionA, deploymentA.Deployment.CommitSha);
        Assert.Equal("A", await fixture.GetGatewayVersionAsync());
        await fixture.AssertActiveEndpointOwnedByChildAsync(deploymentA.Deployment.Id);
        var routeA = fixture.ActiveRoute();

        await fixture.CommitAndPushAsync("B", buildFails: true);
        var failure = await Assert.ThrowsAsync<DeploymentException>(() =>
            fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-b")));
        Assert.Equal("build.command_failed", failure.Code);
        Assert.Equal(routeA.DeploymentId, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("A", await fixture.GetGatewayVersionAsync());

        var versionC = await fixture.CommitAndPushAsync("C", buildFails: false);
        var deploymentC = await fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-c"));
        Assert.Equal(versionC, deploymentC.Deployment.CommitSha);
        Assert.NotEqual(routeA.DeploymentId, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("C", await fixture.GetGatewayVersionAsync());

        var rollbackFailure = await Assert.ThrowsAsync<DeploymentException>(() =>
            fixture.Engine.RollbackAsync("demo", deploymentA.Deployment.Id, OperationExecutionContext.CreateSystem("rollback-a")));
        Assert.Equal("artifact.redeploy_required", rollbackFailure.Code);
        Assert.Equal(deploymentC.Deployment.Id, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("C", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task WildcardCandidateIsRejectedBeforeCutoverAndPreviousRouteRemainsActive()
    {
        if (FindExecutable("node.exe") is null) return;
        await using var fixture = await DeploymentFixture.CreateAsync();
        await fixture.CommitAndPushAsync("A", buildFails: false);
        var deploymentA = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-a"));
        var routeA = fixture.ActiveRoute();
        await fixture.CommitAndPushAsync("B", buildFails: false, listenerAddress: "0.0.0.0");

        var failure = await Assert.ThrowsAsync<DeploymentException>(() =>
            fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-wildcard")));

        Assert.Equal(HostedEndpointVerificationCodes.ListenerNotLoopback, failure.Code);
        Assert.Equal(deploymentA.Deployment.Id, routeA.DeploymentId);
        Assert.Equal(routeA.DeploymentId, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("A", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task ForeignCandidateListenerIsRejectedWithoutStoppingItOrChangingTheRoute()
    {
        if (FindExecutable("node.exe") is null) return;
        await using var fixture = await DeploymentFixture.CreateAsync();
        await fixture.CommitAndPushAsync("A", buildFails: false);
        var deploymentA = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-a"));
        var routeA = fixture.ActiveRoute();
        await fixture.SetHealthTypeAsync(HealthCheckType.Tcp);
        await fixture.CommitAndPushAsync("B", buildFails: false, startsListener: false);
        fixture.BindForeignListenerOnNextApplicationStart();

        var failure = await Assert.ThrowsAsync<DeploymentException>(() =>
            fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-foreign")));

        Assert.Equal(HostedEndpointVerificationCodes.ListenerNotOwned, failure.Code);
        Assert.Equal(deploymentA.Deployment.Id, routeA.DeploymentId);
        Assert.Equal(routeA.DeploymentId, fixture.ActiveRoute().DeploymentId);
        Assert.True(await fixture.ForeignListenerStillAcceptsConnectionsAsync());
        Assert.Equal("A", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task DockerEndpointFailureRunsScopedComposeCleanup()
    {
        if (FindExecutable("node.exe") is null) return;
        await using var fixture = await DeploymentFixture.CreateAsync(dockerCompose: true);
        await fixture.CommitAndPushAsync("A", buildFails: false, listenerAddress: "0.0.0.0");

        var failure = await Assert.ThrowsAsync<DeploymentException>(() =>
            fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-docker-wildcard")));

        Assert.Equal(HostedEndpointVerificationCodes.ListenerNotLoopback, failure.Code);
        Assert.True(fixture.ComposeDownWasInvoked);
    }

    [Fact(Timeout = 120_000)]
    public async Task CancellationDuringPostCutoverDrainKeepsNewDeploymentActive()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);

        await fixture.CommitAndPushAsync("A", buildFails: false);
        var deploymentA = await fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-a"));
        await fixture.CommitAndPushAsync("B", buildFails: false);
        await fixture.SetDrainSecondsAsync(30);

        using var cancellation = new CancellationTokenSource();
        var deploying = fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-b"), cancellation.Token);
        var committedRoute = await fixture.WaitForDifferentActiveDeploymentAsync(deploymentA.Deployment.Id);
        Assert.Equal("B", await fixture.GetGatewayVersionAsync());

        cancellation.Cancel();
        var failure = await Assert.ThrowsAsync<DeploymentException>(async () => await deploying);

        Assert.Equal("deployment.cleanup_failed", failure.Code);
        Assert.Equal(committedRoute.DeploymentId, fixture.ActiveRoute().DeploymentId);
        Assert.NotEqual(deploymentA.Deployment.Id, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("B", await fixture.GetGatewayVersionAsync());
        var committed = await fixture.GetDeploymentAsync(committedRoute.DeploymentId!);
        Assert.Equal(DeploymentStatus.Succeeded.ToString(), committed.State);
        Assert.True(committed.IsKnownGood);
    }

    [Fact(Timeout = 120_000)]
    public async Task FailureBeforeCutoverPreservesPreviousDeployment()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);

        await fixture.CommitAndPushAsync("A", buildFails: false);
        var deploymentA = await fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-a"));
        await fixture.CommitAndPushAsync("B", buildFails: false);
        await fixture.SetStaticOutputDirectoryAsync("missing-output");

        var failure = await Assert.ThrowsAsync<DeploymentException>(() =>
            fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-b")));

        Assert.Equal("path.missing", failure.Code);
        Assert.Equal(deploymentA.Deployment.Id, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("A", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task StaticDeploymentRejectsRepositoryRemoteDriftBeforeCreatingTheMirror()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);
        await fixture.CommitAndPushAsync("reviewed", buildFails: false);
        fixture.RedirectRepositoryRemote();

        var failure = await Assert.ThrowsAsync<DeploymentException>(() =>
            fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-redirected-remote")));

        Assert.Equal("repository.remote_mismatch", failure.Code);
        Assert.False(fixture.MirrorExists);
    }

    [Fact(Timeout = 120_000)]
    public async Task StaticDeploymentAcceptsReviewedCanonicalLocalRemoteInsideProjectsRoot()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);
        var commit = await fixture.CommitAndPushAsync("reviewed-local", buildFails: false);

        var deployment = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-reviewed-local"));

        Assert.Equal(commit, deployment.Deployment.CommitSha);
        Assert.Equal("reviewed-local", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task StaticDeploymentIgnoresTwoRuleAmbientInsteadOfRewriteChain()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);
        var commit = await fixture.CommitAndPushAsync("rewrite-isolated", buildFails: false);

        using (fixture.UseAmbientInsteadOfRewriteChain())
        {
            var deployment = await fixture.Engine.DeployAsync(
                "demo", OperationExecutionContext.CreateSystem("deploy-rewrite-isolated"));
            Assert.Equal(commit, deployment.Deployment.CommitSha);
        }

        Assert.Equal("rewrite-isolated", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task StaticDeploymentIgnoresRepositoryLocalInsteadOfRewrite()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);
        var commit = await fixture.CommitAndPushAsync("local-rewrite-isolated", buildFails: false);
        fixture.ConfigureRepositoryLocalInsteadOfRewrite();

        var deployment = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-local-rewrite-isolated"));

        Assert.Equal(commit, deployment.Deployment.CommitSha);
        Assert.Equal("local-rewrite-isolated", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task RestartMarksNonactiveInterruptedCandidateFailedAndPreservesActiveRelease()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);
        await fixture.CommitAndPushAsync("A", buildFails: false);
        var deploymentA = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-a"));
        var interrupted = await fixture.InsertInterruptedCandidateAsync();

        await fixture.Engine.ReconcileInterruptedDeploymentsAsync();

        var recovered = await fixture.GetDeploymentAsync(interrupted.Id);
        Assert.Equal(DeploymentStatus.Failed.ToString(), recovered.State);
        Assert.False(recovered.IsKnownGood);
        Assert.Contains("before this candidate became active", recovered.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(deploymentA.Deployment.Id, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("A", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task RestartReprovesAndFinalizesActiveInterruptedStaticCutover()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);
        await fixture.CommitAndPushAsync("A", buildFails: false);
        await fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-a"));
        await fixture.CommitAndPushAsync("B", buildFails: false);
        var deploymentB = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-b"));
        await fixture.MarkInterruptedAsync(deploymentB.Deployment.Id);

        await fixture.Engine.ReconcileInterruptedDeploymentsAsync();

        var recovered = await fixture.GetDeploymentAsync(deploymentB.Deployment.Id);
        Assert.Equal(DeploymentStatus.Succeeded.ToString(), recovered.State);
        Assert.True(recovered.IsKnownGood);
        Assert.Equal("healthy", recovered.HealthOutcome);
        Assert.Equal(deploymentB.Deployment.Id, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("B", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task RestartFallsBackWhenActiveInterruptedStaticArtifactDisappeared()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);
        await fixture.CommitAndPushAsync("A", buildFails: false);
        var deploymentA = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-a"));
        await fixture.CommitAndPushAsync("B", buildFails: false);
        var deploymentB = await fixture.Engine.DeployAsync(
            "demo", OperationExecutionContext.CreateSystem("deploy-b"));
        await fixture.MarkInterruptedAsync(deploymentB.Deployment.Id);
        Directory.Delete(Path.Combine(deploymentB.Deployment.WorktreePath, "dist"), recursive: true);

        await fixture.Engine.ReconcileInterruptedDeploymentsAsync();

        var failed = await fixture.GetDeploymentAsync(deploymentB.Deployment.Id);
        Assert.Equal(DeploymentStatus.Failed.ToString(), failed.State);
        Assert.False(failed.IsKnownGood);
        Assert.Equal(deploymentA.Deployment.Id, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("A", await fixture.GetGatewayVersionAsync());
    }

    [Fact(Timeout = 120_000)]
    public async Task CancellationDuringPostCutoverRollbackDrainKeepsRollbackActive()
    {
        await using var fixture = await DeploymentFixture.CreateAsync(staticDeployment: true);

        await fixture.CommitAndPushAsync("A", buildFails: false);
        var deploymentA = await fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-a"));
        await fixture.CommitAndPushAsync("B", buildFails: false);
        var deploymentB = await fixture.Engine.DeployAsync("demo", OperationExecutionContext.CreateSystem("deploy-b"));
        await fixture.SetDrainSecondsAsync(30);

        using var cancellation = new CancellationTokenSource();
        var rollingBack = fixture.Engine.RollbackAsync(
            "demo", deploymentA.Deployment.Id, OperationExecutionContext.CreateSystem("rollback-a"), cancellation.Token);
        await fixture.WaitForActiveDeploymentAsync(deploymentA.Deployment.Id);
        Assert.Equal("A", await fixture.GetGatewayVersionAsync());

        cancellation.Cancel();
        var failure = await Assert.ThrowsAsync<DeploymentException>(async () => await rollingBack);

        Assert.Equal("rollback.cleanup_failed", failure.Code);
        Assert.Equal(deploymentA.Deployment.Id, fixture.ActiveRoute().DeploymentId);
        Assert.NotEqual(deploymentB.Deployment.Id, fixture.ActiveRoute().DeploymentId);
        Assert.Equal("A", await fixture.GetGatewayVersionAsync());
    }

    private static string? FindExecutable(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private sealed class DeploymentFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _repository;
        private readonly ApplicationSupervisor _supervisor;
        private readonly WebApplication _gateway;
        private readonly HttpClient _client;
        private readonly GatewayRouteRegistry _routes;
        private readonly AbodeToWebDatabase _database;
        private readonly ManifestProvider _manifest;
        private readonly EndpointTestProcessLauncher _processLauncher;

        private DeploymentFixture(string root, string repository, DeploymentEngine engine, ApplicationSupervisor supervisor,
            WebApplication gateway, HttpClient client, GatewayRouteRegistry routes, AbodeToWebDatabase database,
            ManifestProvider manifest, EndpointTestProcessLauncher processLauncher)
        {
            _root = root;
            _repository = repository;
            Engine = engine;
            _supervisor = supervisor;
            _gateway = gateway;
            _client = client;
            _routes = routes;
            _database = database;
            _manifest = manifest;
            _processLauncher = processLauncher;
        }

        internal DeploymentEngine Engine { get; }

        internal static async Task<DeploymentFixture> CreateAsync(
            int drainSeconds = 0,
            bool staticDeployment = false,
            bool dockerCompose = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "AbodeToWeb-e2e-" + Guid.NewGuid().ToString("N"));
            var repository = Path.Combine(root, "repository");
            var remote = Path.Combine(root, "remote.git");
            Directory.CreateDirectory(root);
            RunGit(root, "init", "--bare", remote);
            RunGit(root, "init", "-b", "main", repository);
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
            var manifest = CreateManifest(repository, remote, drainSeconds, staticDeployment, dockerCompose);
            await File.WriteAllTextAsync(paths.ConfigurationFile,
                JsonSerializer.Serialize(manifest, JsonManifestParser.CreateSerializerOptions()));
            var parser = new YamlManifestParser();
            var validator = new ManifestValidator();
            // The Docker cleanup fixture substitutes Node for the Docker CLI so
            // the engine's scoped-down path is testable without Docker Desktop.
            var provider = new ManifestProvider(paths, parser, item => dockerCompose
                ? new AbodeToWeb.Core.Validation.ValidationResult()
                : validator.Validate(item,
                    new ManifestValidationContext(new Dictionary<string, string>(), true,
                        AllowLocalRepositoryRemotesForTests: true)));
            var loaded = await provider.LoadAsync();
            Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Errors.Select(error => error.Message)));

            var database = new AbodeToWebDatabase(paths);
            await database.InitializeAsync();
            var routes = new GatewayRouteRegistry(database);
            await routes.InitializeAsync(provider.Current);
            var hub = new LogStreamHub();
            var logs = new BoundedLogStore(paths, hub);
            var processLauncher = new EndpointTestProcessLauncher();
            var supervisor = new ApplicationSupervisor(logs, NullLogger<ApplicationSupervisor>.Instance, processLauncher);
            var runner = new StructuredCommandRunner(logs, processLauncher);
            var git = new GitClient(runner, provider, paths, allowLocalRepositoryRemotesForTests: true);
            var healthClient = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var health = new ApplicationHealthProbe(healthClient);
            var endpoints = new HostedEndpointVerifier(new WindowsTcpListenerSnapshotProvider(), supervisor);
            var secrets = new SecretFileStore(paths);
            var engine = new DeploymentEngine(provider, paths, database, git, runner, supervisor, health, endpoints, routes, secrets,
                new DeterministicPortAllocator(21_000, 25_000));

            var gatewayPort = FreePort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{gatewayPort}");
            builder.Services.AddSingleton(routes);
            builder.Services.AddSingleton(provider);
            builder.Services.AddSingleton<ICloudflareAccessTokenValidator>(
                new CloudflareAccessTokenValidator(new HttpClient(new SocketsHttpHandler { UseProxy = false })));
            builder.Services.AddHttpForwarder();
            builder.Services.AddSingleton<GatewayForwarderClient>();
            builder.Services.AddSingleton<GatewayMiddleware>();
            var gateway = builder.Build();
            gateway.Run(context => context.RequestServices.GetRequiredService<GatewayMiddleware>().HandleAsync(context));
            await gateway.StartAsync();
            var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{gatewayPort}") };
            return new DeploymentFixture(
                root, repository, engine, supervisor, gateway, client, routes, database, provider, processLauncher);
        }

        internal async Task<string> CommitAndPushAsync(
            string version,
            bool buildFails,
            string listenerAddress = "127.0.0.1",
            bool startsListener = true)
        {
            Directory.CreateDirectory(Path.Combine(_repository, "dist"));
            await File.WriteAllTextAsync(Path.Combine(_repository, "dist", "index.html"), version);
            var serverScript = startsListener
                ? $$"""
                    const { spawn } = require('child_process');
                    const version = '{{version}}';
                    const listenAddress = '{{listenerAddress}}';
                    const childCode = `
                      const http = require('http');
                      const version = process.env.ABODE_TO_WEB_FIXTURE_VERSION;
                      const port = Number(process.env.PORT);
                      const server = http.createServer((req, res) => {
                        if (req.url === '/health') { res.writeHead(200, {'content-type':'text/plain'}); res.end('ok'); return; }
                        res.writeHead(200, {'content-type':'text/plain'}); res.end(version);
                      });
                      server.listen(port, process.env.ABODE_TO_WEB_FIXTURE_LISTEN_ADDRESS);
                    `;
                    const child = spawn(process.execPath, ['-e', childCode], {
                      env: {
                        ...process.env,
                        ABODE_TO_WEB_FIXTURE_VERSION: version,
                        ABODE_TO_WEB_FIXTURE_LISTEN_ADDRESS: listenAddress
                      },
                      stdio: ['ignore', 'inherit', 'inherit']
                    });
                    process.stdin.resume();
                    process.stdin.on('end', () => {
                      child.kill('SIGTERM');
                      setTimeout(() => process.exit(0), 50);
                    });
                    """
                : "process.stdin.resume();";
            var composeDownMarker = JsonSerializer.Serialize(Path.Combine(_repository, "compose-down.marker"));
            serverScript =
                $"if (process.argv.includes('down')) {{ require('fs').writeFileSync({composeDownMarker}, 'down'); process.exit(0); }}" +
                Environment.NewLine + serverScript;
            await File.WriteAllTextAsync(Path.Combine(_repository, "server.js"), serverScript);
            await File.WriteAllTextAsync(Path.Combine(_repository, "build.js"), buildFails
                ? "console.error('intentional build failure'); process.exit(42);"
                : "console.log('build ok');");
            RunGit(_repository, "add", "server.js", "build.js", "dist/index.html");
            RunGit(_repository, "commit", "-m", "Version " + version);
            RunGit(_repository, "push", "-u", "origin", "main");
            return RunGit(_repository, "rev-parse", "HEAD").Trim();
        }

        internal GatewayRoute ActiveRoute()
        {
            Assert.True(_routes.TryGetByApp("demo", out var route));
            return route;
        }

        internal async Task<GatewayRoute> WaitForDifferentActiveDeploymentAsync(string deploymentId)
        {
            var timeout = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < timeout)
            {
                if (_routes.TryGetByApp("demo", out var route)
                    && !string.Equals(route.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase))
                    return route;
                await Task.Delay(25);
            }
            throw new TimeoutException("The candidate route did not become active.");
        }

        internal async Task WaitForActiveDeploymentAsync(string deploymentId)
        {
            var timeout = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < timeout)
            {
                if (_routes.TryGetByApp("demo", out var route)
                    && string.Equals(route.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase))
                    return;
                await Task.Delay(25);
            }
            throw new TimeoutException("The expected route did not become active.");
        }

        internal async Task<DeploymentRecord> GetDeploymentAsync(string deploymentId) =>
            await _database.GetDeploymentAsync(deploymentId)
            ?? throw new InvalidOperationException("The committed deployment record is missing.");

        internal async Task<DeploymentRecord> InsertInterruptedCandidateAsync()
        {
            var id = Guid.NewGuid().ToString("N");
            var sha = new string('f', 40);
            var appRoot = Path.GetDirectoryName((await _database.GetDeploymentsAsync("demo", 1)).Single().WorktreePath)!;
            var worktree = Path.Combine(appRoot, $"{sha[..12]}-{id[..8]}");
            Directory.CreateDirectory(worktree);
            var record = new DeploymentRecord(
                id, "demo", "interrupted-operation", sha, "main", worktree,
                DeploymentStatus.Building.ToString(), DateTimeOffset.UtcNow.AddSeconds(-5), null,
                null, null, null, "Interrupted candidate", "AbodeToWeb Tests", false, null, null);
            await _database.InsertDeploymentAsync(record);
            return record;
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

        internal async Task SetDrainSecondsAsync(int drainSeconds) => await _manifest.UpdateAtomicAsync(current => current with
        {
            Apps = current.Apps.Select(app => app.Id.Equals("demo", StringComparison.OrdinalIgnoreCase)
                ? app with { Deployment = app.Deployment with { DrainSeconds = drainSeconds } }
                : app).ToList()
        });

        internal async Task SetStaticOutputDirectoryAsync(string outputDirectory) => await _manifest.UpdateAtomicAsync(current => current with
        {
            Apps = current.Apps.Select(app => app.Id.Equals("demo", StringComparison.OrdinalIgnoreCase)
                ? app with { Deployment = app.Deployment with { StaticOutputDirectory = outputDirectory } }
                : app).ToList()
        });

        internal async Task SetHealthTypeAsync(HealthCheckType healthType) =>
            await _manifest.UpdateAtomicAsync(current => current with
            {
                Apps = current.Apps.Select(app => app.Id.Equals("demo", StringComparison.OrdinalIgnoreCase)
                    ? app with { Health = app.Health with { Type = healthType } }
                    : app).ToList()
            });

        internal void BindForeignListenerOnNextApplicationStart() =>
            _processLauncher.BindForeignListenerOnNextApplicationStart();

        internal async Task<bool> ForeignListenerStillAcceptsConnectionsAsync() =>
            await _processLauncher.ForeignListenerStillAcceptsConnectionsAsync();

        internal bool ComposeDownWasInvoked =>
            File.Exists(Path.Combine(_repository, "compose-down.marker"));

        internal bool MirrorExists =>
            Directory.Exists(Path.Combine(_root, "data", "state", "repositories", "demo.git"));

        internal void RedirectRepositoryRemote()
        {
            var replacement = Path.Combine(_root, "replacement.git");
            RunGit(_root, "init", "--bare", replacement);
            RunGit(_repository, "remote", "set-url", "origin", replacement);
        }

        internal IDisposable UseAmbientInsteadOfRewriteChain()
        {
            var sourceRemote = RunGit(_repository, "config", "--local", "--no-includes", "--get", "remote.origin.url").Trim();
            var firstRewrite = Path.Combine(_root, "rewrite-first.git").Replace('\\', '/');
            var secondRewrite = Path.Combine(_root, "rewrite-second.git").Replace('\\', '/');
            var config = Path.Combine(_root, "ambient-gitconfig");
            File.WriteAllText(config,
                $$"""
                [url "{{firstRewrite}}"]
                    insteadOf = {{sourceRemote}}
                [url "{{secondRewrite}}"]
                    insteadOf = {{firstRewrite}}
                """);
            return new AmbientGitConfigScope(config);
        }

        internal void ConfigureRepositoryLocalInsteadOfRewrite()
        {
            var sourceRemote = RunGit(_repository, "config", "--local", "--no-includes", "--get", "remote.origin.url").Trim();
            var replacement = Path.Combine(_root, "repository-local-rewrite.git").Replace('\\', '/');
            RunGit(_repository, "config", "--local", $"url.{replacement}.insteadOf", sourceRemote);
        }

        internal async Task AssertActiveEndpointOwnedByChildAsync(string deploymentId)
        {
            var route = ActiveRoute();
            var port = Assert.IsType<int>(route.InternalPort);
            var runtime = Assert.Single(_supervisor.GetSnapshots(), snapshot =>
                string.Equals(snapshot.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase)
                && snapshot.State == ManagedRuntimeState.Running);
            var processTree = await _supervisor.CaptureProcessTreeAsync(runtime.InstanceId);
            Assert.NotNull(processTree);
            var endpoint = Assert.Single(new WindowsTcpListenerSnapshotProvider().Capture(), listener =>
                listener.LocalPort == port
                && processTree.ProcessIds.Contains(listener.OwningProcessId));
            Assert.NotEqual(processTree.RootProcessId, endpoint.OwningProcessId);
        }

        internal async Task<string> GetGatewayVersionAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Host = "demo.test";
            using var response = await _client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return content;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _gateway.StopAsync();
            await _gateway.DisposeAsync();
            await _supervisor.DisposeAsync();
            _processLauncher.Dispose();
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
            int drainSeconds,
            bool staticDeployment,
            bool dockerCompose) => new()
            {
                ManifestVersion = 1,
                AbodeToWeb = new AbodeToWebOptions
                {
                    ProjectsRoots = [Path.GetDirectoryName(repository)!],
                    ExperimentalProjectExecution = !staticDeployment,
                    Dashboard = new ListenerOptions { Bind = "127.0.0.1", Port = 45_451 },
                    Gateway = new ListenerOptions { Bind = "127.0.0.1", Port = 45_480 },
                    Storage = new StorageOptions { RetainedDeploymentsPerApp = 5 }
                },
                Apps =
            [
                new AppManifest
                {
                    Id = "demo", Name = "Demo", Enabled = true,
                    Repository = new RepositoryOptions
                    {
                        Provider = RepositoryProvider.Other,
                        LocalPath = repository,
                        Remote = "origin",
                        ExpectedRemoteUrl = remote,
                        Branch = "main"
                    },
                    Deployment = staticDeployment
                        ? new DeploymentOptions
                        {
                            Adapter = DeploymentAdapter.Static, Mode = DeploymentMode.Worktree,
                            StaticOutputDirectory = "dist", CandidateStartupTimeoutSeconds = 15, DrainSeconds = drainSeconds
                        }
                        : new DeploymentOptions
                        {
                            Adapter = dockerCompose ? DeploymentAdapter.DockerCompose : DeploymentAdapter.Node,
                            Mode = DeploymentMode.Worktree,
                            Build = new AbodeToWeb.Core.Processes.CommandSpec { Executable = "node.exe", Arguments = ["build.js"], TimeoutSeconds = 20 },
                            Start = dockerCompose
                                ? new AbodeToWeb.Core.Processes.CommandSpec
                                {
                                    Executable = "node.exe",
                                    Arguments = ["server.js", "up"],
                                    TimeoutSeconds = 86_400
                                }
                                : new AbodeToWeb.Core.Processes.CommandSpec
                                {
                                    Executable = "node.exe",
                                    Arguments = ["server.js"],
                                    TimeoutSeconds = 86_400
                                },
                            CandidateStartupTimeoutSeconds = 15, DrainSeconds = drainSeconds
                        },
                    Runtime = new RuntimeOptions { RestartPolicy = RestartPolicy.OnFailure, MaxRestarts = 2, RestartWindowSeconds = 30,
                        StableRuntimeSeconds = 10, GracefulShutdownSeconds = 1 },
                    Health = new HealthCheckOptions { Type = HealthCheckType.Http, Path = "/health", IntervalSeconds = 2,
                        TimeoutSeconds = 1, HealthyThreshold = 1, UnhealthyThreshold = 1 },
                    Routing = new RoutingOptions { Hostname = "demo.test" }
                }
            ]
            };

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static string RunGit(string workingDirectory, params string[] arguments)
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
            // Fixture repos must not inherit developer or machine git config;
            // e.g. a global core.excludesFile ignoring dist/ breaks the add below.
            process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Git fixture command failed: " + error);
            return output;
        }

        private static void ClearReadOnlyAttributes(string root)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }

        private sealed class AmbientGitConfigScope : IDisposable
        {
            private static readonly Lock Gate = new();
            private readonly string? _global;
            private readonly string? _noSystem;
            private bool _disposed;

            internal AmbientGitConfigScope(string config)
            {
                lock (Gate)
                {
                    _global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
                    _noSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
                    Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
                    Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
                }
            }

            public void Dispose()
            {
                lock (Gate)
                {
                    if (_disposed) return;
                    Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", _global);
                    Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", _noSystem);
                    _disposed = true;
                }
            }
        }

        private sealed class EndpointTestProcessLauncher : IProcessLauncher, IDisposable
        {
            private readonly object _gate = new();
            private TcpListener? _foreignListener;
            private bool _bindForeignListener;

            internal void BindForeignListenerOnNextApplicationStart()
            {
                lock (_gate) _bindForeignListener = true;
            }

            public LaunchedProcess Start(ProcessStartInfo startInfo, string jobName, string? isolationKey)
            {
                lock (_gate)
                {
                    if (_bindForeignListener
                        && startInfo.ArgumentList.Any(argument => argument.Equals("server.js", StringComparison.OrdinalIgnoreCase)))
                    {
                        _bindForeignListener = false;
                        var port = int.Parse(
                            startInfo.Environment["PORT"]
                            ?? throw new InvalidOperationException("The candidate port was not provided."),
                            System.Globalization.CultureInfo.InvariantCulture);
                        _foreignListener = new TcpListener(IPAddress.Loopback, port);
                        _foreignListener.Server.ExclusiveAddressUse = true;
                        _foreignListener.Start();
                    }
                }

                return SuspendedProcessLauncher.Start(startInfo, jobName, isolationKey: null);
            }

            internal async Task<bool> ForeignListenerStillAcceptsConnectionsAsync()
            {
                TcpListener? listener;
                lock (_gate) listener = _foreignListener;
                if (listener is null) return false;
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using var client = new TcpClient(AddressFamily.InterNetwork);
                await client.ConnectAsync(IPAddress.Loopback, port);
                return client.Connected;
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _foreignListener?.Stop();
                    _foreignListener = null;
                }
            }
        }
    }
}
