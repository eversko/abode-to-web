using System.Collections.Concurrent;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Health;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Deployments;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Health;
using AbodeToWeb.Infrastructure.Logging;
using AbodeToWeb.Infrastructure.Operations;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Runtime;
using AbodeToWeb.Infrastructure.Security;

namespace AbodeToWeb.Server.Services;

public sealed partial class AbodeToWebInitializer(
    AbodeToWebDatabase database,
    ManifestProvider manifest,
    GatewayRouteRegistry routes,
    DeploymentEngine deployments,
    SecretFileStore secrets,
    ILogger<AbodeToWebInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken);
        var retiredProjectIds = await database.GetRetiredProjectIdsAsync(cancellationToken);
        manifest.SetRetiredProjectIds(retiredProjectIds);
        foreach (var retiredProjectId in retiredProjectIds)
            await secrets.DeleteAsync(retiredProjectId, cancellationToken);
        var retiredInManifest = manifest.CurrentRetiredProjectIds();
        if (retiredInManifest.Count > 0)
            throw new InvalidOperationException(
                $"The manifest contains permanently retired project ID(s): {string.Join(", ", retiredInManifest)}. Remove those entries or choose new IDs before starting AbodeToWeb.");
        await routes.InitializeAsync(manifest.Current, cancellationToken);
        // Finish or fail interrupted cutovers before Kestrel accepts a new
        // administrative action for the same project.
        await deployments.ReconcileInterruptedDeploymentsAsync(cancellationToken);
        LogInitialized(logger, database.DatabaseFile);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(301, LogLevel.Information, "AbodeToWeb state initialized at {databaseFile}")]
    private static partial void LogInitialized(ILogger logger, string databaseFile);
}

public sealed partial class RuntimeReconciliationService(DeploymentEngine deployments, ILogger<RuntimeReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            await deployments.ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { LogReconciliationFailed(logger, exception); }
    }

    [LoggerMessage(302, LogLevel.Error, "Runtime reconciliation failed; projects can still be started manually")]
    private static partial void LogReconciliationFailed(ILogger logger, Exception exception);
}

public sealed partial class ApplicationHealthMonitoringService(
    ManifestProvider manifest,
    GatewayRouteRegistry routes,
    ApplicationSupervisor supervisor,
    HostedEndpointVerifier endpointVerifier,
    ApplicationHealthProbe probe,
    AbodeToWeb.Infrastructure.AbodeToWebPaths paths,
    ILogger<ApplicationHealthMonitoringService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, HealthSnapshot> _health = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastChecked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _runtimeGeneration = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var app in manifest.Current.Apps.Where(app => app.Enabled))
            {
                if (!routes.TryGetByApp(app.Id, out var route) || route.Status is GatewayRouteStatus.Stopped or GatewayRouteStatus.Disabled) continue;
                if (route.StaticPath is not null)
                {
                    var staticStatus = IsStaticDeploymentAvailable(paths.DeploymentsRoot, app.Id, route.StaticPath)
                        ? GatewayRouteStatus.Healthy
                        : GatewayRouteStatus.Unhealthy;
                    if (staticStatus != route.Status) await routes.SetStatusAsync(app.Id, staticStatus, stoppingToken);
                    continue;
                }
                var now = DateTimeOffset.UtcNow;
                if (_lastChecked.TryGetValue(app.Id, out var checkedAt) && now - checkedAt < TimeSpan.FromSeconds(app.Health.IntervalSeconds)) continue;
                _lastChecked[app.Id] = now;
                try
                {
                    var managed = route.DeploymentId is null ? null : supervisor.GetSnapshots().FirstOrDefault(snapshot =>
                            snapshot.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(snapshot.DeploymentId, route.DeploymentId, StringComparison.OrdinalIgnoreCase)
                            && snapshot.State == ManagedRuntimeState.Running);
                    var running = managed is not null;
                    if (managed is not null)
                    {
                        var generation = $"{managed.DeploymentId}:{managed.InstanceId}:{managed.StartedUtc:O}";
                        if (!_runtimeGeneration.TryGetValue(app.Id, out var observed)
                            || !observed.Equals(generation, StringComparison.Ordinal))
                        {
                            _runtimeGeneration[app.Id] = generation;
                            _health[app.Id] = HealthStateMachine.MarkStarted(HealthSnapshot.Unknown, now);
                        }
                    }
                    ProbeResult? result = null;
                    if (managed is not null)
                    {
                        var attestation = route.InternalPort is > 0 and <= 65_535
                            ? await endpointVerifier.VerifyStableAsync(
                                managed.InstanceId,
                                route.InternalPort.Value,
                                async probeCancellationToken =>
                                {
                                    var stillRunning = supervisor.TryGetSnapshot(managed.InstanceId, out var current)
                                                       && current.State == ManagedRuntimeState.Running;
                                    result = await probe.ProbeAsync(
                                        app.Health,
                                        route.InternalPort,
                                        stillRunning,
                                        probeCancellationToken);
                                    return result.Success
                                           && supervisor.TryGetSnapshot(managed.InstanceId, out var afterProbe)
                                           && afterProbe.State == ManagedRuntimeState.Running;
                                },
                                stoppingToken)
                            : new HostedEndpointVerification(
                                false,
                                HostedEndpointVerificationCodes.InspectionFailed,
                                "AbodeToWeb could not prove the managed application's expected endpoint.");
                        if (!attestation.IsVerified
                            && !attestation.Code.Equals(
                                HostedEndpointVerificationCodes.FinalProbeFailed,
                                StringComparison.Ordinal))
                        {
                            await QuarantineAttestationViolationAsync(
                                app.Id,
                                managed.InstanceId,
                                managed.DeploymentId!,
                                attestation,
                                now,
                                stoppingToken);
                            continue;
                        }

                        result ??= new ProbeResult(
                            false,
                            "The managed application did not pass its direct health probe.",
                            TimeSpan.Zero);
                    }
                    else
                    {
                        result = await probe.ProbeAsync(app.Health, route.InternalPort, running, stoppingToken);
                    }
                    var snapshot = _health.GetOrAdd(app.Id, _ => HealthStateMachine.MarkStarted(HealthSnapshot.Unknown, now));
                    snapshot = HealthStateMachine.Apply(snapshot,
                        result.Success ? HealthObservation.Succeeded(result.Detail) : HealthObservation.Failed(result.Detail),
                        new HealthThresholds(app.Health.HealthyThreshold, app.Health.UnhealthyThreshold), now);
                    _health[app.Id] = snapshot;
                    var status = snapshot.State switch
                    {
                        HealthState.Healthy => GatewayRouteStatus.Healthy,
                        HealthState.Unhealthy => GatewayRouteStatus.Unhealthy,
                        _ => route.Status
                    };
                    if (status != route.Status) await routes.SetStatusAsync(app.Id, status, stoppingToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    LogHealthProbeFailed(logger, app.Id, exception);
                }
            }
        }
    }

    [LoggerMessage(303, LogLevel.Debug, "Health probe failed for {appId}")]
    private static partial void LogHealthProbeFailed(ILogger logger, string appId, Exception exception);

    [LoggerMessage(307, LogLevel.Error, "Endpoint attestation failed for {appId} instance {instanceId}: {code}; the supervised job is being stopped")]
    private static partial void LogEndpointAttestationFailed(
        ILogger logger,
        string appId,
        string instanceId,
        string code);

    [LoggerMessage(308, LogLevel.Error, "Endpoint attestation failed for {appId} instance {instanceId}, and its supervised job could not be stopped")]
    private static partial void LogAttestationStopFailed(
        ILogger logger,
        Exception exception,
        string appId,
        string instanceId);

    private async Task QuarantineAttestationViolationAsync(
        string appId,
        string instanceId,
        string deploymentId,
        HostedEndpointVerification attestation,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        LogEndpointAttestationFailed(logger, appId, instanceId, attestation.Code);
        _health[appId] = new HealthSnapshot(
            HealthState.Unhealthy,
            ConsecutiveSuccesses: 0,
            ConsecutiveFailures: 1,
            LastCheckedAt: observedAt,
            StateChangedAt: observedAt,
            Detail: attestation.SafeMessage);

        try
        {
            // The Job Object is the only termination boundary here. Never open or
            // terminate the foreign PID reported by the listener snapshot.
            await supervisor.StopAsync(instanceId, force: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogAttestationStopFailed(logger, exception, appId, instanceId);
        }

        if (routes.TryGetByApp(appId, out var currentRoute)
            && currentRoute.StaticPath is null
            && string.Equals(currentRoute.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase)
            && currentRoute.Status is not GatewayRouteStatus.Stopped
                and not GatewayRouteStatus.Disabled
                and not GatewayRouteStatus.Unhealthy)
        {
            await routes.SetStatusAsync(appId, GatewayRouteStatus.Unhealthy, cancellationToken);
        }
    }

    private static bool IsStaticDeploymentAvailable(string deploymentsRoot, string appId, string staticPath)
    {
        try
        {
            var appRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(deploymentsRoot, appId)));
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staticPath));
            if (!fullPath.StartsWith(appRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !fullPath.Equals(appRoot, StringComparison.OrdinalIgnoreCase)) return false;
            if (!StaticDeploymentContent.ContainsServableFile(fullPath)) return false;

            for (var current = fullPath; ; current = Path.GetDirectoryName(current)!)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                if (current.Equals(appRoot, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(current))) return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

public sealed partial class ManifestWatchService(
    AbodeToWeb.Infrastructure.AbodeToWebPaths paths,
    ManifestProvider manifest,
    GatewayRouteRegistry routes,
    DeploymentEngine deployments,
    ILogger<ManifestWatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = Path.GetDirectoryName(paths.ConfigurationFile)!;
        Directory.CreateDirectory(directory);
        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(paths.ConfigurationFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        var changes = System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        FileSystemEventHandler changed = (_, _) => changes.Writer.TryWrite(true);
        RenamedEventHandler renamed = (_, _) => changes.Writer.TryWrite(true);
        watcher.Changed += changed; watcher.Created += changed; watcher.Renamed += renamed;
        await foreach (var _ in changes.Reader.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(300, stoppingToken);
            var previous = manifest.Current;
            var result = await manifest.LoadAsync(allowLastKnownGood: false, stoppingToken);
            if (result.Success && result.Manifest is not null)
            {
                var nextById = result.Manifest.Apps.ToDictionary(app => app.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var priorApp in previous.Apps)
                {
                    if (nextById.TryGetValue(priorApp.Id, out var nextApp)
                        && nextApp.Enabled && nextApp.Visibility != AppVisibility.Disabled) continue;
                    try
                    {
                        await deployments.StopForConfigurationChangeAsync(
                            priorApp, OperationExecutionContext.CreateSystem("manifest-change"), stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        LogManifestStopFailed(logger, exception, priorApp.Id);
                    }
                }
                routes.ReloadDesiredConfiguration(result.Manifest);
                LogManifestReloaded(logger);
            }
            else LogManifestRejected(logger, result.Errors.Count > 0 ? result.Errors[0].Message : "Unknown validation error");
        }
    }

    [LoggerMessage(304, LogLevel.Information, "AbodeToWeb manifest reloaded")]
    private static partial void LogManifestReloaded(ILogger logger);

    [LoggerMessage(305, LogLevel.Warning, "Invalid manifest change rejected; last-known-good configuration remains active: {reason}")]
    private static partial void LogManifestRejected(ILogger logger, string reason);

    [LoggerMessage(306, LogLevel.Error, "Manifest disabled or removed {appId}, but its running process could not be stopped")]
    private static partial void LogManifestStopFailed(ILogger logger, Exception exception, string appId);
}

public sealed class LogQuotaService(BoundedLogStore logs, ManifestProvider manifest) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            var appCount = Math.Max(1, manifest.Current.Apps.Count);
            logs.EnforceTotalQuota(manifest.Current.AbodeToWeb.Storage.MaxLogBytesPerApp * appCount);
            logs.PruneOlderThan(DateTimeOffset.UtcNow.AddDays(-manifest.Current.AbodeToWeb.Storage.LogRetentionDays));
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
