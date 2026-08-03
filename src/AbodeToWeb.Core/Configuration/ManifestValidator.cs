using System.Net;
using System.Text.RegularExpressions;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Core.Routing;
using AbodeToWeb.Core.Security;
using AbodeToWeb.Core.Validation;

namespace AbodeToWeb.Core.Configuration;

public sealed record ManifestValidationContext(
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    bool RequireRepositoryPathsToExist = false,
    IPathInspector? PathInspector = null,
    bool AllowLocalRepositoryRemotesForTests = false)
{
    public static ManifestValidationContext Empty { get; } = new(new Dictionary<string, string>());
}

public interface IManifestValidator
{
    ValidationResult Validate(AbodeToWebManifest manifest, ManifestValidationContext? context = null);
}

public sealed partial class ManifestValidator : IManifestValidator
{
    public const int SupportedManifestVersion = 1;

    public ValidationResult Validate(AbodeToWebManifest manifest, ManifestValidationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        context ??= ManifestValidationContext.Empty;
        var result = new ValidationResult();

        if (manifest.ManifestVersion != SupportedManifestVersion)
        {
            result.Add(
                "manifest.version.unsupported",
                "$.manifestVersion",
                $"Manifest version must be {SupportedManifestVersion}; received {manifest.ManifestVersion}.");
        }

        ValidateAbodeToWebOptions(manifest.AbodeToWeb, context, result, out var projectPathPolicy);
        var reservedPorts = new HashSet<int>();
        if (manifest.AbodeToWeb?.Dashboard is { Port: >= 1 and <= 65_535 } dashboard)
        {
            reservedPorts.Add(dashboard.Port);
        }

        if (manifest.AbodeToWeb?.Gateway is { Port: >= 1 and <= 65_535 } gateway)
        {
            reservedPorts.Add(gateway.Port);
        }

        ValidateApps(
            manifest.Apps,
            manifest.AbodeToWeb?.ExperimentalProjectExecution == true,
            context,
            projectPathPolicy,
            reservedPorts,
            result);
        ValidateExternalSites(manifest.ExternalSites, manifest.Apps, result);
        ValidateLocalProjects(manifest.LocalProjects, manifest.Apps, manifest.ExternalSites, result);
        ValidateCloudflareAccess(manifest, result);
        return result;
    }

    private static void ValidateLocalProjects(
        IReadOnlyList<LocalProjectManifest>? projects,
        IReadOnlyList<AppManifest>? apps,
        IReadOnlyList<ExternalSiteManifest>? sites,
        ValidationResult result)
    {
        if (projects is null)
        {
            result.Add("localProjects.required", "$.localProjects", "Local projects must be an array.");
            return;
        }

        var ids = (apps ?? []).Where(app => app is not null && !string.IsNullOrWhiteSpace(app.Id))
            .Select(app => app.Id)
            .Concat((sites ?? []).Where(site => site is not null && !string.IsNullOrWhiteSpace(site.Id))
                .Select(site => site.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var path = $"$.localProjects[{index}]";
            if (project is null)
            {
                result.Add("localProject.required", path, "Local project entry cannot be null.");
                continue;
            }

            if (!IsValidAppId(project.Id))
                result.Add("localProject.id.invalid", $"{path}.id", "Local project id must use the lowercase project identifier format.");
            else if (!ids.Add(project.Id))
                result.Add("localProject.id.duplicate", $"{path}.id", $"Resource id '{project.Id}' is already configured.");

            if (string.IsNullOrWhiteSpace(project.Name) || project.Name.Length > 80)
                result.Add("localProject.name.invalid", $"{path}.name", "Local project name must contain 1-80 characters.");
            if (project.Description is { Length: > 500 })
                result.Add("localProject.description.length", $"{path}.description", "Description cannot exceed 500 characters.");
            if (string.IsNullOrWhiteSpace(project.RepositoryPath) || project.RepositoryPath.Length > 1024)
                result.Add("localProject.repositoryPath.invalid", $"{path}.repositoryPath", "Repository path must contain 1-1024 characters.");
            if (project.PlannedHostname is not null && !Hostname.TryParse(project.PlannedHostname, out _))
                result.Add("localProject.plannedHostname.invalid", $"{path}.plannedHostname", "Planned hostname must be a routable domain name.");
            if (project.Notes is { Length: > 500 })
                result.Add("localProject.notes.length", $"{path}.notes", "Notes cannot exceed 500 characters.");
        }
    }

    private static void ValidateExternalSites(
        IReadOnlyList<ExternalSiteManifest>? sites,
        IReadOnlyList<AppManifest>? apps,
        ValidationResult result)
    {
        if (sites is null)
        {
            result.Add("externalSites.required", "$.externalSites", "External sites must be an array.");
            return;
        }

        var ids = (apps ?? []).Where(app => app is not null && !string.IsNullOrWhiteSpace(app.Id))
            .Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sites.Count; index++)
        {
            var site = sites[index];
            var path = $"$.externalSites[{index}]";
            if (site is null)
            {
                result.Add("externalSite.required", path, "External site entry cannot be null.");
                continue;
            }

            if (!IsValidAppId(site.Id))
                result.Add("externalSite.id.invalid", $"{path}.id", "External site id must use the lowercase project identifier format.");
            else if (!ids.Add(site.Id))
                result.Add("externalSite.id.duplicate", $"{path}.id", $"Resource id '{site.Id}' is already configured.");

            if (string.IsNullOrWhiteSpace(site.Name) || site.Name.Length > 80)
                result.Add("externalSite.name.invalid", $"{path}.name", "External site name must contain 1-80 characters.");
            if (site.Description is { Length: > 500 })
                result.Add("externalSite.description.length", $"{path}.description", "Description cannot exceed 500 characters.");
            if (string.IsNullOrWhiteSpace(site.Provider) || site.Provider.Length > 80)
                result.Add("externalSite.provider.invalid", $"{path}.provider", "Provider must contain 1-80 characters.");
            ValidateExternalHttpsUrl(site.Url, $"{path}.url", "externalSite.url.invalid", result);
            if (site.ManagementUrl is not null)
                ValidateExternalHttpsUrl(site.ManagementUrl, $"{path}.managementUrl", "externalSite.managementUrl.invalid", result);
            if (site.RepositoryPath is { Length: > 1024 })
                result.Add("externalSite.repositoryPath.length", $"{path}.repositoryPath", "Repository path cannot exceed 1024 characters.");
        }
    }

    private static void ValidateExternalHttpsUrl(string? value, string path, string code, ValidationResult result)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !Hostname.TryParse(uri.Host, out _))
        {
            result.Add(code, path, "Use an absolute HTTPS URL with a routable domain name and no embedded credentials.");
        }
    }

    private static void ValidateAbodeToWebOptions(
        AbodeToWebOptions? options,
        ManifestValidationContext context,
        ValidationResult result,
        out ProjectPathPolicy? pathPolicy)
    {
        pathPolicy = null;
        if (options is null)
        {
            result.Add("abodeToWeb.required", "$.abodeToWeb", "Abode to Web configuration is required.");
            return;
        }

        ValidateListener(options.Dashboard, "$.abodeToWeb.dashboard", result);
        ValidateListener(options.Gateway, "$.abodeToWeb.gateway", result);
        if (options.Dashboard is not null && options.Gateway is not null &&
            options.Dashboard.Port == options.Gateway.Port &&
            string.Equals(options.Dashboard.Bind, options.Gateway.Bind, StringComparison.OrdinalIgnoreCase))
        {
            result.Add("listener.collision", "$.abodeToWeb.gateway.port", "Dashboard and gateway listeners cannot use the same endpoint.");
        }

        if (options.StartupStaggerSeconds is < 0 or > 300)
        {
            result.Add("startupStagger.range", "$.abodeToWeb.startupStaggerSeconds", "Startup staggering must be between 0 and 300 seconds.");
        }

        if (options.Storage is null)
        {
            result.Add("storage.required", "$.abodeToWeb.storage", "Storage configuration cannot be null.");
        }
        else
        {
            if (options.Storage.RetainedDeploymentsPerApp is < 1 or > 50)
            {
                result.Add("storage.retention.range", "$.abodeToWeb.storage.retainedDeploymentsPerApp", "Retained deployments must be between 1 and 50.");
            }

            if (options.Storage.LogRetentionDays is < 1 or > 365)
            {
                result.Add("storage.logRetention.range", "$.abodeToWeb.storage.logRetentionDays", "Log retention must be between 1 and 365 days.");
            }

            if (options.Storage.MaxLogBytesPerApp is < 1_048_576 or > 10_737_418_240)
            {
                result.Add("storage.logQuota.range", "$.abodeToWeb.storage.maxLogBytesPerApp", "Per-application log quota must be between 1 MiB and 10 GiB.");
            }
        }

        if (options.Cloudflare is null)
        {
            result.Add("cloudflare.required", "$.abodeToWeb.cloudflare", "Cloudflare configuration cannot be null.");
        }
        else if (options.Cloudflare.Enabled && string.IsNullOrWhiteSpace(options.Cloudflare.TunnelName))
        {
            result.Add("cloudflare.tunnelName.required", "$.abodeToWeb.cloudflare.tunnelName", "Tunnel name is required when Cloudflare integration is enabled.");
        }
        else if (options.Cloudflare.TunnelName is { Length: > 100 })
        {
            result.Add("cloudflare.tunnelName.length", "$.abodeToWeb.cloudflare.tunnelName", "Tunnel name cannot exceed 100 characters.");
        }
        else if (options.Cloudflare.TunnelName is { } tunnelName &&
                 tunnelName.Any(character => char.IsControl(character) || character is '/' or '\\'))
        {
            result.Add("cloudflare.tunnelName.invalid", "$.abodeToWeb.cloudflare.tunnelName", "Tunnel name contains an unsupported character.");
        }

        if (options.ProjectsRoots is null || options.ProjectsRoots.Count == 0)
        {
            result.Add("projectsRoots.required", "$.abodeToWeb.projectsRoots", "At least one allowed project root is required.");
            return;
        }

        var expandedRoots = new List<string>();
        var uniqueRoots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var index = 0; index < options.ProjectsRoots.Count; index++)
        {
            var root = options.ProjectsRoots[index];
            var path = $"$.abodeToWeb.projectsRoots[{index}]";
            if (!TryExpand(root, context, path, result, out var expanded))
            {
                continue;
            }

            if (!Path.IsPathFullyQualified(expanded))
            {
                result.Add("projectsRoot.absolute", path, "Project root must expand to a fully qualified path.");
                continue;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(expanded);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                result.Add("projectsRoot.invalid", path, "Project root is not a valid filesystem path.");
                continue;
            }

            if (!uniqueRoots.Add(normalized))
            {
                result.Add("projectsRoot.duplicate", path, "Project root is configured more than once.");
                continue;
            }

            expandedRoots.Add(normalized);
        }

        if (expandedRoots.Count == 0)
        {
            return;
        }

        try
        {
            pathPolicy = new ProjectPathPolicy(expandedRoots, context.PathInspector);
        }
        catch (ArgumentException exception)
        {
            result.Add("projectsRoot.invalid", "$.abodeToWeb.projectsRoots", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.Add("projectsRoot.unavailable", "$.abodeToWeb.projectsRoots", "A project root could not be inspected safely.");
        }
    }

    private static void ValidateApps(
        IReadOnlyList<AppManifest>? apps,
        bool experimentalProjectExecution,
        ManifestValidationContext context,
        ProjectPathPolicy? pathPolicy,
        HashSet<int> reservedPorts,
        ValidationResult result)
    {
        if (apps is null)
        {
            result.Add("apps.required", "$.apps", "Apps must be an array.");
            return;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostnames = new HashSet<Hostname>();
        var ports = new HashSet<int>();
        for (var index = 0; index < apps.Count; index++)
        {
            var app = apps[index];
            var path = $"$.apps[{index}]";
            if (app is null)
            {
                result.Add("app.required", path, "App entry cannot be null.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(app.Id) && !ids.Add(app.Id))
            {
                result.Add("app.id.duplicate", $"{path}.id", $"App id '{app.Id}' is already configured.");
            }

            if (!IsValidAppId(app.Id))
            {
                result.Add("app.id.invalid", $"{path}.id", "App id must be 1-63 lowercase letters, numbers or hyphens, starting with a letter and ending with a letter or number.");
            }

            if (string.IsNullOrWhiteSpace(app.Name) || app.Name.Length > 80)
            {
                result.Add("app.name.invalid", $"{path}.name", "App name must contain 1-80 characters.");
            }

            if (app.Description is { Length: > 500 })
            {
                result.Add("app.description.length", $"{path}.description", "Description cannot exceed 500 characters.");
            }

            if (app.Autostart && (!app.Enabled || app.Visibility == AppVisibility.Disabled))
            {
                result.Add("app.autostart.disabled", $"{path}.autostart", "A disabled app cannot start automatically.");
            }

            if (app.DemoGroups is null)
            {
                result.Add("app.demoGroups.required", $"{path}.demoGroups", "Demo groups must be an array.");
            }
            else if (app.DemoGroups.Any(group => !IsValidAppId(group)))
            {
                result.Add("app.demoGroups.invalid", $"{path}.demoGroups", "Demo group ids must use the same lowercase identifier format as app ids.");
            }

            var repositoryPath = ValidateRepository(app, path, context, pathPolicy, result);
            ValidateDeployment(app, path, experimentalProjectExecution, result);
            ValidateDeploymentWorkingDirectory(app, path, context, pathPolicy, repositoryPath, result);
            ValidateRuntime(app.Runtime, path, ports, reservedPorts, result);
            ValidateHealth(app.Health, path, result);
            ValidateRouting(app, path, hostnames, result);
        }
    }

    private static void ValidateCloudflareAccess(AbodeToWebManifest manifest, ValidationResult result)
    {
        var access = manifest.AbodeToWeb?.Cloudflare?.Access;
        if (access is not null && !IsValidCloudflareAccessTeamDomain(access.TeamDomain))
        {
            result.Add(
                "cloudflare.access.teamDomain.invalid",
                "$.abodeToWeb.cloudflare.access.teamDomain",
                "Cloudflare Access team domain must be an HTTPS URL like https://your-team.cloudflareaccess.com without a path, query, fragment or custom port.");
        }

        if (manifest.Apps is null)
        {
            return;
        }

        for (var index = 0; index < manifest.Apps.Count; index++)
        {
            var app = manifest.Apps[index];
            if (app is null || app.Visibility != AppVisibility.Protected)
            {
                continue;
            }

            if (access is null)
            {
                result.Add(
                    "cloudflare.access.required",
                    "$.abodeToWeb.cloudflare.access",
                    "Cloudflare Access team configuration is required when an app has protected visibility.");
            }

            var audience = app.Routing?.AccessApplicationAudience;
            if (!IsValidCloudflareAccessAudience(audience))
            {
                result.Add(
                    "routing.accessApplicationAudience.invalid",
                    $"$.apps[{index}].routing.accessApplicationAudience",
                    "Protected apps require the 64-character hexadecimal Application Audience (AUD) tag from Cloudflare Access.");
            }
        }
    }

    internal static bool IsValidCloudflareAccessTeamDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var hostname = uri.IdnHost;
        const string suffix = ".cloudflareaccess.com";
        return hostname.Length > suffix.Length
               && hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
               && hostname[..^suffix.Length].All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '-' or '.');
    }

    internal static bool IsValidCloudflareAccessAudience(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static ValidatedProjectPath? ValidateRepository(
        AppManifest app,
        string appPath,
        ManifestValidationContext context,
        ProjectPathPolicy? pathPolicy,
        ValidationResult result)
    {
        if (app.Deployment is not null && app.Deployment.Mode == DeploymentMode.Worktree && app.Repository is null)
        {
            result.Add("repository.required", $"{appPath}.repository", "Worktree deployments require a Git repository.");
            return null;
        }

        if (app.Repository is null)
        {
            return null;
        }

        var repository = app.Repository;
        if (!IsSimpleName(repository.Remote))
        {
            result.Add("repository.remote.invalid", $"{appPath}.repository.remote", "Git remote name contains unsupported characters.");
        }

        if (!IsValidGitBranch(repository.Branch))
        {
            result.Add("repository.branch.invalid", $"{appPath}.repository.branch", "Git branch name is not a safe valid ref name.");
        }

        if (!TryExpand(repository.LocalPath, context, $"{appPath}.repository.localPath", result, out var expanded))
        {
            return null;
        }

        if (pathPolicy is null)
        {
            return null;
        }

        if (repository.ExpectedRemoteUrl is not null
            && !IsValidExpectedRemoteUrl(repository.ExpectedRemoteUrl, repository.Provider, pathPolicy,
                context.AllowLocalRepositoryRemotesForTests))
        {
            result.Add("repository.expectedRemoteUrl.invalid", $"{appPath}.repository.expectedRemoteUrl",
                "Expected remote URL must be a supported GitHub HTTPS/SSH remote without embedded credentials.");
        }

        var pathResult = pathPolicy.Validate(expanded, new PathValidationRequest(
            context.RequireRepositoryPathsToExist,
            PathExpectation.Directory));
        if (!pathResult.IsValid)
        {
            result.Add(pathResult.ErrorCode!, $"{appPath}.repository.localPath", pathResult.ErrorMessage!);
            return null;
        }

        return pathResult.Path;
    }

    private static void ValidateDeploymentWorkingDirectory(
        AppManifest app,
        string appPath,
        ManifestValidationContext context,
        ProjectPathPolicy? pathPolicy,
        ValidatedProjectPath? repositoryPath,
        ValidationResult result)
    {
        if (app.Deployment is null || pathPolicy is null || repositoryPath is null ||
            !IsSafeRelativePath(app.Deployment.WorkingDirectory))
        {
            return;
        }

        var combined = Path.Combine(repositoryPath.LexicalPath, app.Deployment.WorkingDirectory);
        var pathResult = pathPolicy.Validate(combined, new PathValidationRequest(
            context.RequireRepositoryPathsToExist,
            PathExpectation.Directory));
        if (!pathResult.IsValid)
        {
            result.Add(pathResult.ErrorCode!, $"{appPath}.deployment.workingDirectory", pathResult.ErrorMessage!);
        }
    }

    private static void ValidateDeployment(
        AppManifest app,
        string appPath,
        bool experimentalProjectExecution,
        ValidationResult result)
    {
        var deployment = app.Deployment;
        var path = $"{appPath}.deployment";
        if (deployment is null)
        {
            result.Add("deployment.required", path, "Deployment configuration is required.");
            return;
        }

        if (!IsSafeRelativePath(deployment.WorkingDirectory))
        {
            result.Add("deployment.workingDirectory.invalid", $"{path}.workingDirectory", "Working directory must be a relative path without parent traversal.");
        }

        if (deployment.Mode != DeploymentMode.Worktree)
        {
            result.Add("deployment.mode.unsupported", $"{path}.mode", "AbodeToWeb v1 supports only isolated Git worktree deployments.");
        }

        if (deployment.EnvironmentFile is not null)
        {
            result.Add("deployment.environmentFile.unsupported", $"{path}.environmentFile",
                "Repository environment files are not loaded. Manage runtime values through AbodeToWeb's protected per-project secrets file.");
        }

        var requiresProjectExecution = deployment.Adapter != DeploymentAdapter.Static
            || deployment.Install is not null
            || deployment.Build is not null
            || deployment.Start is not null;
        if (requiresProjectExecution && !experimentalProjectExecution)
        {
            result.Add(
                "deployment.execution.experimentalDisabled",
                path,
                "Project-controlled install, build, and start execution is preview-only. Use a prebuilt static app with no commands, or explicitly set abodeToWeb.experimentalProjectExecution to true after reviewing the shared runtime trust boundary.");
        }

        if (deployment.Adapter == DeploymentAdapter.Static)
        {
            if (string.IsNullOrWhiteSpace(deployment.StaticOutputDirectory) || !IsSafeRelativePath(deployment.StaticOutputDirectory))
            {
                result.Add("deployment.staticOutput.required", $"{path}.staticOutputDirectory", "Static apps require a safe relative output directory.");
            }

            if (deployment.Start is not null)
            {
                result.Add("deployment.staticStart.unexpected", $"{path}.start", "Static apps are served by AbodeToWeb and cannot define a start command.");
            }
        }
        else if (deployment.Adapter is DeploymentAdapter.Command or DeploymentAdapter.Node && deployment.Start is null)
        {
            result.Add("deployment.start.required", $"{path}.start", "Command and Node apps require a start command.");
        }
        else if (deployment.Adapter == DeploymentAdapter.DockerCompose)
        {
            if (deployment.Start is null)
            {
                result.Add("deployment.composeStart.required", $"{path}.start", "Docker Compose apps require a foreground 'docker compose up' start command.");
            }
            else
            {
                var executable = Path.GetFileNameWithoutExtension(deployment.Start.Executable);
                if (!executable.Equals("docker", StringComparison.OrdinalIgnoreCase)
                    || !deployment.Start.Arguments.Any(argument => argument.Equals("compose", StringComparison.OrdinalIgnoreCase))
                    || !deployment.Start.Arguments.Any(argument => argument.Equals("up", StringComparison.OrdinalIgnoreCase)))
                    result.Add("deployment.composeStart.invalid", $"{path}.start", "Docker Compose start must execute 'docker compose ... up' as structured arguments.");
                if (deployment.Start.Arguments.Any(argument => argument.Equals("-d", StringComparison.OrdinalIgnoreCase)
                                                              || argument.Equals("--detach", StringComparison.OrdinalIgnoreCase)))
                    result.Add("deployment.composeStart.detached", $"{path}.start", "Docker Compose must remain in the foreground so AbodeToWeb can supervise its logs and lifecycle.");
            }
        }

        AddCommandIssues(deployment.Install, $"{path}.install", result, required: false);
        AddCommandIssues(deployment.Build, $"{path}.build", result, required: false);
        AddCommandIssues(deployment.Start, $"{path}.start", result, required: false);

        if (!IsEnvironmentVariableName(deployment.PortEnvironmentVariable))
        {
            result.Add("deployment.portVariable.invalid", $"{path}.portEnvironmentVariable", "Port environment variable must use letters, numbers and underscores and cannot start with a number.");
        }
        else if (!EnvironmentVariablePolicy.IsAllowedSecretKey(deployment.PortEnvironmentVariable))
        {
            result.Add("deployment.portVariable.reserved", $"{path}.portEnvironmentVariable", "Port environment variable conflicts with a process-loading or AbodeToWeb control variable.");
        }

        if (deployment.CandidateStartupTimeoutSeconds is < 1 or > 1800)
        {
            result.Add("deployment.candidateTimeout.range", $"{path}.candidateStartupTimeoutSeconds", "Candidate startup timeout must be between 1 and 1800 seconds.");
        }

        if (deployment.DrainSeconds is < 0 or > 300)
        {
            result.Add("deployment.drain.range", $"{path}.drainSeconds", "Drain time must be between 0 and 300 seconds.");
        }
    }

    private static void ValidateRuntime(RuntimeOptions? runtime, string appPath, HashSet<int> ports, HashSet<int> reservedPorts, ValidationResult result)
    {
        var path = $"{appPath}.runtime";
        if (runtime is null)
        {
            result.Add("runtime.required", path, "Runtime configuration cannot be null.");
            return;
        }

        if (runtime.InternalPort is { } port)
        {
            if (port is < 1024 or > 65_535)
            {
                result.Add("runtime.port.range", $"{path}.internalPort", "Internal port must be between 1024 and 65535.");
            }
            else if (reservedPorts.Contains(port))
            {
                result.Add("runtime.port.reserved", $"{path}.internalPort", $"Internal port {port} is reserved by a AbodeToWeb listener.");
            }
            else if (!ports.Add(port))
            {
                result.Add("runtime.port.duplicate", $"{path}.internalPort", $"Internal port {port} is assigned to more than one app.");
            }
        }

        if (runtime.MaxRestarts is < 0 or > 100)
        {
            result.Add("runtime.maxRestarts.range", $"{path}.maxRestarts", "Maximum restarts must be between 0 and 100.");
        }

        if (runtime.RestartWindowSeconds is < 10 or > 86_400)
        {
            result.Add("runtime.restartWindow.range", $"{path}.restartWindowSeconds", "Restart window must be between 10 and 86400 seconds.");
        }

        if (runtime.InitialBackoffSeconds is < 0 or > 3600 || runtime.MaxBackoffSeconds < runtime.InitialBackoffSeconds || runtime.MaxBackoffSeconds > 3600)
        {
            result.Add("runtime.backoff.invalid", $"{path}.maxBackoffSeconds", "Backoff must be 0-3600 seconds and maximum backoff cannot be less than initial backoff.");
        }

        if (runtime.StableRuntimeSeconds is < 0 or > 86_400)
        {
            result.Add("runtime.stableRuntime.range", $"{path}.stableRuntimeSeconds", "Stable runtime must be between 0 and 86400 seconds.");
        }

        if (runtime.GracefulShutdownSeconds is < 1 or > 300)
        {
            result.Add("runtime.shutdownGrace.range", $"{path}.gracefulShutdownSeconds", "Graceful shutdown time must be between 1 and 300 seconds.");
        }
    }

    private static void ValidateHealth(HealthCheckOptions? health, string appPath, ValidationResult result)
    {
        var path = $"{appPath}.health";
        if (health is null)
        {
            result.Add("health.required", path, "Health configuration cannot be null.");
            return;
        }

        if (health.Type == HealthCheckType.Http &&
            (string.IsNullOrWhiteSpace(health.Path) || !health.Path.StartsWith('/') ||
             Uri.TryCreate(health.Path, UriKind.Absolute, out _) || health.Path.Contains("..", StringComparison.Ordinal)))
        {
            result.Add("health.path.invalid", $"{path}.path", "HTTP health path must be a local absolute-path reference without traversal.");
        }

        if (health.IntervalSeconds is < 2 or > 3600)
        {
            result.Add("health.interval.range", $"{path}.intervalSeconds", "Health interval must be between 2 and 3600 seconds.");
        }

        if (health.TimeoutSeconds is < 1 or > 300 || health.TimeoutSeconds >= health.IntervalSeconds)
        {
            result.Add("health.timeout.range", $"{path}.timeoutSeconds", "Health timeout must be at least one second and less than the interval.");
        }

        if (health.HealthyThreshold is < 1 or > 20 || health.UnhealthyThreshold is < 1 or > 20)
        {
            result.Add("health.threshold.range", path, "Health thresholds must be between 1 and 20.");
        }
    }

    private static void ValidateRouting(AppManifest app, string appPath, HashSet<Hostname> hostnames, ValidationResult result)
    {
        var routing = app.Routing;
        var path = $"{appPath}.routing";
        if (routing is null)
        {
            result.Add("routing.required", path, "Routing configuration cannot be null.");
            return;
        }
        if (app.Visibility != AppVisibility.Disabled && string.IsNullOrWhiteSpace(routing.Hostname))
        {
            result.Add("routing.hostname.required", $"{path}.hostname", "Public and protected apps require a hostname.");
        }
        else if (!string.IsNullOrWhiteSpace(routing.Hostname))
        {
            if (!Hostname.TryParse(routing.Hostname, out var hostname, out var error))
            {
                result.Add("routing.hostname.invalid", $"{path}.hostname", error!);
            }
            else if (!hostnames.Add(hostname))
            {
                result.Add("routing.hostname.duplicate", $"{path}.hostname", $"Hostname '{hostname}' is assigned to more than one app.");
            }
        }

        if (routing.RequestTimeoutSeconds is < 1 or > 3600)
        {
            result.Add("routing.timeout.range", $"{path}.requestTimeoutSeconds", "Request timeout must be between 1 and 3600 seconds.");
        }

        if (routing.MaxRequestBodyBytes is < 1024 or > 10_737_418_240)
        {
            result.Add("routing.bodyLimit.range", $"{path}.maxRequestBodyBytes", "Request body limit must be between 1 KiB and 10 GiB.");
        }
    }

    private static void ValidateListener(ListenerOptions? listener, string path, ValidationResult result)
    {
        if (listener is null)
        {
            result.Add("listener.required", path, "Listener configuration is required.");
            return;
        }

        if (!IPAddress.TryParse(listener.Bind, out var address) || !IPAddress.IsLoopback(address))
        {
            result.Add("listener.bind.loopback", $"{path}.bind", "Listener must bind to a loopback IP address.");
        }

        if (listener.Port is < 1 or > 65_535)
        {
            result.Add("listener.port.range", $"{path}.port", "Listener port must be between 1 and 65535.");
        }
    }

    private static void AddCommandIssues(CommandSpec? command, string path, ValidationResult result, bool required)
    {
        if (command is null && !required)
        {
            return;
        }

        result.AddRange(CommandSpecValidator.Validate(command, path).Issues);
    }

    private static bool TryExpand(
        string? value,
        ManifestValidationContext context,
        string path,
        ValidationResult result,
        out string expanded)
    {
        expanded = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            result.Add("value.required", path, "Value cannot be empty.");
            return false;
        }

        var expansion = EnvironmentVariableExpander.Expand(value, context.EnvironmentVariables);
        if (!expansion.IsSuccess)
        {
            result.Add("environment.missing", path, $"Environment variable(s) are not defined: {string.Join(", ", expansion.MissingVariables)}.");
            return false;
        }

        expanded = expansion.Value!;
        return true;
    }

    private static bool IsValidAppId(string? value) =>
        value is { Length: >= 1 and <= 63 } &&
        value[0] is >= 'a' and <= 'z' &&
        char.IsAsciiLetterOrDigit(value[^1]) &&
        value.All(character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '-');

    private static bool IsSimpleName(string? value) =>
        value is { Length: >= 1 and <= 64 } &&
        value[0] != '-' &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsValidGitBranch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value is "@" or "." ||
            value.StartsWith('-') || value.StartsWith('/') || value.EndsWith('/') || value.EndsWith('.') ||
            value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("//", StringComparison.Ordinal) || value.Contains("@{", StringComparison.Ordinal))
        {
            return false;
        }

        if (!value.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character) && "~^:?*[\\".IndexOf(character) < 0))
        {
            return false;
        }

        return value.Split('/').All(component =>
            component.Length > 0 &&
            !component.StartsWith('.') &&
            !component.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        return !value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(component => component == "..");
    }

    private static bool IsEnvironmentVariableName(string? value) =>
        value is { Length: >= 1 and <= 128 } &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsValidExpectedRemoteUrl(
        string? value,
        RepositoryProvider provider,
        ProjectPathPolicy pathPolicy,
        bool allowLocalRepositoryRemotesForTests)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048 || value.Any(char.IsControl)) return false;
        if (Path.IsPathFullyQualified(value))
        {
            if (!allowLocalRepositoryRemotesForTests) return false;
            if (provider != RepositoryProvider.Other) return false;
            if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)) return false;
            var local = pathPolicy.Validate(value, new PathValidationRequest(true, PathExpectation.Directory));
            if (!local.IsValid) return false;
            var lexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return lexical.Equals(local.Path!.CanonicalPath, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }
        var githubHttps = Uri.TryCreate(value, UriKind.Absolute, out var uri)
                          && uri.Scheme == Uri.UriSchemeHttps
                          && string.IsNullOrEmpty(uri.UserInfo)
                          && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        var githubSsh = GitHubSshRemotePattern().IsMatch(value);
        return provider is RepositoryProvider.GitHub or RepositoryProvider.Other && (githubHttps || githubSsh);
    }

    [GeneratedRegex("^git@github\\.com:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\\.git)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubSshRemotePattern();
}
