using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Deployments;
using AbodeToWeb.Core.Networking;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Core.Supervision;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Health;
using AbodeToWeb.Infrastructure.Operations;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Processes;
using AbodeToWeb.Infrastructure.Runtime;
using AbodeToWeb.Infrastructure.Security;

namespace AbodeToWeb.Infrastructure.Deployments;

public sealed record DeploymentExecutionResult(DeploymentRecord Deployment, bool RouteChanged, string SafeMessage);

public sealed class DeploymentEngine
{
    private readonly ManifestProvider _manifest;
    private readonly AbodeToWebPaths _paths;
    private readonly AbodeToWebDatabase _database;
    private readonly GitClient _git;
    private readonly StructuredCommandRunner _commands;
    private readonly ApplicationSupervisor _supervisor;
    private readonly ApplicationHealthProbe _health;
    private readonly HostedEndpointVerifier _endpoints;
    private readonly GatewayRouteRegistry _routes;
    private readonly SecretFileStore _secrets;
    private readonly IPortAllocator _ports;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveInstance> _active = new(StringComparer.OrdinalIgnoreCase);

    public DeploymentEngine(
        ManifestProvider manifest,
        AbodeToWebPaths paths,
        AbodeToWebDatabase database,
        GitClient git,
        StructuredCommandRunner commands,
        ApplicationSupervisor supervisor,
        ApplicationHealthProbe health,
        HostedEndpointVerifier endpoints,
        GatewayRouteRegistry routes,
        SecretFileStore secrets,
        IPortAllocator ports)
    {
        _manifest = manifest;
        _paths = paths;
        _database = database;
        _git = git;
        _commands = commands;
        _supervisor = supervisor;
        _health = health;
        _endpoints = endpoints;
        _routes = routes;
        _secrets = secrets;
        _ports = ports;
        _supervisor.StateChanged += OnRuntimeStateChangedAsync;
    }

    public async Task<DeploymentExecutionResult> DeployAsync(string appId, OperationExecutionContext operation, CancellationToken cancellationToken = default)
    {
        var app = GetApp(appId);
        EnsureProjectExecutionEnabled(app);
        if (app.Repository is null) throw new DeploymentException("repository.required", "This project does not have a Git repository configured.");
        if (app.Deployment.Mode != DeploymentMode.Worktree)
            throw new DeploymentException("deployment.mode_unsafe", "Deploy requires isolated worktree mode. Local development folders are never updated in place.");

        var gate = _locks.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new DeploymentException("deployment.conflict", "Another deployment for this project is already running.");

        DeploymentRecord? record = null;
        string? candidateInstanceId = null;
        string? candidateWorkingDirectory = null;
        GatewayRoute? previousRoute = null;
        ActiveInstance? activeBeforeSwitch = null;
        System.Diagnostics.Stopwatch? candidateStartup = null;
        var routeChanged = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await operation.ReportAsync(5, "Validating repository");
            await _git.ValidateRepositoryAsync(app.Repository, app.Id, operation.OperationId, cancellationToken);
            await operation.ReportAsync(10, "Fetching remote branch");
            await _git.FetchAsync(app.Repository, app.Id, operation.OperationId, cancellationToken);
            await operation.ReportAsync(16, "Resolving exact commit");
            var commit = await _git.ResolveRemoteCommitAsync(app.Repository, app.Id, operation.OperationId, cancellationToken);

            var deploymentGuid = Guid.NewGuid();
            var deploymentId = deploymentGuid.ToString("N");
            var worktree = Path.Combine(_paths.DeploymentsRoot, app.Id, $"{commit.Sha[..12]}-{deploymentId[..8]}");
            EnsureUnderDeploymentRoot(worktree);
            record = new DeploymentRecord(deploymentId, app.Id, operation.OperationId, commit.Sha, app.Repository.Branch,
                worktree, DeploymentStatus.Queued.ToString(), DateTimeOffset.UtcNow, null, null, null, null,
                commit.Message, commit.Author, false, null, null);
            await _database.InsertDeploymentAsync(record, cancellationToken);
            previousRoute = _routes.TryGetByApp(app.Id, out var existingRoute) ? existingRoute : null;
            var previousGuid = Guid.TryParseExact(previousRoute?.DeploymentId, "N", out var parsedPrevious) ? parsedPrevious : (Guid?)null;
            var state = DeploymentStateMachine.Create(deploymentGuid, app.Id, commit.Sha, record.StartedUtc, previousGuid);

            state = await StageAsync(state, DeploymentStatus.PreparingWorktree, record, operation, 22, "Creating isolated worktree", cancellationToken);
            await _git.CreateWorktreeAsync(app.Repository.LocalPath, commit.Sha, worktree, app.Id, operation.OperationId, cancellationToken);
            var workingDirectory = ResolveWithinWorktree(worktree, app.Deployment.WorkingDirectory, requireExisting: true);
            candidateWorkingDirectory = workingDirectory;
            var port = AllocateCandidatePort(app, previousRoute);
            // Runtime secrets are never exposed to dependency installation or build
            // commands. Build-time secrets are intentionally outside the v1 model.
            var buildEnvironment = CreateEnvironment(app, port, includeSecrets: false);

            if (app.Deployment.Install is not null)
            {
                state = await StageAsync(state, DeploymentStatus.Installing, record, operation, 34, "Installing dependencies", cancellationToken);
                await RunRequiredCommandAsync(app, app.Deployment.Install, workingDirectory, buildEnvironment, operation.OperationId, "Dependency installation", cancellationToken);
            }
            if (app.Deployment.Build is not null)
            {
                state = await StageAsync(state, DeploymentStatus.Building, record, operation, 52, "Building candidate", cancellationToken);
                await RunRequiredCommandAsync(app, app.Deployment.Build, workingDirectory, buildEnvironment, operation.OperationId, "Build", cancellationToken);
            }

            state = await StageAsync(state, DeploymentStatus.StartingCandidate, record, operation, 68, "Starting candidate", cancellationToken);
            string? staticPath = null;
            if (app.Deployment.Adapter == DeploymentAdapter.Static)
            {
                staticPath = ResolveStaticOutput(app, workingDirectory);
                RevokeRuntimeDirectoryAccess(app, workingDirectory);
                port = null;
            }
            else
            {
                if (app.Deployment.Start is null) throw new DeploymentException("start.command_required", "This project does not have a start command.");
                candidateInstanceId = $"{app.Id}-candidate-{deploymentId}";
                candidateStartup = System.Diagnostics.Stopwatch.StartNew();
                await _supervisor.StartAsync(CreateProcessRequest(app, record, candidateInstanceId, workingDirectory, port!.Value), cancellationToken);
            }

            state = await StageAsync(state, DeploymentStatus.VerifyingCandidate, record, operation, 76, "Checking candidate health", cancellationToken);
            var healthy = app.Deployment.Adapter == DeploymentAdapter.Static
                ? (true, AbodeToWeb.Core.Health.HealthSnapshot.Unknown, new ProbeResult(true, "Static output is ready.", TimeSpan.Zero))
                : await _health.WaitForHealthyAsync(app.Health, port,
                    () => candidateInstanceId is not null && _supervisor.TryGetSnapshot(candidateInstanceId, out var snapshot)
                        && snapshot.State == ManagedRuntimeState.Running,
                    RemainingCandidateStartup(app, candidateStartup), cancellationToken);
            state = DeploymentStateMachine.RecordCandidateHealth(state, healthy.Item1, DateTimeOffset.UtcNow, healthy.Item3.Detail);
            if (!healthy.Item1)
                throw new DeploymentException("candidate.unhealthy", "The candidate did not pass its health check. The previous deployment remains online.");
            if (candidateInstanceId is not null)
            {
                await RequireStableCandidateEndpointAsync(
                    app,
                    candidateInstanceId,
                    port!.Value,
                    candidateStartup ?? throw new InvalidOperationException("Candidate startup timing was unavailable."),
                    cancellationToken);
            }

            state = DeploymentStateMachine.Transition(state, DeploymentStatus.SwitchingRoute, DateTimeOffset.UtcNow, "Candidate is healthy; switching gateway route.");
            await operation.ReportAsync(86, "Switching gateway route");
            activeBeforeSwitch = _active.TryGetValue(app.Id, out var currentActive) ? currentActive : null;
            await _routes.SetActiveAsync(app, deploymentId, port, staticPath, GatewayRouteStatus.Healthy, cancellationToken);
            // SetActiveAsync is the cutover commit point. Once it succeeds, the
            // candidate is authoritative and must never be stopped or replaced
            // with the previous route by post-cutover error handling.
            routeChanged = true;
            if (candidateInstanceId is not null)
            {
                _active[app.Id] = CreateActiveInstance(app, candidateInstanceId, deploymentId, port, workingDirectory);
                if (!_supervisor.TryGetSnapshot(candidateInstanceId, out var committedRuntime)
                    || committedRuntime.State != ManagedRuntimeState.Running)
                    await _routes.SetStatusAsync(app.Id,
                        committedRuntime?.State is ManagedRuntimeState.Starting or ManagedRuntimeState.Backoff
                            ? GatewayRouteStatus.Starting : GatewayRouteStatus.Unhealthy,
                        CancellationToken.None);
            }
            state = DeploymentStateMachine.RecordRouteActivated(state, DateTimeOffset.UtcNow);

            if (previousRoute is not null && !string.Equals(previousRoute.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase))
            {
                state = DeploymentStateMachine.Transition(state, DeploymentStatus.DrainingPrevious, DateTimeOffset.UtcNow, "Draining previous deployment.");
                await operation.ReportAsync(93, "Draining previous deployment");
                await Task.Delay(TimeSpan.FromSeconds(app.Deployment.DrainSeconds), cancellationToken);
                if (activeBeforeSwitch is not null && activeBeforeSwitch.InstanceId != candidateInstanceId)
                {
                    await StopManagedInstanceAsync(app, activeBeforeSwitch, false, operation.OperationId, cancellationToken);
                }
                else
                {
                    var previousSnapshot = _supervisor.GetSnapshots().FirstOrDefault(item => item.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase)
                        && item.DeploymentId == previousRoute.DeploymentId && item.InstanceId != candidateInstanceId);
                    if (previousSnapshot is not null)
                    {
                        await _supervisor.StopAsync(previousSnapshot.InstanceId, cancellationToken: cancellationToken);
                        var previousDeployment = previousRoute.DeploymentId is null
                            ? null : await _database.GetDeploymentAsync(previousRoute.DeploymentId, cancellationToken);
                        if (previousDeployment is not null)
                            RevokeRuntimeDirectoryAccess(app, ResolveWithinWorktree(
                                previousDeployment.WorktreePath, app.Deployment.WorkingDirectory, requireExisting: true));
                    }
                }
            }

            state = DeploymentStateMachine.Transition(state, DeploymentStatus.Succeeded, DateTimeOffset.UtcNow, "Deployment completed.");
            record = record with
            {
                State = state.Status.ToString(),
                CompletedUtc = DateTimeOffset.UtcNow,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                BuildOutcome = "succeeded",
                HealthOutcome = "healthy",
                IsKnownGood = true,
                InternalPort = port
            };
            await _database.UpdateDeploymentAsync(record, cancellationToken);
            await SaveProjectStateAsync(app, "running", "running", "healthy", record, candidateInstanceId, null, cancellationToken);
            await operation.ReportAsync(100, "Deployment complete");
            await EnforceRetentionAsync(app, deploymentId, operation.OperationId, cancellationToken);
            return new DeploymentExecutionResult(record, true, "The healthy deployment is now active.");
        }
        catch (Exception exception)
        {
            var candidateMustRemain = routeChanged;
            if (!candidateMustRemain && candidateInstanceId is not null)
            {
                if (app.Deployment.Adapter == DeploymentAdapter.DockerCompose
                    && record is not null
                    && candidateWorkingDirectory is not null)
                    try { await RunComposeDownAsync(app, candidateWorkingDirectory, record.Id, operation.OperationId, CancellationToken.None); } catch { }
                try { await _supervisor.StopAsync(candidateInstanceId, true, CancellationToken.None); } catch { }
            }
            if (!candidateMustRemain && candidateWorkingDirectory is not null)
            {
                try { RevokeRuntimeDirectoryAccess(app, candidateWorkingDirectory); } catch { }
                if (app.Repository is not null && record is not null && Directory.Exists(record.WorktreePath))
                    try
                    {
                        await _git.RemoveWorktreeAsync(app.Repository.LocalPath, record.WorktreePath, app.Id,
                            operation.OperationId, CancellationToken.None);
                    }
                    catch { }
            }
            if (record is not null)
            {
                record = record with
                {
                    State = (candidateMustRemain ? DeploymentStatus.Succeeded : DeploymentStatus.Failed).ToString(),
                    CompletedUtc = DateTimeOffset.UtcNow,
                    DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                    BuildOutcome = candidateMustRemain || routeChanged ? "succeeded" : "failed",
                    HealthOutcome = routeChanged ? "healthy" : "failed",
                    IsKnownGood = candidateMustRemain,
                    FailureReason = candidateMustRemain
                        ? "The healthy deployment is serving traffic, but post-deployment cleanup did not complete."
                        : SafeMessage(exception)
                };
                await _database.UpdateDeploymentAsync(record, CancellationToken.None);
            }
            if (candidateMustRemain)
                throw new DeploymentException("deployment.cleanup_failed",
                    "The healthy deployment is serving traffic, but AbodeToWeb could not finish retiring the previous release. Check the deployment log.", exception);
            if (exception is DeploymentException) throw;
            throw new DeploymentException("deployment.failed", SafeMessage(exception), exception);
        }
        finally { gate.Release(); }
    }

    public async Task<DeploymentExecutionResult> RollbackAsync(string appId, string deploymentId, OperationExecutionContext operation, CancellationToken cancellationToken = default)
    {
        var app = GetApp(appId);
        EnsureRetainedArtifactAllowed(app);
        var retained = await _database.GetDeploymentAsync(deploymentId, cancellationToken)
            ?? throw new DeploymentException("rollback.not_found", "The selected deployment no longer exists.");
        if (!retained.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase) || !retained.IsKnownGood || !Directory.Exists(retained.WorktreePath))
            throw new DeploymentException("rollback.not_retained", "The selected deployment is not a retained known-good release.");

        var gate = _locks.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken)) throw new DeploymentException("deployment.conflict", "Another deployment action is in progress.");
        string? candidateId = null;
        string? candidateWorkingDirectory = null;
        GatewayRoute? previousRoute = null;
        ActiveInstance? activeBeforeSwitch = null;
        var routeChanged = false;
        try
        {
            await operation.ReportAsync(15, "Preparing retained deployment");
            var workingDirectory = ResolveWithinWorktree(retained.WorktreePath, app.Deployment.WorkingDirectory, true);
            candidateWorkingDirectory = workingDirectory;
            previousRoute = _routes.TryGetByApp(app.Id, out var currentRoute) ? currentRoute : null;
            var port = app.Deployment.Adapter == DeploymentAdapter.Static ? null : AllocateCandidatePort(app, previousRoute);
            string? staticPath = null;
            if (app.Deployment.Adapter == DeploymentAdapter.Static)
                staticPath = ResolveStaticOutput(app, workingDirectory);
            else
            {
                candidateId = $"{app.Id}-rollback-{retained.Id}";
                await _supervisor.StartAsync(CreateProcessRequest(app, retained, candidateId, workingDirectory, port!.Value), cancellationToken);
                await operation.ReportAsync(48, "Checking rollback candidate");
                var health = await _health.WaitForHealthyAsync(app.Health, port,
                    () => _supervisor.TryGetSnapshot(candidateId, out var snapshot) && snapshot.State == ManagedRuntimeState.Running,
                    TimeSpan.FromSeconds(app.Deployment.CandidateStartupTimeoutSeconds), cancellationToken);
                if (!health.Healthy) throw new DeploymentException("rollback.unhealthy", "The retained deployment did not become healthy; the current release remains online.");
            }

            await operation.ReportAsync(75, "Switching gateway route");
            activeBeforeSwitch = _active.TryGetValue(app.Id, out var active) ? active : null;
            await _routes.SetActiveAsync(app, retained.Id, port, staticPath, GatewayRouteStatus.Healthy, cancellationToken);
            // As with a normal deployment, successful route activation commits
            // the rollback. Cleanup errors may leave the old process running,
            // but must not undo the healthy cutover.
            routeChanged = true;
            if (candidateId is not null)
            {
                _active[app.Id] = CreateActiveInstance(app, candidateId, retained.Id, port, workingDirectory);
                if (!_supervisor.TryGetSnapshot(candidateId, out var committedRuntime)
                    || committedRuntime.State != ManagedRuntimeState.Running)
                    await _routes.SetStatusAsync(app.Id,
                        committedRuntime?.State is ManagedRuntimeState.Starting or ManagedRuntimeState.Backoff
                            ? GatewayRouteStatus.Starting : GatewayRouteStatus.Unhealthy,
                        CancellationToken.None);
            }
            await Task.Delay(TimeSpan.FromSeconds(app.Deployment.DrainSeconds), cancellationToken);
            if (activeBeforeSwitch is not null && activeBeforeSwitch.InstanceId != candidateId)
            {
                await StopManagedInstanceAsync(app, activeBeforeSwitch, false, operation.OperationId, cancellationToken);
            }
            await SaveProjectStateAsync(app, "running", "running", "healthy", retained, candidateId, null, cancellationToken);
            await operation.ReportAsync(100, "Rollback complete");
            return new DeploymentExecutionResult(retained, true, "The retained deployment is active.");
        }
        catch (Exception exception)
        {
            var candidateMustRemain = routeChanged;
            if (!candidateMustRemain && candidateId is not null)
            {
                if (app.Deployment.Adapter == DeploymentAdapter.DockerCompose && candidateWorkingDirectory is not null)
                    try { await RunComposeDownAsync(app, candidateWorkingDirectory, retained.Id, operation.OperationId, CancellationToken.None); } catch { }
                try { await _supervisor.StopAsync(candidateId, true, CancellationToken.None); } catch { }
            }
            if (!candidateMustRemain && candidateWorkingDirectory is not null)
                try { RevokeRuntimeDirectoryAccess(app, candidateWorkingDirectory); } catch { }
            if (candidateMustRemain)
                throw new DeploymentException("rollback.cleanup_failed",
                    "The rollback is serving traffic, but AbodeToWeb could not finish retiring the previous release. Check the deployment log.", exception);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task StartAsync(string appId, OperationExecutionContext operation, CancellationToken cancellationToken = default)
    {
        var app = GetApp(appId);
        EnsureRetainedArtifactAllowed(app);
        var gate = await EnterActionAsync(app.Id, cancellationToken);
        try { await StartCoreAsync(app, operation, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task StartCoreAsync(AppManifest app, OperationExecutionContext operation, CancellationToken cancellationToken)
    {
        EnsureRetainedArtifactAllowed(app);
        if (_active.TryGetValue(app.Id, out var current)
            && _supervisor.TryGetSnapshot(current.InstanceId, out var snapshot)
            && snapshot.State == ManagedRuntimeState.Running) return;
        if (!_routes.TryGetByApp(app.Id, out var route) || string.IsNullOrWhiteSpace(route.DeploymentId))
            throw new DeploymentException("start.no_deployment", "Deploy this project before starting it.");
        var deployment = await _database.GetDeploymentAsync(route.DeploymentId, cancellationToken)
            ?? throw new DeploymentException("start.deployment_missing", "The active deployment record is missing.");
        if (!Directory.Exists(deployment.WorktreePath)) throw new DeploymentException("start.artifact_missing", "The active deployment directory is missing. Deploy or roll back to repair it.");

        await operation.ReportAsync(20, "Starting project");
        if (app.Deployment.Adapter == DeploymentAdapter.Static)
        {
            var staticWorkingDirectory = ResolveWithinWorktree(deployment.WorktreePath, app.Deployment.WorkingDirectory, true);
            var staticPath = ResolveStaticOutput(app, staticWorkingDirectory);
            await _routes.SetActiveAsync(app, deployment.Id, null, staticPath, GatewayRouteStatus.Healthy, cancellationToken);
            await SaveProjectStateAsync(app, "running", "running", "healthy", deployment, null, null, cancellationToken);
            return;
        }
        var port = AllocateCandidatePort(app, null);
        var workingDirectory = ResolveWithinWorktree(deployment.WorktreePath, app.Deployment.WorkingDirectory, true);
        var instanceId = $"{app.Id}-active-{deployment.Id}";
        await _supervisor.StartAsync(CreateProcessRequest(app, deployment, instanceId, workingDirectory, port!.Value), cancellationToken);
        var health = await _health.WaitForHealthyAsync(app.Health, port,
            () => _supervisor.TryGetSnapshot(instanceId, out var state) && state.State == ManagedRuntimeState.Running,
            TimeSpan.FromSeconds(app.Deployment.CandidateStartupTimeoutSeconds), cancellationToken);
        if (!health.Healthy)
        {
            await _supervisor.StopAsync(instanceId, true, cancellationToken);
            await _routes.SetStatusAsync(app.Id, GatewayRouteStatus.Unhealthy, cancellationToken);
            throw new DeploymentException("start.unhealthy", "The project started but did not become healthy.");
        }
        await _routes.SetActiveAsync(app, deployment.Id, port, null, GatewayRouteStatus.Healthy, cancellationToken);
        _active[app.Id] = CreateActiveInstance(app, instanceId, deployment.Id, port, workingDirectory);
        await SaveProjectStateAsync(app, "running", "running", "healthy", deployment, instanceId, null, cancellationToken);
        await operation.ReportAsync(100, "Project is running");
    }

    public async Task StopAsync(string appId, OperationExecutionContext operation, bool force = false, CancellationToken cancellationToken = default)
    {
        var app = GetApp(appId);
        var gate = await EnterActionAsync(app.Id, cancellationToken);
        try { await StopCoreAsync(app, operation, force, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync(AppManifest app, OperationExecutionContext operation, bool force, CancellationToken cancellationToken)
    {
        await operation.ReportAsync(20, force ? "Force stopping process tree" : "Stopping project");
        if (_active.TryRemove(app.Id, out var active)) await StopManagedInstanceAsync(app, active, force, operation.OperationId, cancellationToken);
        else
        {
            var running = _supervisor.GetSnapshots().Where(snapshot => snapshot.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var snapshot in running) await _supervisor.StopAsync(snapshot.InstanceId, force, cancellationToken);
        }
        await _routes.SetStatusAsync(app.Id, GatewayRouteStatus.Stopped, cancellationToken);
        var deployment = _routes.TryGetByApp(app.Id, out var route) && route.DeploymentId is not null
            ? await _database.GetDeploymentAsync(route.DeploymentId, cancellationToken) : null;
        await SaveProjectStateAsync(app, "stopped", "stopped", "stopped", deployment, null, null, cancellationToken);
        await operation.ReportAsync(100, "Project stopped");
    }

    public async Task RestartAsync(string appId, OperationExecutionContext operation, CancellationToken cancellationToken = default)
    {
        var app = GetApp(appId);
        EnsureRetainedArtifactAllowed(app);
        var gate = await EnterActionAsync(app.Id, cancellationToken);
        try
        {
            await StopCoreAsync(app, operation, false, cancellationToken);
            await StartCoreAsync(app, operation, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<RepositoryGitStatus> CheckForUpdatesAsync(string appId, string operationId, CancellationToken cancellationToken = default)
    {
        var app = GetApp(appId);
        if (app.Repository is null) throw new DeploymentException("repository.required", "This project does not have a repository.");
        var gate = await EnterActionAsync(app.Id, cancellationToken);
        try
        {
            string? deployedSha = null;
            if (_routes.TryGetByApp(app.Id, out var route) && route.DeploymentId is not null)
                deployedSha = (await _database.GetDeploymentAsync(route.DeploymentId, cancellationToken))?.CommitSha;
            return await _git.GetStatusAsync(app.Repository, app.Id, operationId, true, deployedSha, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task StopForConfigurationChangeAsync(
        AppManifest previousApp,
        OperationExecutionContext operation,
        CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(previousApp.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await StopCoreAsync(previousApp, operation, false, cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task ReconcileInterruptedDeploymentsAsync(CancellationToken cancellationToken = default)
    {
        var interrupted = await _database.GetInterruptedDeploymentsAsync(cancellationToken);
        foreach (var deployment in interrupted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var app = _manifest.Current.Apps.FirstOrDefault(candidate =>
                candidate.Id.Equals(deployment.AppId, StringComparison.OrdinalIgnoreCase));
            var isActive = _routes.TryGetByApp(deployment.AppId, out var route)
                && string.Equals(route.DeploymentId, deployment.Id, StringComparison.OrdinalIgnoreCase);

            if (!isActive || app is null || !app.Enabled || app.Visibility == AppVisibility.Disabled)
            {
                await MarkInterruptedDeploymentFailedAsync(deployment,
                    "AbodeToWeb restarted before this candidate became active.", cancellationToken);
                if (!isActive && app?.Repository is not null)
                    await RemoveInterruptedWorktreeAsync(app, deployment, cancellationToken);
                continue;
            }

            try
            {
                await StartAsync(app.Id,
                    OperationExecutionContext.CreateSystem("recover-" + deployment.Id), cancellationToken);
                if (!_routes.TryGetByApp(app.Id, out var recoveredRoute)
                    || !IsRecoveredDeploymentHealthy(app, deployment, recoveredRoute))
                    throw new DeploymentException("recovery.unhealthy",
                        "The interrupted active deployment could not be proven healthy after restart.");

                var completedUtc = DateTimeOffset.UtcNow;
                var recovered = deployment with
                {
                    State = DeploymentStatus.Succeeded.ToString(),
                    CompletedUtc = completedUtc,
                    DurationMilliseconds = DurationMilliseconds(deployment.StartedUtc, completedUtc),
                    BuildOutcome = "succeeded",
                    HealthOutcome = "healthy",
                    IsKnownGood = true,
                    FailureReason = null,
                    InternalPort = recoveredRoute.InternalPort
                };
                await _database.UpdateDeploymentAsync(recovered, cancellationToken);
                await AppendRecoveryAuditBestEffortAsync(recovered, "succeeded",
                    "Interrupted active deployment was re-proven healthy after restart.", cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                              || !cancellationToken.IsCancellationRequested)
            {
                Exception recoveryError = exception;
                await MarkInterruptedDeploymentFailedAsync(deployment,
                    "The interrupted active deployment did not recover after restart.", CancellationToken.None);
                var fallback = (await _database.GetDeploymentsAsync(app.Id, 500, CancellationToken.None))
                    .Where(candidate => candidate.IsKnownGood
                        && !candidate.Id.Equals(deployment.Id, StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(candidate.WorktreePath))
                    .OrderByDescending(candidate => candidate.CompletedUtc)
                    .FirstOrDefault();
                if (fallback is not null)
                {
                    try
                    {
                        await RollbackAsync(app.Id, fallback.Id,
                            OperationExecutionContext.CreateSystem("recover-fallback-" + fallback.Id),
                            CancellationToken.None);
                        await AppendRecoveryAuditBestEffortAsync(deployment, "rolled-back",
                            "Recovery restored the most recent retained known-good deployment.", CancellationToken.None);
                        continue;
                    }
                    catch (Exception fallbackException)
                    {
                        if (_routes.TryGetByApp(app.Id, out var fallbackRoute)
                            && IsRecoveredDeploymentHealthy(app, fallback, fallbackRoute))
                        {
                            await AppendRecoveryAuditBestEffortAsync(deployment, "rolled-back",
                                "The retained release is serving, but cleanup needs attention.", CancellationToken.None);
                            continue;
                        }
                        recoveryError = new AggregateException(recoveryError, fallbackException);
                    }
                }

                await _routes.SetStatusAsync(app.Id, GatewayRouteStatus.Unhealthy, CancellationToken.None);
                await AppendRecoveryAuditBestEffortAsync(deployment, "failed", SafeMessage(recoveryError), CancellationToken.None);
            }
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var states = (await _database.GetProjectStatesAsync(cancellationToken)).ToDictionary(state => state.AppId, StringComparer.OrdinalIgnoreCase);
        foreach (var app in _manifest.Current.Apps.Where(app =>
                     app.Enabled
                     && (!states.TryGetValue(app.Id, out var persisted) || persisted.RuntimeState != "circuit-open")
                     && (app.Autostart || (states.TryGetValue(app.Id, out var state) && state.DesiredState == "running"))))
        {
            try
            {
                await StartAsync(app.Id, OperationExecutionContext.CreateSystem("reconcile"), cancellationToken);
            }
            catch (Exception exception)
            {
                await _routes.SetStatusAsync(app.Id, GatewayRouteStatus.Unhealthy, cancellationToken);
                await SaveProjectStateAsync(app, "running", "failed", "unhealthy", null, null, SafeMessage(exception), cancellationToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(_manifest.Current.AbodeToWeb.StartupStaggerSeconds), cancellationToken);
        }
    }

    private async Task MarkInterruptedDeploymentFailedAsync(
        DeploymentRecord deployment,
        string reason,
        CancellationToken cancellationToken)
    {
        var completedUtc = DateTimeOffset.UtcNow;
        await _database.UpdateDeploymentAsync(deployment with
        {
            State = DeploymentStatus.Failed.ToString(),
            CompletedUtc = completedUtc,
            DurationMilliseconds = DurationMilliseconds(deployment.StartedUtc, completedUtc),
            BuildOutcome = deployment.BuildOutcome ?? "interrupted",
            HealthOutcome = deployment.HealthOutcome ?? "interrupted",
            IsKnownGood = false,
            FailureReason = reason
        }, cancellationToken);
        await AppendRecoveryAuditBestEffortAsync(deployment, "interrupted", reason, cancellationToken);
    }

    private async Task RemoveInterruptedWorktreeAsync(
        AppManifest app,
        DeploymentRecord deployment,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsOwnedDeploymentWorktree(app, deployment) || !Directory.Exists(deployment.WorktreePath)) return;
            RevokeRuntimeDirectoryAccess(app, ResolveWithinWorktree(
                deployment.WorktreePath, app.Deployment.WorkingDirectory, requireExisting: true));
            await _git.RemoveWorktreeAsync(app.Repository!.LocalPath, deployment.WorktreePath,
                app.Id, "recover-" + deployment.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is DeploymentException or IOException
                                            or UnauthorizedAccessException or ArgumentException)
        {
            // The row is durably terminal even when conservative ownership checks
            // prevent automatic artifact cleanup. A later successful deployment's
            // retention pass can retry through Git's worktree registry.
        }
    }

    private bool IsOwnedDeploymentWorktree(AppManifest app, DeploymentRecord deployment)
    {
        if (deployment.Id.Length < 8 || deployment.CommitSha.Length < 12) return false;
        var expectedParent = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(_paths.DeploymentsRoot, app.Id)));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(deployment.WorktreePath));
        return string.Equals(Path.GetDirectoryName(fullPath), expectedParent, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(fullPath).Equals(
                $"{deployment.CommitSha[..12]}-{deployment.Id[..8]}", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsRecoveredDeploymentHealthy(
        AppManifest app,
        DeploymentRecord deployment,
        GatewayRoute route)
    {
        if (!string.Equals(route.DeploymentId, deployment.Id, StringComparison.OrdinalIgnoreCase)
            || route.Status != GatewayRouteStatus.Healthy) return false;
        if (app.Deployment.Adapter == DeploymentAdapter.Static)
            return route.StaticPath is not null && StaticDeploymentContent.ContainsServableFile(route.StaticPath);
        return _supervisor.GetSnapshots().Any(snapshot =>
            snapshot.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(snapshot.DeploymentId, deployment.Id, StringComparison.OrdinalIgnoreCase)
            && snapshot.State == ManagedRuntimeState.Running);
    }

    private async Task AppendRecoveryAuditBestEffortAsync(
        DeploymentRecord deployment,
        string outcome,
        string detail,
        CancellationToken cancellationToken)
    {
        var detailJson = System.Text.Json.JsonSerializer.Serialize(new { detail });
        try
        {
            await _database.AppendAuditAsync(new AuditEventRecord(
                0, DateTimeOffset.UtcNow, "AbodeToWeb", "deployment-recovery", deployment.AppId,
                outcome, deployment.OperationId, detailJson, null), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { }
    }

    private static long DurationMilliseconds(DateTimeOffset startedUtc, DateTimeOffset completedUtc) =>
        Math.Max(0, (long)Math.Min(long.MaxValue, (completedUtc - startedUtc).TotalMilliseconds));

    private ManagedProcessRequest CreateProcessRequest(AppManifest app, DeploymentRecord deployment, string instanceId, string workingDirectory, int port)
    {
        EnsureProjectExecutionEnabled(app);
        if (app.Deployment.Start is null) throw new DeploymentException("start.command_required", "Start command is not configured.");
        var environment = CreateEnvironment(app, port, true);
        if (app.Deployment.Adapter == DeploymentAdapter.DockerCompose)
            environment["COMPOSE_PROJECT_NAME"] = ComposeProjectName(app.Id, deployment.Id);
        return new ManagedProcessRequest(instanceId, app.Id, deployment.Id, app.Deployment.Start, workingDirectory,
            environment, RestartPolicySettings.From(app.Runtime), TimeSpan.FromSeconds(app.Runtime.GracefulShutdownSeconds),
            EnableAutomaticRestart: false);
    }

    private async Task RequireStableCandidateEndpointAsync(
        AppManifest app,
        string instanceId,
        int port,
        System.Diagnostics.Stopwatch candidateStartup,
        CancellationToken cancellationToken)
    {
        var startupAllowance = TimeSpan.FromSeconds(app.Deployment.CandidateStartupTimeoutSeconds);
        HostedEndpointVerification? lastResult = null;

        while (candidateStartup.Elapsed < startupAllowance)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = startupAllowance - candidateStartup.Elapsed;
            if (remaining <= TimeSpan.Zero) break;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(remaining);
            try
            {
                lastResult = await _endpoints.VerifyStableAsync(
                    instanceId,
                    port,
                    async probeCancellationToken =>
                    {
                        var running = _supervisor.TryGetSnapshot(instanceId, out var snapshot)
                                      && snapshot.State == ManagedRuntimeState.Running;
                        var probe = await _health.ProbeAsync(
                            app.Health,
                            port,
                            running,
                            probeCancellationToken);
                        return probe.Success
                               && _supervisor.TryGetSnapshot(instanceId, out var afterProbe)
                               && afterProbe.State == ManagedRuntimeState.Running;
                    },
                    deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastResult = new HostedEndpointVerification(
                    false,
                    HostedEndpointVerificationCodes.FinalProbeFailed,
                    "The managed endpoint could not be proven before the candidate startup timeout.");
            }

            if (lastResult.IsVerified) return;
            if (!lastResult.Code.Equals(HostedEndpointVerificationCodes.ListenerMissing, StringComparison.Ordinal))
                throw EndpointAttestationException(lastResult);

            remaining = startupAllowance - candidateStartup.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(
                TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds)),
                cancellationToken);
        }

        throw EndpointAttestationException(lastResult ?? new HostedEndpointVerification(
            false,
            HostedEndpointVerificationCodes.ListenerMissing,
            "The managed application did not expose its expected loopback listener before the candidate startup timeout."));
    }

    private static DeploymentException EndpointAttestationException(HostedEndpointVerification result) =>
        new(result.Code, $"{result.SafeMessage} The active route was not changed.");

    private static TimeSpan RemainingCandidateStartup(
        AppManifest app,
        System.Diagnostics.Stopwatch? candidateStartup)
    {
        if (candidateStartup is null) return TimeSpan.Zero;
        var remaining = TimeSpan.FromSeconds(app.Deployment.CandidateStartupTimeoutSeconds) - candidateStartup.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private async Task<SemaphoreSlim> EnterActionAsync(string appId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new DeploymentException("project.action_conflict", "Another action for this project is already running.");
        return gate;
    }

    private static ActiveInstance CreateActiveInstance(AppManifest app, string instanceId, string deploymentId, int? port, string workingDirectory) =>
        new(instanceId, deploymentId, port, workingDirectory,
            app.Deployment.Adapter == DeploymentAdapter.DockerCompose ? ComposeProjectName(app.Id, deploymentId) : null);

    private async Task StopManagedInstanceAsync(AppManifest app, ActiveInstance active, bool force, string operationId, CancellationToken cancellationToken)
    {
        if (app.Deployment.Adapter == DeploymentAdapter.DockerCompose)
            await RunComposeDownAsync(app, active.WorkingDirectory, active.DeploymentId, operationId, cancellationToken);
        await _supervisor.StopAsync(active.InstanceId, force, cancellationToken);
        RevokeRuntimeDirectoryAccess(app, active.WorkingDirectory);
    }

    private async Task RunComposeDownAsync(AppManifest app, string workingDirectory, string deploymentId, string operationId, CancellationToken cancellationToken)
    {
        if (app.Deployment.Start is null) return;
        var arguments = app.Deployment.Start.Arguments.ToList();
        var upIndex = arguments.FindIndex(argument => argument.Equals("up", StringComparison.OrdinalIgnoreCase));
        arguments = upIndex >= 0 ? arguments.Take(upIndex).ToList() : ["compose"];
        arguments.Add("down");
        arguments.Add("--timeout");
        arguments.Add(app.Runtime.GracefulShutdownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var environment = CreateEnvironment(app, null, true);
        environment["COMPOSE_PROJECT_NAME"] = ComposeProjectName(app.Id, deploymentId);
        var command = new CommandSpec { Executable = app.Deployment.Start.Executable, Arguments = arguments, TimeoutSeconds = Math.Max(30, app.Runtime.GracefulShutdownSeconds + 15) };
        var result = await _commands.RunAsync(new CommandExecutionRequest(command, workingDirectory, environment, app.Id, operationId,
            IsolationKey: app.Id), cancellationToken);
        if (!result.Succeeded) throw new DeploymentException("docker.stop_failed", "Docker Compose did not stop cleanly. Check the runtime log and Docker Desktop.");
    }

    private static string ComposeProjectName(string appId, string deploymentId) =>
        $"abode-to-web-{appId}-{deploymentId[..Math.Min(8, deploymentId.Length)]}".ToLowerInvariant();

    private async Task RunRequiredCommandAsync(AppManifest app, CommandSpec command, string workingDirectory,
        IReadOnlyDictionary<string, string?> environment, string operationId, string stageName, CancellationToken cancellationToken)
    {
        EnsureProjectExecutionEnabled(app);
        var result = await _commands.RunAsync(new CommandExecutionRequest(command, workingDirectory, environment, app.Id, operationId,
            IsolationKey: app.Id), cancellationToken);
        if (!result.Succeeded)
            throw new DeploymentException("build.command_failed", $"{stageName} failed. The previous deployment remains online.");
    }

    private async Task<DeploymentState> StageAsync(DeploymentState state, DeploymentStatus status, DeploymentRecord record,
        OperationExecutionContext operation, int percent, string message, CancellationToken cancellationToken)
    {
        var next = DeploymentStateMachine.Transition(state, status, DateTimeOffset.UtcNow, message);
        await _database.UpdateDeploymentAsync(record with { State = status.ToString() }, cancellationToken);
        await operation.ReportAsync(percent, message);
        return next;
    }

    private int? AllocateCandidatePort(AppManifest app, GatewayRoute? previous)
    {
        if (app.Deployment.Adapter == DeploymentAdapter.Static) return null;
        var unavailable = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        foreach (var route in _routes.Snapshot()) if (route.InternalPort is not null) unavailable.Add(route.InternalPort.Value);
        if (previous is null && app.Runtime.InternalPort is { } preferred && !unavailable.Contains(preferred))
        {
            var availability = PortCollisionDetector.CheckLoopback(preferred);
            if (availability.Available) return preferred;
        }
        var candidate = _ports.Allocate(app.Id + "-" + Guid.NewGuid().ToString("N"), unavailable);
        var check = PortCollisionDetector.CheckLoopback(candidate);
        if (!check.Available) throw new DeploymentException("port.collision", check.Message);
        return candidate;
    }

    private Dictionary<string, string?> CreateEnvironment(AppManifest app, int? port, bool includeSecrets)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (includeSecrets)
            foreach (var pair in _secrets.ReadForProcess(app.Id)) result[pair.Key] = pair.Value;
        // Control-plane values always win even if a protected file was edited
        // outside AbodeToWeb and predates the reserved-key policy.
        result[app.Deployment.PortEnvironmentVariable] = port?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        result["ABODE_TO_WEB_PROJECT_ID"] = app.Id;
        result["ABODE_TO_WEB_MANAGED"] = "1";
        result["CI"] = "true";
        return result;
    }

    private async Task SaveProjectStateAsync(AppManifest app, string desired, string runtime, string health, DeploymentRecord? deployment,
        string? instanceId, string? error, CancellationToken cancellationToken)
    {
        ManagedProcessSnapshot? snapshot = null;
        if (instanceId is not null && _supervisor.TryGetSnapshot(instanceId, out var current)) snapshot = current;
        await _database.UpsertProjectStateAsync(new ProjectStateRecord(app.Id, desired, runtime, health, deployment?.Id,
            snapshot?.ProcessId, snapshot?.StartedUtc, snapshot?.ExecutablePath, snapshot?.RestartCount ?? 0,
            snapshot?.RetryAfter, DateTimeOffset.UtcNow, error), cancellationToken);
    }

    private async Task OnRuntimeStateChangedAsync(ManagedProcessSnapshot snapshot)
    {
        if (!_active.TryGetValue(snapshot.AppId, out var active) || active.InstanceId != snapshot.InstanceId) return;
        var status = snapshot.State switch
        {
            ManagedRuntimeState.Running or ManagedRuntimeState.Starting or ManagedRuntimeState.Backoff => GatewayRouteStatus.Starting,
            ManagedRuntimeState.Stopped => GatewayRouteStatus.Stopped,
            _ => GatewayRouteStatus.Unhealthy
        };
        await _routes.SetStatusAsync(snapshot.AppId, status);

        var app = _manifest.Current.Apps.FirstOrDefault(item =>
            item.Id.Equals(snapshot.AppId, StringComparison.OrdinalIgnoreCase));
        if (app is null) return;
        var deployment = await _database.GetDeploymentAsync(active.DeploymentId);
        var runtimeState = snapshot.State switch
        {
            ManagedRuntimeState.Running => "running",
            ManagedRuntimeState.Starting or ManagedRuntimeState.Backoff => "starting",
            ManagedRuntimeState.CircuitOpen => "circuit-open",
            ManagedRuntimeState.Stopped => "stopped",
            _ => "failed"
        };
        var healthState = status switch
        {
            GatewayRouteStatus.Healthy => "healthy",
            GatewayRouteStatus.Starting => "starting",
            GatewayRouteStatus.Stopped => "stopped",
            _ => "unhealthy"
        };
        await SaveProjectStateAsync(app, "running", runtimeState, healthState, deployment,
            snapshot.InstanceId, snapshot.LastError, CancellationToken.None);
    }

    private async Task EnforceRetentionAsync(AppManifest app, string activeDeploymentId, string operationId, CancellationToken cancellationToken)
    {
        if (app.Repository is null) return;
        var deployments = await _database.GetDeploymentsAsync(app.Id, 500, cancellationToken);
        var retained = deployments.Where(item => item.IsKnownGood).OrderByDescending(item => item.CompletedUtc).Take(_manifest.Current.AbodeToWeb.Storage.RetainedDeploymentsPerApp)
            .Select(item => item.Id).Append(activeDeploymentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in deployments.Where(item => !retained.Contains(item.Id) && Directory.Exists(item.WorktreePath)))
        {
            try { await _git.RemoveWorktreeAsync(app.Repository.LocalPath, old.WorktreePath, app.Id, operationId, cancellationToken); }
            catch (DeploymentException) { }
        }
    }

    private AppManifest GetApp(string appId)
    {
        if (_manifest.IsProjectIdRetired(appId))
            throw new DeploymentException("project.retired",
                "This project ID was permanently retired and cannot run or deploy again.");
        var app = _manifest.Current.Apps.FirstOrDefault(app => app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Project '{appId}' was not found.");
        if (!app.Enabled || app.Visibility == AppVisibility.Disabled)
            throw new DeploymentException("project.disabled", "This project is disabled in the manifest.");
        return app;
    }

    private void EnsureProjectExecutionEnabled(AppManifest app)
    {
        if (!RequiresProjectExecution(app) || _manifest.Current.AbodeToWeb.ExperimentalProjectExecution) return;
        throw new DeploymentException(
            "deployment.execution.experimentalDisabled",
            "Project-controlled install, build, and start execution is preview-only. Use a prebuilt static app with no commands, or explicitly enable experimental project execution after reviewing the shared runtime trust boundary.");
    }

    private void EnsureRetainedArtifactAllowed(AppManifest app)
    {
        EnsureProjectExecutionEnabled(app);
        if (!RequiresProjectExecution(app)) return;
        throw new DeploymentException(
            "artifact.redeploy_required",
            "Projects that execute install, build, or start commands must use a fresh reviewed deployment. Retained executable artifacts cannot be started or rolled back until sealed artifacts are available.");
    }

    private static bool RequiresProjectExecution(AppManifest app) =>
        app.Deployment.Adapter != DeploymentAdapter.Static
        || app.Deployment.Install is not null
        || app.Deployment.Build is not null
        || app.Deployment.Start is not null;

    private void EnsureUnderDeploymentRoot(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.DeploymentsRoot)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new DeploymentException("path.invalid", "Deployment path escaped the AbodeToWeb deployment root.");
    }

    private static string ResolveWithinWorktree(string root, string relativePath, bool requireExisting)
    {
        if (Path.IsPathFullyQualified(relativePath)) throw new DeploymentException("path.absolute", "Deployment paths must be relative to the worktree.");
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(relativePath, root);
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) && !Path.TrimEndingDirectorySeparator(fullPath).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException("path.escape", "A deployment path escaped its isolated worktree.");
        if (requireExisting && !Directory.Exists(fullPath)) throw new DeploymentException("path.missing", "A configured deployment directory does not exist.");
        if (Directory.Exists(fullPath))
        {
            var current = new DirectoryInfo(fullPath);
            while (current is not null && current.FullName.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new DeploymentException("path.reparse_point", "Deployment paths cannot traverse junctions or symbolic links.");
                current = current.Parent;
            }
        }
        return fullPath;
    }

    private static string ResolveStaticOutput(AppManifest app, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(app.Deployment.StaticOutputDirectory))
            throw new DeploymentException("static.output_required", "Static deployment output is not configured.");
        var staticPath = ResolveWithinWorktree(
            workingDirectory, app.Deployment.StaticOutputDirectory, requireExisting: true);
        if (!StaticDeploymentContent.ContainsServableFile(staticPath))
            throw new DeploymentException("static.output_empty", "The static build output has no servable files.");
        return staticPath;
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        DeploymentException deployment => deployment.SafeMessage,
        RuntimeCredentialException credential => credential.Message,
        TimeoutException => "The deployment timed out. The previous deployment remains online.",
        UnauthorizedAccessException => "AbodeToWeb cannot access a required repository or deployment path.",
        IOException => "A required deployment file was unavailable. The previous deployment remains online.",
        _ => "Deployment failed. The previous healthy deployment remains online."
    };

    private static void RevokeRuntimeDirectoryAccess(AppManifest app, string workingDirectory)
    {
        if (!RequiresProjectExecution(app)
            || !OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH"))) return;
        RuntimeProcessIsolation.RevokeDirectoryAccess(workingDirectory);
    }

    private sealed record ActiveInstance(string InstanceId, string DeploymentId, int? Port, string WorkingDirectory, string? ComposeProjectName);
}
