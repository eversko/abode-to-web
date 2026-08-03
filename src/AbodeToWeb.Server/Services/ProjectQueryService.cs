using System.Collections.Concurrent;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Core.Deployments;
using AbodeToWeb.Core.Health;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Deployments;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Runtime;
using AbodeToWeb.Infrastructure.Security;

namespace AbodeToWeb.Server.Services;

public sealed class ProjectQueryService(
    ManifestProvider manifest,
    AbodeToWebDatabase database,
    GatewayRouteRegistry routes,
    ApplicationSupervisor supervisor,
    SecretFileStore secrets)
{
    private readonly ConcurrentDictionary<string, RepositoryGitStatus> _git = new(StringComparer.OrdinalIgnoreCase);

    public int UpdatesAvailable => _git.Values.Count(status => status.UpdateAvailable);

    public void SetGitStatus(string appId, RepositoryGitStatus status) => _git[appId] = status;

    public async Task<IReadOnlyList<ProjectSummary>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        var states = (await database.GetProjectStatesAsync(cancellationToken)).ToDictionary(state => state.AppId, StringComparer.OrdinalIgnoreCase);
        var deployments = await database.GetDeploymentsAsync(limit: 500, cancellationToken: cancellationToken);
        return manifest.Current.Apps.Select(app =>
            {
                var latest = deployments.FirstOrDefault(deployment => deployment.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
                var activeId = routes.TryGetByApp(app.Id, out var route) ? route.DeploymentId : null;
                var active = activeId is null ? null : deployments.FirstOrDefault(deployment => deployment.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase));
                return BuildSummary(app, states.GetValueOrDefault(app.Id), latest, active);
            })
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ProjectDetail?> GetProjectAsync(string appId, CancellationToken cancellationToken = default)
    {
        var app = manifest.Current.Apps.FirstOrDefault(item => item.Id.Equals(appId, StringComparison.OrdinalIgnoreCase));
        if (app is null) return null;
        var state = (await database.GetProjectStatesAsync(cancellationToken)).FirstOrDefault(item => item.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
        var deployments = await database.GetDeploymentsAsync(app.Id, 50, cancellationToken);
        var activeId = routes.TryGetByApp(app.Id, out var route) ? route.DeploymentId : null;
        var summaries = deployments.Select(deployment => MapDeployment(deployment, activeId)).ToArray();
        var secretKeys = secrets.ListKeys(app.Id).Select(key => new SecretKeyInfo(key, true, null)).ToArray();
        var activeRecord = activeId is null ? null : deployments.FirstOrDefault(deployment => deployment.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase));
        return new ProjectDetail(BuildSummary(app, state, deployments.Count > 0 ? deployments[0] : null, activeRecord), app,
            summaries.FirstOrDefault(deployment => deployment.IsActive), summaries, secretKeys, state?.LastError);
    }

    public async Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(string? appId, CancellationToken cancellationToken = default)
    {
        var records = await database.GetDeploymentsAsync(appId, 200, cancellationToken);
        var active = routes.Snapshot().Where(route => route.DeploymentId is not null).ToDictionary(route => route.AppId, route => route.DeploymentId!, StringComparer.OrdinalIgnoreCase);
        return records.Select(record => MapDeployment(record, active.GetValueOrDefault(record.AppId))).ToArray();
    }

    public async Task<IReadOnlyList<PortfolioProjectExport>> ExportAsync(CancellationToken cancellationToken = default)
    {
        var projects = await GetProjectsAsync(cancellationToken);
        return projects.Where(project => project.Visibility != AppVisibility.Disabled).Select(project => new PortfolioProjectExport(
            project.Id, project.Name, project.Description, project.Hostname is null ? null : "https://" + project.Hostname,
            project.RuntimeState == ProjectRuntimeState.Running && project.Health == HealthState.Healthy,
            project.Health, DateTimeOffset.UtcNow)).ToArray();
    }

    private ProjectSummary BuildSummary(AppManifest app, ProjectStateRecord? state, DeploymentRecord? latest, DeploymentRecord? active)
    {
        routes.TryGetByApp(app.Id, out var route);
        var runtimeSnapshot = supervisor.GetSnapshots()
            .Where(item => item.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => route?.DeploymentId is not null
                                       && string.Equals(item.DeploymentId, route.DeploymentId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.State is ManagedRuntimeState.Running or ManagedRuntimeState.Starting or ManagedRuntimeState.Backoff)
            .ThenByDescending(item => item.StartedUtc)
            .FirstOrDefault();
        var staticRuntime = app.Deployment.Adapter == DeploymentAdapter.Static && route is { DeploymentId: not null, StaticPath: not null }
            ? route.Status switch
            {
                GatewayRouteStatus.Healthy or GatewayRouteStatus.Unhealthy => ProjectRuntimeState.Running,
                GatewayRouteStatus.Starting => ProjectRuntimeState.Starting,
                _ => ProjectRuntimeState.Stopped
            }
            : (ProjectRuntimeState?)null;
        var runtime = !app.Enabled ? ProjectRuntimeState.Disabled : staticRuntime ?? runtimeSnapshot?.State switch
        {
            ManagedRuntimeState.Running => ProjectRuntimeState.Running,
            ManagedRuntimeState.Starting or ManagedRuntimeState.Backoff => ProjectRuntimeState.Starting,
            ManagedRuntimeState.Stopping => ProjectRuntimeState.Stopping,
            ManagedRuntimeState.CircuitOpen => ProjectRuntimeState.CircuitOpen,
            ManagedRuntimeState.Exited => ProjectRuntimeState.Failed,
            _ => state?.RuntimeState == "failed" ? ProjectRuntimeState.Failed : ProjectRuntimeState.Stopped
        };
        var health = !app.Enabled ? HealthState.Disabled : route is not null ? route.Status switch
        {
            GatewayRouteStatus.Healthy => HealthState.Healthy,
            GatewayRouteStatus.Unhealthy => HealthState.Unhealthy,
            GatewayRouteStatus.Starting => HealthState.Starting,
            GatewayRouteStatus.Stopped => HealthState.Stopped,
            _ => HealthState.Unknown
        } : HealthState.Unknown;
        _git.TryGetValue(app.Id, out var gitStatus);
        var git = app.Repository is null ? null : new GitStatus(app.Repository.Branch,
            active?.CommitSha,
            gitStatus?.RemoteHead, gitStatus?.UpdateAvailable ?? false, gitStatus?.RemoteCommit?.Message,
            gitStatus?.RemoteCommit?.Author, gitStatus?.RemoteCommit?.AuthoredAt, gitStatus?.CheckedUtc, gitStatus?.SafeError);
        return new ProjectSummary(app.Id, app.Name, app.Description, runtime, health, git, app.Routing.Hostname,
            app.Visibility, app.Protected, latest?.CompletedUtc, runtimeSnapshot?.StartedUtc);
    }

    private static DeploymentSummary MapDeployment(DeploymentRecord record, string? activeId)
    {
        var id = Guid.TryParseExact(record.Id, "N", out var guid) ? guid : Guid.Empty;
        var status = Enum.TryParse<DeploymentStatus>(record.State, true, out var parsed) ? parsed : DeploymentStatus.Failed;
        return new DeploymentSummary(id, record.AppId, record.CommitSha, record.CommitSha[..Math.Min(8, record.CommitSha.Length)], record.Branch,
            status, record.CommitMessage, record.CommitAuthor, record.StartedUtc, record.CompletedUtc,
            record.DurationMilliseconds is null ? null : TimeSpan.FromMilliseconds(record.DurationMilliseconds.Value),
            record.BuildOutcome == "succeeded", record.HealthOutcome == "healthy",
            string.Equals(record.Id, activeId, StringComparison.OrdinalIgnoreCase), record.IsKnownGood, record.FailureReason);
    }
}
