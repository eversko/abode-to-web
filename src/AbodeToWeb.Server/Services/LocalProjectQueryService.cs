using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Processes;

namespace AbodeToWeb.Server.Services;

public sealed class LocalProjectQueryService(ManifestProvider manifest, StructuredCommandRunner commands)
{
    public async Task<IReadOnlyList<LocalProjectSummary>> GetAsync(CancellationToken cancellationToken = default)
    {
        var inspections = manifest.Current.LocalProjects.Select(project => InspectAsync(project, cancellationToken));
        var projects = await Task.WhenAll(inspections);
        return projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<LocalProjectSummary> InspectAsync(LocalProjectManifest project, CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        string path;
        try
        {
            path = Path.GetFullPath(project.RepositoryPath);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Map(project, false, null, null, observedAt, "The configured repository path is not a valid path.");
        }

        if (!Directory.Exists(path))
            return Map(project, false, null, null, observedAt, "The repository folder was not found on disk.");
        if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git")))
            return Map(project, true, null, null, observedAt, "The folder exists but is not a Git repository.");

        var branchResult = await RunGitInspectionAsync(path, cancellationToken, "branch", "--show-current");
        var branch = branchResult.Succeeded ? SingleSafeLine(branchResult.CapturedOutput, 240) : null;
        var commitResult = await RunGitInspectionAsync(path, cancellationToken,
            "show", "-s", "--format=%H%x1f%an%x1f%aI%x1f%s", "HEAD");
        var commit = commitResult.Succeeded ? ParseCommit(commitResult.CapturedOutput) : null;
        return Map(project, true, branch, commit, observedAt,
            commit is null ? "The repository's last commit could not be read." : null);
    }

    private static LocalProjectSummary Map(
        LocalProjectManifest project,
        bool exists,
        string? branch,
        LocalCommitInfo? commit,
        DateTimeOffset observedAt,
        string? safeStatusMessage) => new(
            project.Id,
            project.Name,
            project.Description,
            project.RepositoryPath,
            project.PlannedHostname,
            project.Notes,
            exists,
            branch,
            commit?.Sha,
            commit?.Sha is { Length: >= 7 } sha ? sha[..7] : null,
            commit?.Message,
            commit?.Author,
            commit?.Date,
            observedAt,
            safeStatusMessage);

    private static LocalCommitInfo? ParseCommit(string output)
    {
        var line = output.Trim();
        var parts = line.Split('\u001f');
        if (parts.Length < 4) return null;
        var sha = parts[0].Trim();
        if (sha.Length is < 7 or > 64 || !sha.All(Uri.IsHexDigit)) return null;
        if (!DateTimeOffset.TryParse(parts[2].Trim(), out var date)) return null;
        var author = SingleSafeLine(parts[1], 240);
        var message = SingleSafeLine(parts[3], 500);
        return new LocalCommitInfo(sha, author, date, message);
    }

    private Task<CommandExecutionResult> RunGitInspectionAsync(
        string path,
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
            new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "Never" },
            "local-project-git", null, CaptureOutput: true, TrustedControlPlaneCommand: true,
            // Commit metadata is needed by this service, but it is repository-controlled
            // input and must not become a durable control-plane log entry.
            RedactOutputInLogs: true), cancellationToken);
    }

    private static string? SingleSafeLine(string value, int maximumLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length is > 0 && trimmed.Length <= maximumLength && !trimmed.Any(char.IsControl)
            ? trimmed
            : null;
    }

    private sealed record LocalCommitInfo(string Sha, string? Author, DateTimeOffset Date, string? Message);
}
