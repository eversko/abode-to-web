using System.Text.RegularExpressions;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Core.Security;
using AbodeToWeb.Infrastructure;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Operations;
using AbodeToWeb.Infrastructure.Processes;

namespace AbodeToWeb.Infrastructure.Deployments;

public sealed record GitCommitInfo(string Sha, string Author, DateTimeOffset? AuthoredAt, string Message);

public sealed record RepositoryGitStatus(
    string Branch,
    string? LocalHead,
    string? RemoteHead,
    bool UpdateAvailable,
    GitCommitInfo? RemoteCommit,
    DateTimeOffset CheckedUtc,
    string? SafeError);

public sealed partial class GitClient(
    StructuredCommandRunner runner,
    ManifestProvider manifest,
    AbodeToWebPaths paths,
    bool allowLocalRepositoryRemotesForTests = false)
{
    private const string MirrorRemoteName = "abode-to-web-origin";

    public async Task ValidateRepositoryAsync(RepositoryOptions repository, string appId, string operationId, CancellationToken cancellationToken = default)
    {
        repository = CanonicalizeRepository(repository);
        EnsureSafeName(repository.Remote, nameof(repository.Remote));
        EnsureSafeBranch(repository.Branch);
        var result = await RunGitAsync(repository.LocalPath, appId, operationId, true, cancellationToken,
            "rev-parse", "--is-inside-work-tree");
        if (!result.Succeeded || !result.CapturedOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException("repository.invalid", "The configured path is not a usable Git repository.");

        var topLevel = await RunGitAsync(repository.LocalPath, appId, operationId, true, cancellationToken,
            "rev-parse", "--path-format=absolute", "--show-toplevel");
        if (!topLevel.Succeeded || !Path.GetFullPath(topLevel.CapturedOutput.Trim()).Equals(
                repository.LocalPath, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException("repository.root_mismatch", "The configured repository path must be its Git worktree root.");

        var common = await RunGitAsync(repository.LocalPath, appId, operationId, true, cancellationToken,
            "rev-parse", "--path-format=absolute", "--git-common-dir");
        if (!common.Succeeded)
            throw new DeploymentException("repository.metadata_invalid", "Git repository metadata could not be validated.");
        var commonPath = Path.GetFullPath(common.CapturedOutput.Trim(), repository.LocalPath);
        var commonValidation = CreatePathPolicy().Validate(commonPath, new PathValidationRequest(true, PathExpectation.Directory));
        if (!commonValidation.IsValid)
            throw new DeploymentException("repository.metadata_outside_root", "Git repository metadata must remain inside an allowed project root.");
    }

    public async Task FetchAsync(RepositoryOptions repository, string appId, string operationId, CancellationToken cancellationToken = default)
    {
        repository = CanonicalizeRepository(repository);
        var remoteUrl = await ValidateRemoteUrlAsync(repository, appId, operationId, cancellationToken);
        EnsureSafeName(repository.Remote, nameof(repository.Remote));
        var mirror = MirrorPath(appId);
        if (!Directory.Exists(mirror))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            var initialized = await RunGitCommandAsync(paths.StateRoot, operationId, false, cancellationToken,
                "init", "--bare", mirror);
            if (!initialized.Succeeded)
                throw new DeploymentException("git.mirror_failed", "Git could not initialize AbodeToWeb's protected repository mirror.");
            var added = await RunGitAsync(mirror, appId, operationId, false, cancellationToken,
                "remote", "add", MirrorRemoteName, remoteUrl);
            if (!added.Succeeded)
                throw new DeploymentException("git.mirror_failed", "Git could not configure AbodeToWeb's protected repository mirror.");
            var refspec = await RunGitAsync(mirror, appId, operationId, false, cancellationToken,
                "config", $"remote.{MirrorRemoteName}.fetch", $"+refs/heads/*:refs/remotes/{MirrorRemoteName}/*");
            if (!refspec.Succeeded)
                throw new DeploymentException("git.mirror_failed", "Git could not configure AbodeToWeb's protected repository mirror.");
        }
        else
        {
            EnsureControlledMirror(mirror);
            var updated = await RunGitAsync(mirror, appId, operationId, false, cancellationToken,
                "remote", "set-url", MirrorRemoteName, remoteUrl);
            if (!updated.Succeeded)
                throw new DeploymentException("git.mirror_failed", "Git could not update AbodeToWeb's protected repository mirror.");
        }
        // A fully-qualified local remote is itself a repository this invocation
        // uses: fetch spawns git-upload-pack inside it, and Git's ownership
        // protection rejects it when the operator owns the folder and AbodeToWeb's
        // service identity runs the fetch. Trust exactly the validated remote
        // path for this invocation; never a wildcard.
        var localRemotePath = Path.IsPathFullyQualified(remoteUrl) && Directory.Exists(remoteUrl)
            ? Path.GetFullPath(remoteUrl)
            : null;
        var result = await RunGitAsync(mirror, localRemotePath, appId, operationId, false, cancellationToken,
            "fetch", "--prune", "--no-tags", MirrorRemoteName);
        if (!result.Succeeded)
            throw new DeploymentException("git.fetch_failed", "Git could not fetch the configured repository. Check connectivity and Git Credential Manager authentication.");
    }

    public async Task<GitCommitInfo> ResolveRemoteCommitAsync(RepositoryOptions repository, string appId, string operationId, CancellationToken cancellationToken = default)
    {
        repository = CanonicalizeRepository(repository);
        EnsureSafeName(repository.Remote, nameof(repository.Remote));
        EnsureSafeBranch(repository.Branch);
        var reference = $"refs/remotes/{MirrorRemoteName}/{repository.Branch}";
        var resolved = await RunGitAsync(MirrorPath(appId), appId, operationId, true, cancellationToken,
            "rev-parse", "--verify", reference + "^{commit}");
        var sha = resolved.CapturedOutput.Trim();
        if (!resolved.Succeeded || !ShaPattern().IsMatch(sha))
            throw new DeploymentException("git.branch_not_found", "The configured remote branch could not be resolved to a commit.");
        return await GetCommitInfoAsync(MirrorPath(appId), sha, appId, operationId, cancellationToken);
    }

    public async Task<RepositoryGitStatus> GetStatusAsync(
        RepositoryOptions repository,
        string appId,
        string operationId,
        bool fetch,
        string? deployedCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            repository = CanonicalizeRepository(repository);
            await ValidateRepositoryAsync(repository, appId, operationId, cancellationToken);
            if (fetch) await FetchAsync(repository, appId, operationId, cancellationToken);
            var remote = await ResolveRemoteCommitAsync(repository, appId, operationId, cancellationToken);
            var deployedHead = deployedCommitSha is not null && ShaPattern().IsMatch(deployedCommitSha)
                ? deployedCommitSha.ToLowerInvariant()
                : null;
            return new RepositoryGitStatus(repository.Branch, deployedHead, remote.Sha,
                !string.Equals(deployedHead, remote.Sha, StringComparison.OrdinalIgnoreCase), remote, DateTimeOffset.UtcNow, null);
        }
        catch (DeploymentException exception)
        {
            return new RepositoryGitStatus(repository.Branch, null, null, false, null, DateTimeOffset.UtcNow, exception.SafeMessage);
        }
    }

    public async Task CreateWorktreeAsync(string repositoryPath, string commitSha, string worktreePath, string appId, string operationId, CancellationToken cancellationToken = default)
    {
        if (!ShaPattern().IsMatch(commitSha)) throw new ArgumentException("Commit SHA is invalid.", nameof(commitSha));
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        _ = CanonicalizeRepositoryPath(repositoryPath);
        var result = await RunGitAsync(MirrorPath(appId), appId, operationId, false, cancellationToken,
            "worktree", "add", "--detach", worktreePath, commitSha);
        if (!result.Succeeded) throw new DeploymentException("git.worktree_failed", "Git could not create the isolated deployment worktree.");
    }

    public async Task RemoveWorktreeAsync(string repositoryPath, string worktreePath, string appId, string operationId, CancellationToken cancellationToken = default)
    {
        _ = CanonicalizeRepositoryPath(repositoryPath);
        var result = await RunGitAsync(MirrorPath(appId), appId, operationId, false, cancellationToken,
            "worktree", "remove", "--force", worktreePath);
        if (!result.Succeeded && Directory.Exists(worktreePath))
            throw new DeploymentException("git.worktree_remove_failed", "Git could not remove an old deployment worktree.");
    }

    private async Task<GitCommitInfo> GetCommitInfoAsync(string repositoryPath, string sha, string appId, string operationId, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repositoryPath, appId, operationId, true, cancellationToken,
            "show", "-s", "--format=%H%x1f%an%x1f%aI%x1f%s", sha);
        if (!result.Succeeded) throw new DeploymentException("git.commit_read_failed", "Git could not read commit metadata.");
        var fields = result.CapturedOutput.Trim().Split((char)0x1f, 4);
        if (fields.Length != 4 || !ShaPattern().IsMatch(fields[0]))
            throw new DeploymentException("git.commit_read_failed", "Git returned invalid commit metadata.");
        return new GitCommitInfo(fields[0].ToLowerInvariant(), fields[1],
            DateTimeOffset.TryParse(fields[2], out var authoredAt) ? authoredAt : null, fields[3]);
    }

    private Task<CommandExecutionResult> RunGitAsync(string repositoryPath, string appId, string operationId, bool capture, CancellationToken cancellationToken, params string[] arguments)
        => RunGitAsync(repositoryPath, null, appId, operationId, capture, cancellationToken, arguments);

    private Task<CommandExecutionResult> RunGitAsync(string repositoryPath, string? additionalSafeDirectory, string appId, string operationId, bool capture, CancellationToken cancellationToken, params string[] arguments)
    {
        _ = appId;
        // Git correctly rejects a repository owned by another Windows identity by
        // default. AbodeToWeb deliberately reads user-owned development repositories
        // from its restricted service identity, so opt in only to the repositories
        // being used by this invocation: the one Git runs in, plus a validated
        // local-path remote when fetch must open it via git-upload-pack. Never
        // mutate global Git config or use the unsafe wildcard form of safe.directory.
        var safeDirectory = Path.GetFullPath(repositoryPath).Replace('\\', '/');
        IEnumerable<string> prefix = ["-c", $"safe.directory={safeDirectory}"];
        if (additionalSafeDirectory is not null)
            prefix = prefix.Concat(["-c", $"safe.directory={Path.GetFullPath(additionalSafeDirectory).Replace('\\', '/')}"]);
        var fullArguments = prefix.Concat(["-C", repositoryPath])
            .Concat(arguments)
            .ToArray();
        return RunGitCommandAsync(repositoryPath, operationId, capture, cancellationToken, fullArguments);
    }

    private Task<CommandExecutionResult> RunGitCommandAsync(string workingDirectory, string operationId, bool capture,
        CancellationToken cancellationToken, params string[] arguments)
    {
        Directory.CreateDirectory(EmptyHooksPath);
        var command = new CommandSpec
        {
            Executable = "git.exe",
            Arguments =
            [
                "-c", $"core.hooksPath={EmptyHooksPath}",
                "-c", "protocol.ext.allow=never",
                "-c", $"protocol.file.allow={(allowLocalRepositoryRemotesForTests ? "always" : "never")}",
                "-c", "credential.helper=",
                "-c", "credential.helper=manager",
                .. arguments
            ],
            TimeoutSeconds = 900
        };
        return runner.RunAsync(new CommandExecutionRequest(command, workingDirectory,
            new Dictionary<string, string?>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "Never",
                // A protected fetch must not inherit machine/user URL rewrite
                // rules such as url.*.insteadOf from its caller.
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_SYSTEM"] = "NUL",
                ["GIT_CONFIG_GLOBAL"] = "NUL",
                ["GIT_CONFIG_COUNT"] = "0",
                ["GIT_CONFIG_PARAMETERS"] = null
            },
            "git", operationId, CaptureOutput: capture, TrustedControlPlaneCommand: true), cancellationToken);
    }

    private async Task<string> ValidateRemoteUrlAsync(RepositoryOptions repository, string appId, string operationId, CancellationToken cancellationToken)
    {
        EnsureSafeName(repository.Remote, nameof(repository.Remote));
        var result = await RunGitAsync(repository.LocalPath, appId, operationId, true, cancellationToken,
            "config", "--local", "--no-includes", "--get", $"remote.{repository.Remote}.url");
        var remoteUrl = result.CapturedOutput.Trim();
        if (!result.Succeeded || !TryNormalizeSupportedRemoteUrl(remoteUrl, repository.Provider, out var actualRemoteUrl))
            throw new DeploymentException("repository.remote_invalid", "The configured Git remote URL is invalid.");
        if (!TryNormalizeSupportedRemoteUrl(repository.ExpectedRemoteUrl, repository.Provider, out var expectedRemoteUrl))
            throw new DeploymentException("repository.expected_remote_invalid", "The reviewed Git remote URL is invalid or unavailable.");
        if (!string.Equals(actualRemoteUrl, expectedRemoteUrl, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException("repository.remote_mismatch", "The configured Git remote no longer matches the reviewed remote URL.");
        return expectedRemoteUrl;
    }

    private bool TryNormalizeSupportedRemoteUrl(string? value, RepositoryProvider provider, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048 || value.Any(char.IsControl)) return false;
        if (Path.IsPathFullyQualified(value))
        {
            if (!allowLocalRepositoryRemotesForTests) return false;
            if (provider != RepositoryProvider.Other) return false;
            return TryNormalizeLocalRemotePath(value, out normalized);
        }

        var githubHttps = Uri.TryCreate(value, UriKind.Absolute, out var uri)
                          && uri.Scheme == Uri.UriSchemeHttps
                          && string.IsNullOrEmpty(uri.UserInfo)
                          && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        var githubSsh = GitHubSshPattern().IsMatch(value);
        if (provider is not (RepositoryProvider.GitHub or RepositoryProvider.Other) || (!githubHttps && !githubSsh)) return false;
        normalized = value;
        return true;
    }

    private bool TryNormalizeLocalRemotePath(string value, out string normalized)
    {
        normalized = string.Empty;
        // Local remotes are an advanced fixture-only escape hatch. Do not let
        // a manifest turn them into UNC/device-backed network fetches.
        if (IsNetworkOrDevicePath(value)) return false;

        var validation = CreatePathPolicy().Validate(value, new PathValidationRequest(true, PathExpectation.Directory));
        if (!validation.IsValid) return false;

        var lexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        // ProjectPathPolicy resolves every reparse point. Requiring the raw
        // lexical identity to already be canonical rejects links even if their
        // final target is still within an allowed root.
        if (!lexical.Equals(validation.Path!.CanonicalPath, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)) return false;

        normalized = validation.Path.CanonicalPath;
        return true;
    }

    private static bool IsNetworkOrDevicePath(string value) =>
        value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal);

    private RepositoryOptions CanonicalizeRepository(RepositoryOptions repository) =>
        repository with { LocalPath = CanonicalizeRepositoryPath(repository.LocalPath) };

    private string CanonicalizeRepositoryPath(string path)
    {
        var validation = CreatePathPolicy().Validate(path, new PathValidationRequest(true, PathExpectation.Directory));
        if (!validation.IsValid)
            throw new DeploymentException("repository.path_invalid", "The repository path is outside the allowed project roots or crosses a link.");
        return validation.Path!.CanonicalPath;
    }

    private ProjectPathPolicy CreatePathPolicy() => new(manifest.Current.AbodeToWeb.ProjectsRoots);

    private string MirrorPath(string appId)
    {
        if (!SafeAppIdPattern().IsMatch(appId)) throw new DeploymentException("app.id_invalid", "Application id is invalid.");
        var path = Path.Combine(paths.StateRoot, "repositories", appId + ".git");
        EnsureControlledMirror(path, requireExisting: false);
        return path;
    }

    private void EnsureControlledMirror(string path, bool requireExisting = true)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.StateRoot)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException("git.mirror_path_invalid", "AbodeToWeb repository mirror path is invalid.");
        if (requireExisting && !Directory.Exists(full))
            throw new DeploymentException("git.mirror_missing", "AbodeToWeb repository mirror is missing; check for disk cleanup or corruption.");
        if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new DeploymentException("git.mirror_link", "AbodeToWeb repository mirrors cannot be junctions or symbolic links.");
    }

    private string EmptyHooksPath => Path.Combine(paths.StateRoot, "empty-git-hooks");

    private static void EnsureSafeName(string value, string parameterName)
    {
        if (!SafeNamePattern().IsMatch(value) || value.StartsWith('-'))
            throw new ArgumentException("Git remote name is invalid.", parameterName);
    }

    private static void EnsureSafeBranch(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || value.StartsWith('-') || value.Contains("..", StringComparison.Ordinal)
            || value.Any(character => char.IsControl(character) || character is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            throw new ArgumentException("Git branch name is invalid.", nameof(value));
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?$")]
    private static partial Regex ShaPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafeNamePattern();

    [GeneratedRegex("^git@github\\.com:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\\.git)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubSshPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$")]
    private static partial Regex SafeAppIdPattern();
}

public sealed class DeploymentException(string code, string safeMessage, Exception? innerException = null)
    : Exception(safeMessage, innerException), ISafeOperationError
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}
