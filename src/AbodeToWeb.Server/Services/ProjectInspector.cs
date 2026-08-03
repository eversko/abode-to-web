using System.Text;
using System.Text.Json;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Core.Processes;

namespace AbodeToWeb.Server.Services;

/// <summary>
/// Performs a bounded, read-only inspection of allowlisted project metadata.
/// It never executes project commands, follows repository metadata links, or
/// reads environment files.
/// </summary>
internal static class ProjectInspector
{
    private const long MaximumPackageJsonBytes = 1024 * 1024;
    private const long MaximumGitHeadBytes = 4 * 1024;
    private const long MaximumGitConfigBytes = 128 * 1024;

    internal static async Task<ProjectDoctorReport> CreateDoctorReportAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var path = ValidateProjectPath(projectPath);
        var framework = await InspectFrameworkAsync(path, cancellationToken);
        var git = await InspectGitAsync(path, cancellationToken);

        var warnings = new List<string>();
        warnings.AddRange(git.Warnings);
        warnings.AddRange(framework.Warnings);

        var adapterIsStatic = framework.Adapter == DeploymentAdapter.Static;
        var staticOutputPresent = adapterIsStatic
            && framework.StaticOutput is not null
            && IsStaticOutputPresent(path, framework.StaticOutput);
        var dependencyInputIsLocked = framework.PackageManager is null || framework.LockFile is not null;
        var status = adapterIsStatic
            ? git.IsRepository
                && git.HeadState == "branch"
                && git.RemoteConfigured
                && staticOutputPresent
                && dependencyInputIsLocked
                ? "supported"
                : "needs-input"
            : "unsupported";

        if (!git.IsRepository)
            warnings.Add("Git metadata was not detected. AbodeToWeb releases must come from a reviewed Git commit.");
        if (git.IsRepository && !git.RemoteConfigured)
            warnings.Add("No Git remote section was detected. Enter and review the deployment remote manually.");
        if (git.IsRepository)
            warnings.Add("Working-tree cleanliness and commit reachability were not inspected because the doctor executes no Git commands. Confirm the exact reviewed commit before publishing.");
        if (adapterIsStatic && !staticOutputPresent)
            warnings.Add("The detected static output does not currently contain an index.html file. Build and review it only after local-setup approval.");
        if (!adapterIsStatic && framework.Framework != DetectedFramework.Unknown)
            warnings.Add("This project requires experimental project execution. It is disabled unless the control-plane manifest explicitly opts in.");

        var requiredInputs = new List<DoctorRequiredInput>
        {
            new("app-id", "Choose a lowercase application id.", true),
            new("display-name", "Choose the dashboard display name.", true),
            new("repository-remote", "Enter the reviewed Git remote name; remote URLs are intentionally not reported.", true),
            new("repository-branch", git.CurrentBranch is null
                ? "Enter the reviewed Git branch because no safe simple branch name was detected."
                : $"Confirm the detected '{git.CurrentBranch}' branch is the intended release branch.", true),
            new("hostname", "Enter a hostname only when you are ready to review routing; the proposal remains disabled.", false)
        };

        if (adapterIsStatic && string.IsNullOrWhiteSpace(framework.StaticOutput))
            requiredInputs.Add(new("static-output-directory", "Enter the deterministic static build output directory.", true));
        if (adapterIsStatic && !staticOutputPresent)
            requiredInputs.Add(new("static-output-build", "Build and review the deterministic static output after APPROVE LOCAL SETUP.", true));
        if (framework.PackageManager is not null && framework.LockFile is null)
            requiredInputs.Add(new("dependency-lockfile", "Add or identify a reviewed lockfile before approving a dependency install.", true));
        if (!adapterIsStatic)
        {
            requiredInputs.Add(new("project-execution-decision",
                "Choose a proven prebuilt static output instead, or separately review the experimental project-execution opt-in.", true));
            requiredInputs.Add(new("health-check",
                "For a dynamic preview, enter a cheap non-mutating health path and port variable.", true));
        }

        var suggestions = framework.Suggestions
            .Select(suggestion => new DoctorCommandSuggestion(
                suggestion.Id,
                suggestion.Purpose,
                suggestion.Command.Executable,
                suggestion.Command.Arguments,
                $"Experimental project execution: {suggestion.Reason}",
                Experimental: true,
                Selected: false))
            .OrderBy(suggestion => PurposeOrder(suggestion.Purpose))
            .ThenBy(suggestion => suggestion.Id, StringComparer.Ordinal)
            .ToArray();
        if (suggestions.Length > 0)
            warnings.Add("Every command suggestion is experimental and unselected. The default proposal executes no project commands.");

        var approvalActions = new[]
        {
            new DoctorApprovalAction(
                "APPROVE LOCAL SETUP",
                "Allows only the exact reviewed repository-local edits and non-elevated checks. It does not authorize installation, deployment, publishing, secrets, or machine-wide changes."),
            new DoctorApprovalAction(
                "APPROVE PUBLISH",
                "After local verification, allows only the exact reviewed Git and AbodeToWeb publish plan. DNS, Cloudflare, firewall, router, power, secrets, and administrator changes still require separate approval.")
        };

        return new ProjectDoctorReport(
            SchemaVersion: 1,
            ChangesMade: false,
            Status: status,
            Git: new DoctorGitFacts(
                git.IsRepository,
                git.HeadState,
                git.CurrentBranch,
                git.RemoteConfigured,
                git.RemoteCount,
                WorkingTreeInspected: false),
            Detected: new DoctorDetectedProject(
                FrameworkName(framework.Framework),
                AdapterName(framework.Adapter),
                framework.PackageManager,
                framework.LockFile,
                framework.StaticOutput,
                staticOutputPresent),
            CommandSuggestions: suggestions,
            ProposedApplication: new DoctorProposedApplication(
                Enabled: false,
                Autostart: false,
                Visibility: "disabled",
                Adapter: AdapterName(framework.Adapter),
                StaticOutputDirectory: framework.StaticOutput,
                ProjectExecutionEnabled: false,
                InstallCommandEnabled: false,
                BuildCommandEnabled: false,
                StartCommandEnabled: false,
                CommandsSelected: false),
            RequiredInputs: requiredInputs,
            Warnings: warnings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ApprovalActions: approvalActions);
    }

    internal static async Task<FrameworkDetection> InspectFrameworkAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var path = ValidateProjectPath(projectPath);
        var warnings = new List<string>();

        if (IsRegularFile(Path.Combine(path, "docker-compose.yml"))
            || IsRegularFile(Path.Combine(path, "docker-compose.yaml"))
            || IsRegularFile(Path.Combine(path, "compose.yml"))
            || IsRegularFile(Path.Combine(path, "compose.yaml")))
        {
            return new FrameworkDetection(
                DetectedFramework.DockerCompose,
                DeploymentAdapter.DockerCompose,
                null,
                null,
                [new("start-compose", "start",
                    new CommandSpec
                    {
                        Executable = "docker.exe",
                        Arguments = ["compose", "up"],
                        TimeoutSeconds = 86_400
                    },
                    "A Docker Compose file was detected; the shared runtime execution path remains preview-only.")],
                ["/health", "/"],
                null,
                "PORT",
                warnings);
        }

        var packagePath = Path.Combine(path, "package.json");
        if (IsRegularFile(packagePath))
        {
            var packageText = await ReadBoundedTextAsync(packagePath, MaximumPackageJsonBytes, "package.too_large", cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(packageText);
            }
            catch (JsonException)
            {
                return Unknown("package.json could not be parsed as JSON. No command was inferred.");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Unknown("package.json must contain a JSON object. No command was inferred.");
                var scripts = root.TryGetProperty("scripts", out var scriptObject) ? scriptObject : default;
                var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "dependencies", "devDependencies" })
                {
                    if (root.TryGetProperty(name, out var objectValue) && objectValue.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in objectValue.EnumerateObject()) dependencies.Add(property.Name);
                    }
                }

                var packageManager = IsRegularFile(Path.Combine(path, "pnpm-lock.yaml")) ? "pnpm"
                    : IsRegularFile(Path.Combine(path, "yarn.lock")) ? "yarn"
                    : "npm";
                var lockFile = packageManager switch
                {
                    "pnpm" => "pnpm-lock.yaml",
                    "yarn" => "yarn.lock",
                    _ => IsRegularFile(Path.Combine(path, "package-lock.json")) ? "package-lock.json" : null
                };
                var executable = packageManager switch
                {
                    "pnpm" => "pnpm.cmd",
                    "yarn" => "yarn.cmd",
                    _ => "npm.cmd"
                };
                var suggestions = new List<InternalSuggestion>();
                IReadOnlyList<string> installArguments = (packageManager, lockFile) switch
                {
                    ("npm", not null) => ["ci"],
                    ("pnpm", not null) => ["install", "--frozen-lockfile"],
                    ("yarn", not null) => ["install", "--immutable"],
                    _ => ["install"]
                };
                suggestions.Add(new InternalSuggestion(
                    "install-locked",
                    "install",
                    new CommandSpec { Executable = executable, Arguments = installArguments, TimeoutSeconds = 900 },
                    lockFile is null
                        ? "A package manifest was detected, but no allowlisted lockfile was found."
                        : $"{lockFile} was detected."));
                if (HasScript(scripts, "build"))
                {
                    suggestions.Add(new InternalSuggestion(
                        "build-package",
                        "build",
                        new CommandSpec { Executable = executable, Arguments = ["run", "build"], TimeoutSeconds = 900 },
                        "A build script is defined; its contents were not read or executed."));
                }
                if (HasScript(scripts, "start"))
                {
                    suggestions.Add(new InternalSuggestion(
                        "start-package",
                        "start",
                        new CommandSpec { Executable = executable, Arguments = ["run", "start"], TimeoutSeconds = 86_400 },
                        "A start script is defined; its contents were not read or executed."));
                }

                var framework = dependencies.Contains("next") ? DetectedFramework.NextJs
                    : dependencies.Contains("vite") ? DetectedFramework.Vite
                    : dependencies.Contains("react") ? DetectedFramework.React
                    : DetectedFramework.Node;
                var isCreateReactApp = dependencies.Contains("react-scripts");
                var isStatic = (framework == DetectedFramework.Vite || isCreateReactApp)
                    && HasScript(scripts, "build")
                    && !HasScript(scripts, "start");
                if (!HasScript(scripts, "start") && !isStatic)
                    warnings.Add("No start script was detected. No start command was inferred.");
                if (lockFile is null)
                    warnings.Add("No allowlisted JavaScript lockfile was detected. Review dependency reproducibility before approving any install.");

                return new FrameworkDetection(
                    framework,
                    isStatic ? DeploymentAdapter.Static : DeploymentAdapter.Node,
                    packageManager,
                    lockFile,
                    suggestions,
                    ["/health", "/api/health", "/"],
                    isStatic ? isCreateReactApp ? "build" : "dist" : null,
                    "PORT",
                    warnings);
            }
        }

        var topLevelEntries = Directory.EnumerateFileSystemEntries(path).Take(257).ToArray();
        if (topLevelEntries.Length > 256)
            warnings.Add("The project root contains more than 256 entries; bounded .NET project detection may be incomplete.");
        if (topLevelEntries.Take(256).Any(entry =>
                Path.GetExtension(entry).Equals(".csproj", StringComparison.OrdinalIgnoreCase) && IsRegularFile(entry)))
        {
            return new FrameworkDetection(
                DetectedFramework.AspNetCore,
                DeploymentAdapter.Command,
                "dotnet",
                null,
                [new("build-dotnet", "build",
                    new CommandSpec
                    {
                        Executable = "dotnet.exe",
                        Arguments = ["publish", "-c", "Release", "--no-self-contained"],
                        TimeoutSeconds = 900
                    },
                    ".NET project metadata was detected; this suggestion is unselected."),
                 new("start-dotnet", "start",
                    new CommandSpec
                    {
                        Executable = "dotnet.exe",
                        Arguments = ["run", "-c", "Release", "--no-build"],
                        TimeoutSeconds = 86_400
                    },
                    ".NET project metadata was detected; this suggestion is unselected and the adapter remains preview-only.")],
                ["/health", "/"],
                null,
                "ASPNETCORE_HTTP_PORTS",
                warnings);
        }

        if (IsRegularFile(Path.Combine(path, "index.html")))
        {
            return new FrameworkDetection(
                DetectedFramework.Static,
                DeploymentAdapter.Static,
                null,
                null,
                [],
                ["/"],
                ".",
                "PORT",
                warnings);
        }

        return Unknown("No allowlisted framework metadata was detected. No command was inferred.");

        FrameworkDetection Unknown(string warning) => new(
            DetectedFramework.Unknown,
            DeploymentAdapter.Command,
            null,
            null,
            [],
            ["/health", "/"],
            null,
            "PORT",
            [.. warnings, warning]);
    }

    private static async Task<SafeGitInspection> InspectGitAsync(string path, CancellationToken cancellationToken)
    {
        var dotGit = Path.Combine(path, ".git");
        if (IsRegularFile(dotGit))
        {
            return new SafeGitInspection(
                true,
                "linked-metadata-uninspected",
                null,
                false,
                0,
                ["This is a linked Git worktree. Its external Git metadata was not followed during read-only inspection."]);
        }
        if (!IsRegularDirectory(dotGit)) return SafeGitInspection.Empty;

        var warnings = new List<string>();
        string? currentBranch = null;
        var headState = "unavailable";
        var headPath = Path.Combine(dotGit, "HEAD");
        if (IsRegularFile(headPath))
        {
            var head = (await ReadBoundedTextAsync(headPath, MaximumGitHeadBytes, "git.head.too_large", cancellationToken)).Trim();
            const string prefix = "ref: refs/heads/";
            if (head.StartsWith(prefix, StringComparison.Ordinal))
            {
                var candidate = head[prefix.Length..];
                if (IsSafeBranchName(candidate))
                {
                    currentBranch = candidate;
                    headState = "branch";
                }
                else
                {
                    headState = "branch-name-redacted";
                    warnings.Add("The current branch name was not reported because it is not a simple safe branch name.");
                }
            }
            else if (head.Length is 40 or 64 && head.All(Uri.IsHexDigit))
            {
                headState = "detached";
                warnings.Add("The repository appears to have a detached HEAD. Choose a reviewed branch before publishing.");
            }
            else
            {
                warnings.Add("Git HEAD metadata was not recognized and was not reported.");
            }
        }
        else
        {
            warnings.Add("Git HEAD metadata could not be inspected.");
        }

        var remoteCount = 0;
        var configPath = Path.Combine(dotGit, "config");
        if (IsRegularFile(configPath))
        {
            var config = await ReadBoundedTextAsync(configPath, MaximumGitConfigBytes, "git.config.too_large", cancellationToken);
            remoteCount = config.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(line => line.StartsWith("[remote \"", StringComparison.OrdinalIgnoreCase) && line.EndsWith("\"]", StringComparison.Ordinal));
        }

        return new SafeGitInspection(
            true,
            headState,
            currentBranch,
            remoteCount > 0,
            remoteCount,
            warnings);
    }

    private static string ValidateProjectPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ProjectInspectionException("path.required", "Provide an existing project directory with --path.");

        string path;
        try { path = Path.GetFullPath(value); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectInspectionException("path.invalid", "The project path is invalid.");
        }

        if (OperatingSystem.IsWindows() && IsNetworkPath(path))
            throw new ProjectInspectionException("path.network_unsupported", "Network project paths are not inspected. Use a canonical directory on a local drive.");
        if (!Directory.Exists(path))
            throw new ProjectInspectionException("path.not_found", "The project directory does not exist.");
        if (!IsRegularDirectory(path))
            throw new ProjectInspectionException("path.reparse_point", "Linked project directories are not inspected. Use the canonical project directory.");
        return path;
    }

    private static async Task<string> ReadBoundedTextAsync(
        string path,
        long maximumBytes,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (!IsRegularFile(path))
            throw new ProjectInspectionException("file.unavailable", "An allowlisted metadata file could not be inspected safely.");

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw new ProjectInspectionException(errorCode, "An allowlisted metadata file exceeds the read-only inspection limit.");

        var bytes = new byte[checked((int)maximumBytes + 1)];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(total, bytes.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total > maximumBytes || (total == maximumBytes && stream.ReadByte() != -1))
            throw new ProjectInspectionException(errorCode, "An allowlisted metadata file exceeds the read-only inspection limit.");

        return Encoding.UTF8.GetString(bytes, 0, total).TrimStart('\uFEFF');
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsRegularDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasScript(JsonElement scripts, string name) =>
        scripts.ValueKind == JsonValueKind.Object
        && scripts.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String;

    private static bool IsStaticOutputPresent(string projectPath, string relativeOutput)
    {
        var outputPath = relativeOutput == "." ? projectPath : Path.Combine(projectPath, relativeOutput);
        return IsRegularDirectory(outputPath) && IsRegularFile(Path.Combine(outputPath, "index.html"));
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return true;

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root)) return false;
        try { return new DriveInfo(root).DriveType == DriveType.Network; }
        catch (Exception exception) when (exception is ArgumentException or IOException) { return false; }
    }

    private static bool IsSafeBranchName(string value) => value.Equals("main", StringComparison.OrdinalIgnoreCase)
        || value.Equals("master", StringComparison.OrdinalIgnoreCase)
        || value.Equals("trunk", StringComparison.OrdinalIgnoreCase)
        || value.Equals("develop", StringComparison.OrdinalIgnoreCase)
        || value.Equals("development", StringComparison.OrdinalIgnoreCase)
        || value.Equals("production", StringComparison.OrdinalIgnoreCase)
        || value.Equals("staging", StringComparison.OrdinalIgnoreCase);

    private static int PurposeOrder(string purpose) => purpose switch
    {
        "install" => 0,
        "build" => 1,
        "start" => 2,
        _ => 3
    };

    private static string FrameworkName(DetectedFramework framework) => framework switch
    {
        DetectedFramework.NextJs => "next-js",
        DetectedFramework.AspNetCore => "asp-net-core",
        DetectedFramework.DockerCompose => "docker-compose",
        _ => framework.ToString().ToLowerInvariant()
    };

    private static string AdapterName(DeploymentAdapter adapter) => adapter switch
    {
        DeploymentAdapter.DockerCompose => "docker-compose",
        _ => adapter.ToString().ToLowerInvariant()
    };
}

internal sealed record InternalSuggestion(
    string Id,
    string Purpose,
    CommandSpec Command,
    string Reason);

internal sealed record FrameworkDetection(
    DetectedFramework Framework,
    DeploymentAdapter Adapter,
    string? PackageManager,
    string? LockFile,
    IReadOnlyList<InternalSuggestion> Suggestions,
    IReadOnlyList<string> HealthPaths,
    string? StaticOutput,
    string PortEnvironmentVariable,
    IReadOnlyList<string> Warnings);

internal sealed record SafeGitInspection(
    bool IsRepository,
    string HeadState,
    string? CurrentBranch,
    bool RemoteConfigured,
    int RemoteCount,
    IReadOnlyList<string> Warnings)
{
    internal static SafeGitInspection Empty { get; } = new(false, "not-a-repository", null, false, 0, []);
}

internal sealed record ProjectDoctorReport(
    int SchemaVersion,
    bool ChangesMade,
    string Status,
    DoctorGitFacts Git,
    DoctorDetectedProject Detected,
    IReadOnlyList<DoctorCommandSuggestion> CommandSuggestions,
    DoctorProposedApplication ProposedApplication,
    IReadOnlyList<DoctorRequiredInput> RequiredInputs,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<DoctorApprovalAction> ApprovalActions);

internal sealed record DoctorGitFacts(
    bool IsRepository,
    string HeadState,
    string? CurrentBranch,
    bool RemoteConfigured,
    int RemoteCount,
    bool WorkingTreeInspected);

internal sealed record DoctorDetectedProject(
    string Framework,
    string Adapter,
    string? PackageManager,
    string? LockFile,
    string? StaticOutputDirectory,
    bool StaticOutputPresent);

internal sealed record DoctorCommandSuggestion(
    string Id,
    string Purpose,
    string Executable,
    IReadOnlyList<string> Arguments,
    string Reason,
    bool Experimental,
    bool Selected);

internal sealed record DoctorProposedApplication(
    bool Enabled,
    bool Autostart,
    string Visibility,
    string Adapter,
    string? StaticOutputDirectory,
    bool ProjectExecutionEnabled,
    bool InstallCommandEnabled,
    bool BuildCommandEnabled,
    bool StartCommandEnabled,
    bool CommandsSelected);

internal sealed record DoctorRequiredInput(string Id, string Prompt, bool Required);

internal sealed record DoctorApprovalAction(string Phrase, string Authorizes);

internal sealed class ProjectInspectionException(string code, string message) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}
