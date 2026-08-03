using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Core.Networking;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Core.Security;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Processes;

namespace AbodeToWeb.Server.Services;

public sealed record DetectProjectRequest(string LocalPath);
public sealed record CloneProjectRequest(string Repository);

public sealed partial class OnboardingService(
    ManifestProvider manifest,
    IManifestValidator validator,
    IPortAllocator ports,
    StructuredCommandRunner commands,
    GatewayRouteRegistry routes,
    AbodeToWebDatabase database)
{
    private readonly ConcurrentDictionary<Guid, CachedDetection> _detections = new();

    public async Task<OnboardingDetection> DetectAsync(string localPath, CancellationToken cancellationToken = default)
    {
        var pathPolicy = new ProjectPathPolicy(manifest.Current.AbodeToWeb.ProjectsRoots);
        var validated = pathPolicy.Validate(localPath, new PathValidationRequest(true, PathExpectation.Directory));
        if (!validated.IsValid) throw new OnboardingException(validated.ErrorCode ?? "path.invalid", validated.ErrorMessage ?? "Project path is invalid.");
        var path = validated.Path!.CanonicalPath;
        var isGit = Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
        var git = isGit
            ? await DetectGitAsync(path, cancellationToken)
            : GitDetection.Empty;
        FrameworkDetection detected;
        try { detected = await ProjectInspector.InspectFrameworkAsync(path, cancellationToken); }
        catch (ProjectInspectionException exception) { throw new OnboardingException(exception.Code, exception.Message); }
        var unavailable = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        foreach (var route in routes.Snapshot()) if (route.InternalPort is not null) unavailable.Add(route.InternalPort.Value);
        var suggestedPort = ports.Allocate(Path.GetFileName(path), unavailable);
        var id = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        var suggestions = detected.Suggestions.Select(suggestion => new CommandSuggestion(
            suggestion.Id, suggestion.Purpose, suggestion.Command.Executable, suggestion.Command.Arguments, suggestion.Reason)).ToArray();
        List<string> warnings = isGit
            ? [.. git.Warnings, .. detected.Warnings]
            : ["The selected directory is not a Git repository; deployment requires Git.", .. detected.Warnings];
        var response = new OnboardingDetection(id, expires, path, isGit, detected.Framework, detected.PackageManager,
            detected.LockFile, git.SuggestedProvider, git.Remotes, git.SuggestedRemote, git.Branch,
            suggestions, detected.HealthPaths, detected.StaticOutput, detected.PortEnvironmentVariable, suggestedPort, warnings);
        _detections[id] = new CachedDetection(response, detected, git, path);
        PurgeExpired();
        return response;
    }

    public async Task<string> CloneAsync(string repository, string operationId, CancellationToken cancellationToken = default)
    {
        var (root, destination) = GetCloneDestination(repository);
        Directory.CreateDirectory(root);
        var command = new CommandSpec { Executable = "gh.exe", Arguments = ["repo", "clone", repository, destination], TimeoutSeconds = 900 };
        var result = await commands.RunAsync(new CommandExecutionRequest(command, root,
            new Dictionary<string, string?> { ["GH_PROMPT_DISABLED"] = "1", ["GIT_TERMINAL_PROMPT"] = "0" },
            "onboarding", operationId, TrustedControlPlaneCommand: true), cancellationToken);
        if (!result.Succeeded) throw new OnboardingException("github.clone_failed", "GitHub CLI could not clone the repository. Check `gh auth status` and the repository name.");
        return destination;
    }

    public (string Root, string Destination) GetCloneDestination(string repository)
    {
        if (!GitHubSlugPattern().IsMatch(repository))
            throw new OnboardingException("github.repository_invalid", "Use a GitHub repository in owner/name form.");
        var name = repository.Split('/')[1];
        var root = manifest.Current.AbodeToWeb.ProjectsRoots[0];
        var destination = Path.Combine(root, name);
        var policy = new ProjectPathPolicy(manifest.Current.AbodeToWeb.ProjectsRoots);
        var validated = policy.Validate(destination, new PathValidationRequest(false, PathExpectation.Directory));
        if (!validated.IsValid) throw new OnboardingException(validated.ErrorCode ?? "path.invalid", validated.ErrorMessage ?? "Clone destination is invalid.");
        if (Directory.Exists(destination)) throw new OnboardingException("github.destination_exists", "A directory with that repository name already exists in the project root.");
        return (root, validated.Path!.CanonicalPath);
    }

    public OnboardingValidation ValidateCreate(CreateProjectRequest request)
    {
        try
        {
            var app = BuildApp(request);
            var candidate = manifest.Current with { Apps = [.. manifest.Current.Apps, app] };
            var result = validator.Validate(candidate, new ManifestValidationContext(GetEnvironment(), true, new PhysicalPathInspector()));
            return new OnboardingValidation(result.IsValid, result.Issues.Select(issue => new ContractValidationIssue(
                issue.Code, issue.Path, issue.Message, issue.Severity == AbodeToWeb.Core.Validation.ValidationSeverity.Warning)).ToArray(), result.IsValid ? app : null);
        }
        catch (OnboardingException exception)
        {
            return new OnboardingValidation(false, [new ContractValidationIssue(exception.Code, "$", exception.Message, false)], null);
        }
    }

    public async Task<AppManifest> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateCreate(request);
        if (!validation.IsValid || validation.NormalizedManifest is null)
            throw new OnboardingException("project.invalid", validation.Issues.Count > 0 ? validation.Issues[0].Message : "Project configuration is invalid.");
        if (await database.IsProjectIdRetiredAsync(validation.NormalizedManifest.Id, cancellationToken))
            throw new OnboardingException("project.id_retired",
                "This project ID was used by a removed project and cannot be reused. Choose a new project ID so old runtime and deployment records remain isolated.");
        var next = await manifest.UpdateAtomicAsync(current =>
        {
            if (current.Apps.Any(app => app.Id.Equals(validation.NormalizedManifest.Id, StringComparison.OrdinalIgnoreCase)))
                throw new OnboardingException("project.duplicate", "A project with this id already exists.");
            return current with { Apps = [.. current.Apps, validation.NormalizedManifest] };
        }, cancellationToken);
        routes.ReloadDesiredConfiguration(next);
        _detections.TryRemove(request.DetectionId, out _);
        return validation.NormalizedManifest;
    }

    private AppManifest BuildApp(CreateProjectRequest request)
    {
        if (!_detections.TryGetValue(request.DetectionId, out var cached) || cached.Detection.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new OnboardingException("detection.expired", "Run project detection again before saving.");
        CommandSpec? Select(string? suggestionId, string purpose)
        {
            if (suggestionId is null) return null;
            var suggestion = cached.Framework.Suggestions.FirstOrDefault(item => item.Id == suggestionId && item.Purpose.Equals(purpose, StringComparison.OrdinalIgnoreCase));
            return suggestion?.Command ?? throw new OnboardingException("suggestion.invalid", $"The selected {purpose} suggestion is not valid for this detection.");
        }
        var install = Select(request.InstallSuggestionId, "install");
        var build = Select(request.BuildSuggestionId, "build");
        var start = Select(request.StartSuggestionId, "start");
        var adapter = cached.Framework.Adapter;
        if (adapter != DeploymentAdapter.Static && start is null)
            throw new OnboardingException("start.required", "Choose a detected start command.");
        if (request.RepositoryProvider is null)
            throw new OnboardingException("repository.provider.required", "Choose the repository provider after reviewing detection.");
        if (string.IsNullOrWhiteSpace(request.RepositoryRemote))
            throw new OnboardingException("repository.remote.required", "Enter the Git remote name to deploy.");
        if (string.IsNullOrWhiteSpace(request.RepositoryBranch))
            throw new OnboardingException("repository.branch.required", "Enter the Git branch to deploy.");
        if (string.IsNullOrWhiteSpace(request.PortEnvironmentVariable))
            throw new OnboardingException("deployment.portVariable.required", "Enter the environment variable used to pass the assigned port.");
        var remoteName = request.RepositoryRemote.Trim();
        if (cached.Git.LocalPathRemotes.Contains(remoteName))
            throw new OnboardingException("repository.local_remote_unsupported",
                "Local-path Git remotes are not supported. Use a reviewed HTTPS or SSH remote instead.");
        if (!cached.Git.RemoteUrls.TryGetValue(remoteName, out var expectedRemoteUrl))
            throw new OnboardingException("repository.remote_unreviewed",
                "The selected Git remote was not safely reviewed during detection. Detect the project again and select a detected remote.");
        return new AppManifest
        {
            Id = request.Id,
            Name = request.Name,
            Description = request.Description,
            Enabled = true,
            Autostart = request.Autostart,
            Protected = request.Protected,
            Repository = new RepositoryOptions
            {
                Provider = request.RepositoryProvider == OnboardingRepositoryProvider.Github
                    ? RepositoryProvider.GitHub
                    : RepositoryProvider.Other,
                LocalPath = cached.Path,
                Remote = remoteName,
                ExpectedRemoteUrl = expectedRemoteUrl,
                Branch = request.RepositoryBranch.Trim()
            },
            Deployment = new DeploymentOptions
            {
                Adapter = adapter,
                Mode = DeploymentMode.Worktree,
                Install = install,
                Build = build,
                Start = start,
                WorkingDirectory = ".",
                StaticOutputDirectory = adapter == DeploymentAdapter.Static ? request.StaticOutputDirectory?.Trim() : null,
                PortEnvironmentVariable = request.PortEnvironmentVariable.Trim()
            },
            Runtime = new RuntimeOptions { InternalPort = request.InternalPort },
            Health = adapter == DeploymentAdapter.Static
                ? new HealthCheckOptions { Type = HealthCheckType.None }
                : new HealthCheckOptions { Type = HealthCheckType.Http, Path = request.HealthPath ?? "/health" },
            Routing = new RoutingOptions
            {
                Hostname = request.Hostname,
                AccessApplicationAudience = request.AccessApplicationAudience
            },
            Visibility = request.Visibility
        };
    }

    private async Task<GitDetection> DetectGitAsync(string path, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var remotesResult = await RunGitInspectionAsync(path, false, cancellationToken, "remote");
        var remotes = remotesResult.Succeeded
            ? remotesResult.CapturedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(remote => SafeRemoteNamePattern().IsMatch(remote) && !remote.StartsWith('-'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(64)
                .ToArray()
            : [];
        if (!remotesResult.Succeeded) warnings.Add("Git remote names could not be inspected. Enter the deployment remote manually.");
        else if (remotes.Length == 0) warnings.Add("No Git remote was detected. Add or enter a remote before deployment.");

        var branchResult = await RunGitInspectionAsync(path, false, cancellationToken, "branch", "--show-current");
        var branch = branchResult.Succeeded ? SingleSafeLine(branchResult.CapturedOutput, 240) : null;
        if (branch is null) warnings.Add("The repository is detached or its current branch could not be detected. Enter the deployment branch manually.");

        var remoteUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localPathRemotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var remote in remotes)
        {
            var urlResult = await RunGitInspectionAsync(path, true, cancellationToken,
                "config", "--local", "--no-includes", "--get", $"remote.{remote}.url");
            var detectedRemoteUrl = urlResult.Succeeded ? SingleSafeLine(urlResult.CapturedOutput, 2_048) : null;
            if (detectedRemoteUrl is null) continue;
            if (Path.IsPathFullyQualified(detectedRemoteUrl))
            {
                localPathRemotes.Add(remote);
                warnings.Add($"Git remote '{remote}' uses a local path, which is not supported for deployment.");
                continue;
            }
            remoteUrls[remote] = detectedRemoteUrl;
        }

        var suggestedRemote = remotes.FirstOrDefault(remote => remote.Equals("origin", StringComparison.OrdinalIgnoreCase))
            ?? remotes.FirstOrDefault();
        OnboardingRepositoryProvider? provider = null;
        if (suggestedRemote is not null && remoteUrls.TryGetValue(suggestedRemote, out var remoteUrl))
        {
            provider = IsGitHubRemote(remoteUrl) ? OnboardingRepositoryProvider.Github : OnboardingRepositoryProvider.Other;
        }
        else if (suggestedRemote is not null) warnings.Add("The repository provider could not be detected safely. Choose it during review.");

        return new GitDetection(remotes, remoteUrls, localPathRemotes, suggestedRemote, branch, provider, warnings);
    }

    private Task<CommandExecutionResult> RunGitInspectionAsync(
        string path,
        bool redactOutputInLogs,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var safeDirectory = Path.GetFullPath(path).Replace('\\', '/');
        var command = new CommandSpec
        {
            Executable = "git.exe",
            Arguments = ["-c", $"safe.directory={safeDirectory}", "-C", path, .. arguments],
            TimeoutSeconds = 15
        };
        return commands.RunAsync(new CommandExecutionRequest(command, path,
            new Dictionary<string, string?>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "Never",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_SYSTEM"] = "NUL",
                ["GIT_CONFIG_GLOBAL"] = "NUL",
                ["GIT_CONFIG_COUNT"] = "0",
                ["GIT_CONFIG_PARAMETERS"] = null
            },
            "onboarding-git", null, CaptureOutput: true, TrustedControlPlaneCommand: true,
            RedactOutputInLogs: redactOutputInLogs), cancellationToken);
    }

    private static string? SingleSafeLine(string value, int maximumLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length is > 0 && trimmed.Length <= maximumLength && !trimmed.Any(char.IsControl)
            ? trimmed
            : null;
    }

    private static bool IsGitHubRemote(string value)
    {
        if (GitHubSshRemotePattern().IsMatch(value)) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.Scheme is "https" or "ssh";
    }

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _detections.Where(entry => entry.Value.Detection.ExpiresAt <= now)) _detections.TryRemove(entry.Key, out _);
    }

    private static Dictionary<string, string> GetEnvironment() => Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>().Where(entry => entry.Key is string && entry.Value is string)
        .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex GitHubSlugPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafeRemoteNamePattern();

    [GeneratedRegex("^git@github\\.com:[^\\r\\n]+$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubSshRemotePattern();

    private sealed record GitDetection(IReadOnlyList<string> Remotes, IReadOnlyDictionary<string, string> RemoteUrls, IReadOnlySet<string> LocalPathRemotes, string? SuggestedRemote, string? Branch,
        OnboardingRepositoryProvider? SuggestedProvider, IReadOnlyList<string> Warnings)
    {
        public static GitDetection Empty { get; } = new([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, null, null, []);
    }
    private sealed record CachedDetection(OnboardingDetection Detection, FrameworkDetection Framework, GitDetection Git, string Path);
}

public sealed class OnboardingException(string code, string message)
    : InvalidOperationException(message), AbodeToWeb.Infrastructure.Operations.ISafeOperationError
{
    public string Code { get; } = code;
    public string SafeMessage => Message;
}
