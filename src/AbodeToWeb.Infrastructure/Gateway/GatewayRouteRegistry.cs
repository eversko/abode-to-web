using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Infrastructure.Persistence;

namespace AbodeToWeb.Infrastructure.Gateway;

public enum GatewayRouteStatus
{
    Stopped,
    Starting,
    Healthy,
    Unhealthy,
    Disabled
}

public sealed record GatewayRoute(
    string AppId,
    string Hostname,
    string? DeploymentId,
    int? InternalPort,
    string? StaticPath,
    GatewayRouteStatus Status,
    AppVisibility Visibility,
    bool OfflinePage,
    TimeSpan RequestTimeout,
    long MaxRequestBodyBytes,
    DateTimeOffset UpdatedUtc);

public sealed class GatewayRouteRegistry(AbodeToWebDatabase database)
{
    private readonly Lock _lock = new();
    private Dictionary<string, GatewayRoute> _byHostname = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, GatewayRoute> _byApp = new(StringComparer.OrdinalIgnoreCase);

    public async Task InitializeAsync(AbodeToWebManifest manifest, CancellationToken cancellationToken = default)
    {
        var persisted = (await database.GetRoutesAsync(cancellationToken)).ToDictionary(route => route.AppId, StringComparer.OrdinalIgnoreCase);
        var routes = new List<GatewayRoute>();
        foreach (var app in manifest.Apps.Where(app => !string.IsNullOrWhiteSpace(app.Routing.Hostname)))
        {
            persisted.TryGetValue(app.Id, out var state);
            var persistedStatus = ParseStatus(state?.Status);
            var status = !app.Enabled || app.Visibility == AppVisibility.Disabled
                ? GatewayRouteStatus.Disabled
                : state?.StaticPath is not null && persistedStatus == GatewayRouteStatus.Healthy
                    ? GatewayRouteStatus.Healthy
                    : persistedStatus == GatewayRouteStatus.Stopped
                        ? GatewayRouteStatus.Stopped
                        // A loopback port is not evidence that the same managed process
                        // survived a service restart. Reconciliation must prove ownership
                        // and health before dynamic traffic is restored.
                        : GatewayRouteStatus.Starting;
            routes.Add(new GatewayRoute(app.Id, app.Routing.Hostname!.TrimEnd('.').ToLowerInvariant(), state?.DeploymentId,
                state?.InternalPort, state?.StaticPath, status, app.Visibility, app.Routing.OfflinePage,
                TimeSpan.FromSeconds(app.Routing.RequestTimeoutSeconds), app.Routing.MaxRequestBodyBytes,
                state?.UpdatedUtc ?? DateTimeOffset.UtcNow));
        }
        foreach (var route in routes) await database.UpsertRouteAsync(ToRecord(route), cancellationToken);
        Replace(routes);
    }

    public IReadOnlyList<GatewayRoute> Snapshot()
    {
        lock (_lock) return _byApp.Values.OrderBy(route => route.AppId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool TryGetByHostname(string hostname, out GatewayRoute route)
    {
        var normalized = NormalizeHostname(hostname);
        lock (_lock) return _byHostname.TryGetValue(normalized, out route!);
    }

    public bool TryGetByApp(string appId, out GatewayRoute route)
    {
        lock (_lock) return _byApp.TryGetValue(appId, out route!);
    }

    public async Task SetActiveAsync(
        AppManifest app,
        string deploymentId,
        int? internalPort,
        string? staticPath,
        GatewayRouteStatus status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(app.Routing.Hostname)) throw new InvalidOperationException("Application does not have a hostname.");
        var route = new GatewayRoute(app.Id, NormalizeHostname(app.Routing.Hostname), deploymentId, internalPort,
            staticPath, status, app.Visibility, app.Routing.OfflinePage, TimeSpan.FromSeconds(app.Routing.RequestTimeoutSeconds),
            app.Routing.MaxRequestBodyBytes, DateTimeOffset.UtcNow);
        await database.UpsertRouteAsync(ToRecord(route), cancellationToken);
        Set(route);
    }

    public async Task SetStatusAsync(string appId, GatewayRouteStatus status, CancellationToken cancellationToken = default)
    {
        GatewayRoute route;
        lock (_lock)
        {
            if (!_byApp.TryGetValue(appId, out route!)) return;
            route = route with { Status = status, UpdatedUtc = DateTimeOffset.UtcNow };
            SetUnsafe(route);
        }
        await database.UpsertRouteAsync(ToRecord(route), cancellationToken);
    }

    public async Task RestoreAsync(GatewayRoute route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        var restored = route with { UpdatedUtc = DateTimeOffset.UtcNow };
        await database.UpsertRouteAsync(ToRecord(restored), cancellationToken);
        Set(restored);
    }

    public void ReloadDesiredConfiguration(AbodeToWebManifest manifest)
    {
        var current = Snapshot().ToDictionary(route => route.AppId, StringComparer.OrdinalIgnoreCase);
        var routes = manifest.Apps.Where(app => !string.IsNullOrWhiteSpace(app.Routing.Hostname)).Select(app =>
        {
            current.TryGetValue(app.Id, out var active);
            return new GatewayRoute(app.Id, NormalizeHostname(app.Routing.Hostname!), active?.DeploymentId, active?.InternalPort,
                active?.StaticPath, !app.Enabled || app.Visibility == AppVisibility.Disabled ? GatewayRouteStatus.Disabled : active?.Status ?? GatewayRouteStatus.Stopped,
                app.Visibility, app.Routing.OfflinePage, TimeSpan.FromSeconds(app.Routing.RequestTimeoutSeconds),
                app.Routing.MaxRequestBodyBytes, DateTimeOffset.UtcNow);
        });
        Replace(routes);
    }

    private void Replace(IEnumerable<GatewayRoute> routes)
    {
        lock (_lock)
        {
            _byHostname = routes.ToDictionary(route => route.Hostname, StringComparer.OrdinalIgnoreCase);
            _byApp = _byHostname.Values.ToDictionary(route => route.AppId, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Set(GatewayRoute route)
    {
        lock (_lock) SetUnsafe(route);
    }

    private void SetUnsafe(GatewayRoute route)
    {
        if (_byApp.TryGetValue(route.AppId, out var previous)) _byHostname.Remove(previous.Hostname);
        _byHostname[route.Hostname] = route;
        _byApp[route.AppId] = route;
    }

    private static RouteStateRecord ToRecord(GatewayRoute route) => new(route.AppId, route.Hostname, route.DeploymentId,
        route.InternalPort, route.Status.ToString().ToLowerInvariant(), route.StaticPath, route.UpdatedUtc);

    private static GatewayRouteStatus ParseStatus(string? status) => Enum.TryParse<GatewayRouteStatus>(status, true, out var parsed)
        ? parsed
        : GatewayRouteStatus.Stopped;

    private static string NormalizeHostname(string hostname)
    {
        var value = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.Contains(':')) value = value.Split(':', 2)[0];
        return value;
    }
}
