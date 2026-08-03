using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Routing;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Security;

namespace AbodeToWeb.Server.Services;

public sealed class ProjectConfigurationService(
    ManifestProvider manifests,
    GatewayRouteRegistry routes,
    AbodeToWebDatabase database,
    SecretFileStore secrets)
{
    public async Task ChangeHostnameAsync(string appId, string hostname, CancellationToken cancellationToken = default)
    {
        if (!Hostname.TryParse(hostname, out var normalized, out var error))
            throw new ProjectConfigurationException("hostname.invalid", error ?? "Hostname is invalid.");

        var next = await manifests.UpdateAtomicAsync(current =>
        {
            var found = false;
            var apps = current.Apps.Select(app =>
            {
                if (!app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)) return app;
                found = true;
                return app with { Routing = app.Routing with { Hostname = normalized.Value } };
            }).ToArray();
            if (!found) throw new ProjectConfigurationException("project.not_found", "Project not found.");
            return current with { Apps = apps };
        }, cancellationToken);

        routes.ReloadDesiredConfiguration(next);
    }

    public async Task ChangeVisibilityAsync(string appId, AppVisibility visibility, CancellationToken cancellationToken = default)
    {
        var next = await manifests.UpdateAtomicAsync(current =>
        {
            var found = false;
            var apps = current.Apps.Select(app =>
            {
                if (!app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)) return app;
                found = true;
                return app with { Visibility = visibility };
            }).ToArray();
            if (!found) throw new ProjectConfigurationException("project.not_found", "Project not found.");
            return current with { Apps = apps };
        }, cancellationToken);

        routes.ReloadDesiredConfiguration(next);
    }

    public async Task RemoveProjectAsync(string appId, bool removeRepository, CancellationToken cancellationToken = default)
    {
        if (removeRepository)
            throw new ProjectConfigurationException("repository.removal_not_supported",
                "AbodeToWeb can remove the project configuration, but source repository deletion is intentionally not automated.");

        if (!manifests.Current.Apps.Any(app => app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)))
            throw new ProjectConfigurationException("project.not_found", "Project not found.");

        // Retire the ID before rewriting the manifest. If power is lost between
        // these durable steps, startup fails closed instead of attaching a future
        // project with the same ID to this project's state, history, or secrets.
        await database.RetireProjectAsync(appId, cancellationToken);
        manifests.RetireProjectId(appId);

        var next = await manifests.UpdateAtomicAsync(current =>
        {
            if (!current.Apps.Any(app => app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)))
                throw new ProjectConfigurationException("project.not_found", "Project not found.");
            return current with
            {
                Apps = current.Apps.Where(app => !app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)).ToArray()
            };
        }, cancellationToken);

        routes.ReloadDesiredConfiguration(next);
        await secrets.DeleteAsync(appId, cancellationToken);
    }
}

public sealed class ProjectConfigurationException(string code, string message)
    : Exception(message), AbodeToWeb.Infrastructure.Operations.ISafeOperationError
{
    public string Code { get; } = code;
    public string SafeMessage => Message;
}
