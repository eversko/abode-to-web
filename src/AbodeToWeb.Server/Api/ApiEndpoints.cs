using System.Text.Json;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Core.Deployments;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Deployments;
using AbodeToWeb.Infrastructure.Logging;
using AbodeToWeb.Infrastructure.Operations;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Security;
using AbodeToWeb.Server.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace AbodeToWeb.Server.Api;

public sealed record ValidateConfigurationRequest(string Content);

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapAbodeToWebApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy" })).ExcludeFromDescription();
        endpoints.MapGet("/api/v1/security/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken });
        }).ExcludeFromDescription();

        var api = endpoints.MapGroup("/api/v1").WithTags("AbodeToWeb");
        api.MapGet("/system/status", (SystemStatusService status, CancellationToken cancellationToken) => status.GetAsync(cancellationToken));
        api.MapGet("/system/tunnel", (TunnelDiagnosticsService tunnel, CancellationToken cancellationToken) => tunnel.GetAsync(cancellationToken));
        api.MapGet("/projects", (ProjectQueryService projects, CancellationToken cancellationToken) => projects.GetProjectsAsync(cancellationToken));
        api.MapGet("/external-sites", (ExternalSiteQueryService sites, CancellationToken cancellationToken) => sites.GetAsync(cancellationToken));
        api.MapPut("/external-sites/{siteId}", async (string siteId, SaveExternalSiteRequest request,
            ExternalSiteConfigurationService configuration, AbodeToWebDatabase database, HttpContext http,
            CancellationToken cancellationToken) =>
        {
            await configuration.SaveAsync(siteId, request, cancellationToken);
            await database.AppendAuditAsync(new AuditEventRecord(0, DateTimeOffset.UtcNow, Actor(http),
                "external-site.save", siteId, "succeeded", null,
                JsonSerializer.Serialize(new { resourceKind = "external-site" }),
                http.Connection.RemoteIpAddress?.ToString()), cancellationToken);
            return Results.NoContent();
        });
        api.MapGet("/local-projects", (LocalProjectQueryService projects, CancellationToken cancellationToken) => projects.GetAsync(cancellationToken));
        api.MapPut("/local-projects/{projectId}", async (string projectId, SaveLocalProjectRequest request,
            LocalProjectConfigurationService configuration, AbodeToWebDatabase database, HttpContext http,
            CancellationToken cancellationToken) =>
        {
            await configuration.SaveAsync(projectId, request, cancellationToken);
            await database.AppendAuditAsync(new AuditEventRecord(0, DateTimeOffset.UtcNow, Actor(http),
                "local-project.save", projectId, "succeeded", null,
                JsonSerializer.Serialize(new { resourceKind = "local-project" }),
                http.Connection.RemoteIpAddress?.ToString()), cancellationToken);
            return Results.NoContent();
        });
        api.MapGet("/projects/export", (ProjectQueryService projects, CancellationToken cancellationToken) => projects.ExportAsync(cancellationToken));
        api.MapGet("/projects/{appId}", async (string appId, ProjectQueryService projects, CancellationToken cancellationToken) =>
        {
            var project = await projects.GetProjectAsync(appId, cancellationToken);
            return project is null ? Results.NotFound(SafeProblem("project_not_found", "Project not found.")) : Results.Ok(project);
        });
        api.MapGet("/projects/{appId}/deployments", (string appId, ProjectQueryService projects, CancellationToken cancellationToken) => projects.GetDeploymentsAsync(appId, cancellationToken));
        api.MapGet("/deployments", (ProjectQueryService projects, CancellationToken cancellationToken) => projects.GetDeploymentsAsync(null, cancellationToken));

        api.MapPost("/projects/{appId}/start", (string appId, HttpContext http, OperationCoordinator operations, DeploymentEngine engine, CancellationToken cancellationToken) =>
            EnqueueAsync(operations, OperationKind.Start, "start", appId, http, (context, token) => engine.StartAsync(appId, context, token), cancellationToken));
        api.MapPost("/projects/{appId}/stop", (string appId, HttpContext http, OperationCoordinator operations, DeploymentEngine engine, CancellationToken cancellationToken) =>
            EnqueueAsync(operations, OperationKind.Stop, "stop", appId, http, (context, token) => engine.StopAsync(appId, context, false, token), cancellationToken));
        api.MapPost("/projects/{appId}/force-stop", (string appId, HttpContext http, OperationCoordinator operations, DeploymentEngine engine, CancellationToken cancellationToken) =>
            EnqueueAsync(operations, OperationKind.Stop, "force-stop", appId, http, (context, token) => engine.StopAsync(appId, context, true, token), cancellationToken));
        api.MapPost("/projects/{appId}/restart", (string appId, HttpContext http, OperationCoordinator operations, DeploymentEngine engine, CancellationToken cancellationToken) =>
            EnqueueAsync(operations, OperationKind.Restart, "restart", appId, http, (context, token) => engine.RestartAsync(appId, context, token), cancellationToken));
        api.MapPost("/projects/{appId}/deploy", (string appId, HttpContext http, OperationCoordinator operations, DeploymentEngine engine, CancellationToken cancellationToken) =>
            EnqueueAsync(operations, OperationKind.Deploy, "deploy", appId, http, async (context, token) => { await engine.DeployAsync(appId, context, token); }, cancellationToken));
        api.MapPost("/projects/{appId}/rollback", (string appId, RollbackRequest request, HttpContext http, OperationCoordinator operations, DeploymentEngine engine, CancellationToken cancellationToken) =>
            EnqueueAsync(operations, OperationKind.Rollback, "rollback", appId, http,
                async (context, token) => { await engine.RollbackAsync(appId, request.DeploymentId.ToString("N"), context, token); }, cancellationToken));
        api.MapPost("/projects/{appId}/git/check", (string appId, HttpContext http, OperationCoordinator operations, DeploymentEngine engine, ProjectQueryService queries, CancellationToken cancellationToken) =>
            EnqueueAsync(operations, OperationKind.CheckForUpdates, "check-for-updates", appId, http, async (context, token) =>
            {
                await context.ReportAsync(10, "Fetching repository status");
                var status = await engine.CheckForUpdatesAsync(appId, context.OperationId, token);
                queries.SetGitStatus(appId, status);
                if (!string.IsNullOrWhiteSpace(status.SafeError))
                    throw new DeploymentException("git.check_failed", status.SafeError);
                await context.ReportAsync(100, status.UpdateAvailable ? "Update available" : "Project is current");
            }, cancellationToken));
        api.MapPut("/projects/{appId}/hostname", (string appId, ChangeHostnameRequest request, HttpContext http,
            OperationCoordinator operations, ProjectConfigurationService configuration, CancellationToken cancellationToken) =>
            EnqueueAsync(operations, OperationKind.ChangeHostname, "change-hostname", appId, http, async (context, token) =>
            {
                await context.ReportAsync(20, "Validating hostname");
                await configuration.ChangeHostnameAsync(appId, request.Hostname, token);
                await context.ReportAsync(100, "Hostname updated");
            }, cancellationToken));
        api.MapPut("/projects/{appId}/visibility", (string appId, ChangeVisibilityRequest request, HttpContext http,
            OperationCoordinator operations, ProjectConfigurationService configuration, CancellationToken cancellationToken) =>
            EnqueueAsync(operations, OperationKind.ChangeVisibility, "change-visibility", appId, http, async (context, token) =>
            {
                await context.ReportAsync(20, "Validating visibility");
                await configuration.ChangeVisibilityAsync(appId, request.Visibility, token);
                await context.ReportAsync(100, "Visibility updated");
            }, cancellationToken));
        api.MapDelete("/projects/{appId}", (string appId, bool? removeRepository, HttpContext http,
            OperationCoordinator operations, DeploymentEngine engine, ProjectConfigurationService configuration,
            CancellationToken cancellationToken) => EnqueueAsync(operations, OperationKind.RemoveProject, "remove-project", appId, http,
                async (context, token) =>
                {
                    await context.ReportAsync(10, "Stopping project");
                    // Removal must not dead-end on projects that cannot be stopped —
                    // a disabled or already-stopped project still deserves deletion.
                    try { await engine.StopAsync(appId, context, false, token); }
                    catch (DeploymentException) { await context.ReportAsync(40, "Project was not running; continuing removal"); }
                    await context.ReportAsync(70, "Removing project configuration");
                    await configuration.RemoveProjectAsync(appId, removeRepository ?? false, token);
                    await context.ReportAsync(100, "Project removed");
                }, cancellationToken));

        api.MapGet("/operations/{operationId:guid}", async (Guid operationId, AbodeToWebDatabase database, CancellationToken cancellationToken) =>
        {
            var record = await database.GetOperationAsync(operationId.ToString("N"), cancellationToken);
            return record is null ? Results.NotFound(SafeProblem("operation_not_found", "Operation not found.")) : Results.Ok(ToProgress(record));
        });
        api.MapGet("/operations/events", StreamOperationsAsync);

        api.MapGet("/logs", async (string? projectId, string? source, string? search, string? severity, int? limit,
            BoundedLogStore logs, CancellationToken cancellationToken) =>
        {
            var kind = ParseLogKind(source);
            AbodeToWebLogSeverity? minimum = Enum.TryParse<AbodeToWebLogSeverity>(severity, true, out var parsedSeverity) ? parsedSeverity : null;
            var items = await logs.QueryAsync(new LogQuery(kind, projectId ?? "abode-to-web", limit ?? 500, search, minimum), cancellationToken);
            return Results.Ok(items.Select(ToContractLog));
        });
        api.MapGet("/logs/events", StreamLogsAsync);
        api.MapGet("/logs/download", async (string? projectId, string? source, string? search, string? severity, int? limit,
            HttpContext http, BoundedLogStore logs, CancellationToken cancellationToken) =>
        {
            var kind = ParseLogKind(source);
            AbodeToWebLogSeverity? minimum = Enum.TryParse<AbodeToWebLogSeverity>(severity, true, out var parsedSeverity) ? parsedSeverity : null;
            http.Response.ContentType = "text/plain; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=abode-to-web-{kind.ToString().ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMddHHmmss}.log";
            await logs.CopyBoundedSegmentAsync(new LogQuery(kind, projectId ?? "abode-to-web", limit ?? 2_000, search, minimum), http.Response.Body, cancellationToken);
        });

        api.MapGet("/activity", async (int? limit, AbodeToWebDatabase database, CancellationToken cancellationToken) =>
        {
            var events = await database.GetAuditEventsAsync(limit ?? 100, cancellationToken);
            return Results.Ok(events.Select(ToActivity));
        });

        api.MapPost("/configuration/validate", (ValidateConfigurationRequest request, ManifestProvider manifest) =>
        {
            var result = manifest.ValidateText(request.Content);
            return Results.Ok(new ConfigurationValidation(result.IsValid, result.Issues.Select(issue => new ContractValidationIssue(
                issue.Code, issue.Path, issue.Message, issue.Severity == AbodeToWeb.Core.Validation.ValidationSeverity.Warning)).ToArray()));
        });

        api.MapGet("/projects/{appId}/secrets", (string appId, ManifestProvider manifest, SecretFileStore secrets) =>
        {
            var app = FindProject(manifest, appId);
            return app is null
                ? Results.NotFound(SafeProblem("project_not_found", "Project not found."))
                : Results.Ok(secrets.ListKeys(app.Id).Select(key => new SecretKeyInfo(key, true, null)));
        });
        api.MapGet("/projects/{appId}/secrets/example", (string appId, HttpContext http,
            ManifestProvider manifest, SecretFileStore secrets) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!SecretFileStore.IsValidApplicationId(appId))
                throw new ArgumentException("Application ID must be a lowercase slug.", nameof(appId));
            var app = FindProject(manifest, appId);
            if (app is null) return Results.NotFound(SafeProblem("project_not_found", "Project not found."));

            return Results.File(
                secrets.CreateNamesOnlyExample(app.Id),
                "text/plain; charset=utf-8",
                app.Id + ".env.example",
                enableRangeProcessing: false);
        })
            .WithName("DownloadProjectEnvironmentExample")
            .Produces<string>(StatusCodes.Status200OK, contentType: "text/plain")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        api.MapPut("/projects/{appId}/secrets/{key}", async (string appId, string key, ReplaceSecretRequest request,
            ManifestProvider manifest, SecretFileStore secrets, AbodeToWebDatabase database, HttpContext http, CancellationToken cancellationToken) =>
        {
            var app = FindProject(manifest, appId);
            if (app is null) return Results.NotFound(SafeProblem("project_not_found", "Project not found."));
            await secrets.ReplaceAsync(app.Id, new Dictionary<string, string?> { [key] = request.Value }, cancellationToken);
            await AuditSecretAsync(database, http, app.Id, "secret.replace", key, cancellationToken);
            return Results.NoContent();
        });
        api.MapDelete("/projects/{appId}/secrets/{key}", async (string appId, string key, ManifestProvider manifest, SecretFileStore secrets,
            AbodeToWebDatabase database, HttpContext http, CancellationToken cancellationToken) =>
        {
            var app = FindProject(manifest, appId);
            if (app is null) return Results.NotFound(SafeProblem("project_not_found", "Project not found."));
            await secrets.ReplaceAsync(app.Id, new Dictionary<string, string?> { [key] = null }, cancellationToken);
            await AuditSecretAsync(database, http, app.Id, "secret.delete", key, cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/onboarding/detect", async (DetectProjectRequest request, OnboardingService onboarding, CancellationToken cancellationToken) =>
            Results.Ok(await onboarding.DetectAsync(request.LocalPath, cancellationToken)))
            .Produces<OnboardingDetection>();
        api.MapPost("/onboarding/clone", async (CloneProjectRequest request, HttpContext http, OperationCoordinator operations,
            OnboardingService onboarding, CancellationToken cancellationToken) =>
        {
            var destination = onboarding.GetCloneDestination(request.Repository).Destination;
            var operationId = await operations.EnqueueAsync(new OperationRequest("clone", "onboarding", Actor(http),
                http.Connection.RemoteIpAddress?.ToString(), async (context, token) =>
                {
                    await context.ReportAsync(10, "Cloning GitHub repository");
                    var path = await onboarding.CloneAsync(request.Repository, context.OperationId, token);
                    await context.ReportAsync(100, "Clone complete: " + path);
                }), cancellationToken);
            var id = Guid.ParseExact(operationId, "N");
            return Results.Accepted($"/api/v1/operations/{id:D}",
                new CloneProjectAccepted(id, OperationKind.TestBuild, $"/api/v1/operations/{id:D}", destination));
        }).Produces<CloneProjectAccepted>(StatusCodes.Status202Accepted);
        api.MapPost("/onboarding/validate", (CreateProjectRequest request, OnboardingService onboarding) => Results.Ok(onboarding.ValidateCreate(request)))
            .Produces<OnboardingValidation>();
        api.MapPost("/onboarding/projects", (CreateProjectRequest request, HttpContext http, OperationCoordinator operations,
            OnboardingService onboarding, DeploymentEngine engine, CancellationToken cancellationToken) => EnqueueAsync(operations,
                request.RunTestBuild ? OperationKind.TestBuild : OperationKind.ValidateConfiguration, "add-project", request.Id, http,
                async (context, token) =>
                {
                    await context.ReportAsync(10, "Saving validated project manifest");
                    var app = await onboarding.CreateAsync(request, token);
                    if (request.RunTestBuild)
                    {
                        await context.ReportAsync(20, "Running test deployment");
                        await engine.DeployAsync(app.Id, context, token);
                    }
                }, cancellationToken));

        api.MapPost("/modes/quiet", (QuietModeRequest request, HttpContext http, OperationCoordinator operations, ManifestProvider manifest,
            DeploymentEngine engine, SystemStatusService system, CancellationToken cancellationToken) => EnqueueAsync(operations,
                OperationKind.QuietMode, "quiet-mode", "system-modes", http, async (context, token) =>
                {
                    if (!request.Enabled)
                    {
                        system.QuietMode = false;
                        await context.ReportAsync(100, "Quiet Mode disabled");
                        return;
                    }
                    var stoppable = manifest.Current.Apps.Where(app => app.Enabled && !app.Protected).ToArray();
                    for (var index = 0; index < stoppable.Length; index++)
                    {
                        await context.ReportAsync(stoppable.Length == 0 ? 100 : index * 100 / stoppable.Length, "Stopping " + stoppable[index].Name);
                        await engine.StopAsync(stoppable[index].Id, context, false, token);
                    }
                    system.QuietMode = true;
                }, cancellationToken));
        api.MapPost("/modes/showcase", (ShowcaseRequest request, HttpContext http, OperationCoordinator operations, ManifestProvider manifest,
            DeploymentEngine engine, SystemStatusService system, CancellationToken cancellationToken) => EnqueueAsync(operations,
                OperationKind.StartShowcase, "start-showcase", "system-modes", http, async (context, token) =>
                {
                    var current = manifest.Current;
                    var requestedIds = request.ProjectIds?.Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
                    var showcase = requestedIds.Length == 0
                        ? current.Apps.Where(app => app.Enabled && app.DemoGroups.Contains("showcase", StringComparer.OrdinalIgnoreCase)).ToArray()
                        : requestedIds.Select(id => current.Apps.FirstOrDefault(app => app.Enabled && app.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ProjectConfigurationException("showcase.project_invalid", "A selected showcase project is missing or disabled.")).ToArray();
                    for (var index = 0; index < showcase.Length; index++)
                    {
                        if (index > 0 && current.AbodeToWeb.StartupStaggerSeconds > 0)
                            await Task.Delay(TimeSpan.FromSeconds(current.AbodeToWeb.StartupStaggerSeconds), token);
                        await context.ReportAsync(showcase.Length == 0 ? 100 : index * 100 / showcase.Length, "Starting " + showcase[index].Name);
                        await engine.StartAsync(showcase[index].Id, context, token);
                    }
                    system.QuietMode = false;
                }, cancellationToken));
        return endpoints;
    }

    private static async Task<IResult> EnqueueAsync(OperationCoordinator operations, OperationKind kind, string action, string target,
        HttpContext http, Func<OperationExecutionContext, CancellationToken, Task> execute, CancellationToken cancellationToken)
    {
        var operationId = await operations.EnqueueAsync(new OperationRequest(action, target, Actor(http),
            http.Connection.RemoteIpAddress?.ToString(), execute), cancellationToken);
        var id = Guid.ParseExact(operationId, "N");
        return Results.Accepted($"/api/v1/operations/{id:D}", new OperationAccepted(id, kind, $"/api/v1/operations/{id:D}"));
    }

    private static async Task StreamOperationsAsync(HttpContext http, OperationStreamHub hub, CancellationToken cancellationToken)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await foreach (var item in hub.SubscribeAsync(cancellationToken))
        {
            await http.Response.WriteAsync("event: operation\ndata: " + JsonSerializer.Serialize(item) + "\n\n", cancellationToken);
            await http.Response.Body.FlushAsync(cancellationToken);
        }
    }

    private static async Task StreamLogsAsync(HttpContext http, LogStreamHub hub, string? projectId, string? source, CancellationToken cancellationToken)
    {
        var kind = ParseLogKind(source);
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await foreach (var item in hub.SubscribeAsync(item => item.Kind == kind
            && (projectId is null || item.Source.Equals(projectId, StringComparison.OrdinalIgnoreCase)), cancellationToken))
        {
            await http.Response.WriteAsync("event: log\ndata: " + JsonSerializer.Serialize(ToContractLog(item)) + "\n\n", cancellationToken);
            await http.Response.Body.FlushAsync(cancellationToken);
        }
    }

    private static OperationProgress ToProgress(OperationRecord record) => new(Guid.ParseExact(record.Id, "N"),
        ParseOperationKind(record.Action), Enum.TryParse<OperationStatus>(record.Status, true, out var status) ? status : OperationStatus.Failed,
        record.Message, record.Progress, record.Message, record.CompletedUtc ?? record.StartedUtc ?? record.CreatedUtc, record.ErrorCode);

    private static OperationKind ParseOperationKind(string action) => action switch
    {
        "start" => OperationKind.Start,
        "stop" or "force-stop" => OperationKind.Stop,
        "restart" => OperationKind.Restart,
        "deploy" => OperationKind.Deploy,
        "rollback" => OperationKind.Rollback,
        "check-for-updates" => OperationKind.CheckForUpdates,
        "quiet-mode" => OperationKind.QuietMode,
        "start-showcase" => OperationKind.StartShowcase,
        "add-project" => OperationKind.TestBuild,
        "change-hostname" => OperationKind.ChangeHostname,
        "remove-project" => OperationKind.RemoveProject,
        _ => OperationKind.ValidateConfiguration
    };

    private static AbodeToWebLogKind ParseLogKind(string? source) => source?.ToLowerInvariant() switch
    {
        "runtime" => AbodeToWebLogKind.Runtime,
        "build" => AbodeToWebLogKind.Build,
        "audit" => AbodeToWebLogKind.Audit,
        "cloudflare" or "tunnel" => AbodeToWebLogKind.Tunnel,
        _ => AbodeToWebLogKind.System
    };

    private static LogEntry ToContractLog(AbodeToWebLogEvent item) => new(0, item.Timestamp, item.Kind switch
    {
        AbodeToWebLogKind.Runtime => LogSource.Runtime,
        AbodeToWebLogKind.Build => LogSource.Build,
        AbodeToWebLogKind.Audit => LogSource.Audit,
        AbodeToWebLogKind.Tunnel => LogSource.Cloudflare,
        _ => LogSource.System
    }, Enum.Parse<LogSeverity>(item.Severity.ToString()), item.Message,
        item.Kind is AbodeToWebLogKind.Runtime or AbodeToWebLogKind.Build ? item.Source : null,
        Guid.TryParseExact(item.OperationId, "N", out var deploymentId) ? deploymentId : null,
        Guid.TryParseExact(item.OperationId, "N", out var operationId) ? operationId : null);

    private static ActivityEvent ToActivity(AuditEventRecord item) => new(item.Id, item.OccurredUtc, item.Action,
        item.Outcome switch { "succeeded" => ActivityOutcome.Succeeded, "failed" => ActivityOutcome.Failed, "rejected" => ActivityOutcome.Rejected, _ => ActivityOutcome.Started },
        item.Actor, item.TargetId == "system-modes" ? null : item.TargetId,
        Guid.TryParseExact(item.OperationId, "N", out var operationId) ? operationId : null,
        item.Outcome == "failed" ? "Action failed; see its operation details." : "Action " + item.Outcome + ".");

    private static async Task AuditSecretAsync(AbodeToWebDatabase database, HttpContext http, string appId, string action, string key, CancellationToken cancellationToken)
    {
        await database.AppendAuditAsync(new AuditEventRecord(0, DateTimeOffset.UtcNow, Actor(http), action, appId, "succeeded", null,
            JsonSerializer.Serialize(new { key }), http.Connection.RemoteIpAddress?.ToString()), cancellationToken);
    }

    private static AppManifest? FindProject(ManifestProvider manifest, string appId)
    {
        if (!SecretFileStore.IsValidApplicationId(appId))
            throw new ArgumentException("Application ID must be a lowercase slug.", nameof(appId));
        return manifest.Current.Apps.FirstOrDefault(candidate =>
            candidate.Id.Equals(appId, StringComparison.OrdinalIgnoreCase));
    }

    private static string Actor(HttpContext http)
    {
        var identity = http.User.Identity;
        if (identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(identity.Name))
            return identity.Name.Length <= 256 ? identity.Name : identity.Name[..256];
        return "unauthenticated-local-request";
    }

    private static object SafeProblem(string code, string detail) => new { type = "about:blank", title = detail, status = 404, code };
}
