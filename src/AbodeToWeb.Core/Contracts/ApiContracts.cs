using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Deployments;
using AbodeToWeb.Core.Health;

namespace AbodeToWeb.Core.Contracts;

public enum ComponentCondition
{
    Unknown,
    Healthy,
    Degraded,
    Unhealthy,
    Offline
}

public sealed record ComponentStatus(ComponentCondition Condition, string Label, string? Detail = null);

public sealed record SystemStatus(
    DateTimeOffset ObservedAt,
    ComponentStatus Pc,
    ComponentStatus AbodeToWeb,
    ComponentStatus Gateway,
    ComponentStatus CloudflareTunnel,
    int RunningProjects,
    int UnhealthyProjects,
    int UpdatesAvailable,
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskFreeBytes,
    long DiskTotalBytes,
    bool QuietMode);

public enum ProjectRuntimeState
{
    Disabled,
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed,
    CircuitOpen
}

public sealed record GitStatus(
    string Branch,
    string? DeployedCommit,
    string? RemoteCommit,
    bool UpdateAvailable,
    string? CommitMessage,
    string? CommitAuthor,
    DateTimeOffset? CommitDate,
    DateTimeOffset? CheckedAt,
    string? Error);

public sealed record ProjectSummary(
    string Id,
    string Name,
    string? Description,
    ProjectRuntimeState RuntimeState,
    HealthState Health,
    GitStatus? Git,
    string? Hostname,
    AppVisibility Visibility,
    bool Protected,
    DateTimeOffset? LastDeploymentAt,
    DateTimeOffset? StartedAt);

public sealed record ProjectDetail(
    ProjectSummary Summary,
    AppManifest DesiredConfiguration,
    DeploymentSummary? ActiveDeployment,
    IReadOnlyList<DeploymentSummary> RecentDeployments,
    IReadOnlyList<SecretKeyInfo> SecretKeys,
    string? SafeStatusMessage);

public sealed record ExternalSiteSummary(
    string Id,
    string Name,
    string? Description,
    string Url,
    string Provider,
    string? ManagementUrl,
    string? RepositoryPath,
    HealthState Health,
    int? HttpStatusCode,
    DateTimeOffset ObservedAt,
    string? SafeStatusMessage);

public sealed record SaveExternalSiteRequest(
    string Name,
    string? Description,
    string Url,
    string Provider,
    string? ManagementUrl,
    string? RepositoryPath);

public sealed record LocalProjectSummary(
    string Id,
    string Name,
    string? Description,
    string RepositoryPath,
    string? PlannedHostname,
    string? Notes,
    bool Exists,
    string? Branch,
    string? LastCommitSha,
    string? LastCommitShortSha,
    string? LastCommitMessage,
    string? LastCommitAuthor,
    DateTimeOffset? LastCommitDate,
    DateTimeOffset ObservedAt,
    string? SafeStatusMessage);

public sealed record SaveLocalProjectRequest(
    string Name,
    string? Description,
    string RepositoryPath,
    string? PlannedHostname,
    string? Notes);

public enum OperationKind
{
    Start,
    Stop,
    Restart,
    Deploy,
    Rollback,
    CheckForUpdates,
    ValidateConfiguration,
    TestBuild,
    QuietMode,
    StartShowcase,
    ChangeHostname,
    RemoveProject,
    ChangeVisibility
}

public enum OperationStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record OperationAccepted(Guid OperationId, OperationKind Kind, string ProgressUrl);

public sealed record CloneProjectAccepted(Guid OperationId, OperationKind Kind, string ProgressUrl, string RepositoryPath);

public sealed record OperationProgress(
    Guid OperationId,
    OperationKind Kind,
    OperationStatus Status,
    string Stage,
    int? Percent,
    string Message,
    DateTimeOffset Timestamp,
    string? ErrorCode = null);

public sealed record DeploymentSummary(
    Guid Id,
    string ProjectId,
    string CommitSha,
    string ShortSha,
    string Branch,
    DeploymentStatus Status,
    string? CommitMessage,
    string? CommitAuthor,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan? Duration,
    bool BuildSucceeded,
    bool HealthSucceeded,
    bool IsActive,
    bool IsKnownGood,
    string? SafeFailureMessage);

public enum LogSource
{
    System,
    Runtime,
    Build,
    Audit,
    Cloudflare
}

public enum LogSeverity
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public sealed record LogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogSource Source,
    LogSeverity Severity,
    string Message,
    string? ProjectId = null,
    Guid? DeploymentId = null,
    Guid? OperationId = null);

public enum ActivityOutcome
{
    Started,
    Succeeded,
    Failed,
    Rejected
}

public sealed record ActivityEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Action,
    ActivityOutcome Outcome,
    string Actor,
    string? ProjectId,
    Guid? OperationId,
    string Summary);

public enum TunnelConnectionState
{
    NotInstalled,
    NotConfigured,
    Stopped,
    Connecting,
    Connected,
    Degraded,
    Unknown
}

public sealed record TunnelStatus(
    TunnelConnectionState State,
    bool CloudflaredInstalled,
    string? Version,
    bool ServiceInstalled,
    bool ServiceRunning,
    bool GatewayReachable,
    string? TunnelName,
    DateTimeOffset CheckedAt,
    string? SafeDiagnostic);

public sealed record SecretKeyInfo(string Key, bool HasValue, DateTimeOffset? UpdatedAt);

public sealed record RollbackRequest(Guid DeploymentId);

public sealed record ReplaceSecretRequest(string Value);

public sealed record ChangeHostnameRequest(string Hostname);
public sealed record ChangeVisibilityRequest(AppVisibility Visibility);

public sealed record QuietModeRequest(bool Enabled);

public sealed record ShowcaseRequest(IReadOnlyList<string>? ProjectIds);

public enum DetectedFramework
{
    Unknown,
    Node,
    Vite,
    NextJs,
    React,
    AspNetCore,
    Static,
    DockerCompose
}

public sealed record CommandSuggestion(string Id, string Purpose, string Executable, IReadOnlyList<string> Arguments, string Reason);

public enum OnboardingRepositoryProvider
{
    Github,
    Other
}

public sealed record OnboardingDetection(
    Guid DetectionId,
    DateTimeOffset ExpiresAt,
    string RepositoryPath,
    bool IsGitRepository,
    DetectedFramework Framework,
    string? PackageManager,
    string? LockFile,
    OnboardingRepositoryProvider? SuggestedRepositoryProvider,
    IReadOnlyList<string> GitRemotes,
    string? SuggestedRemote,
    string? SuggestedBranch,
    IReadOnlyList<CommandSuggestion> SuggestedCommands,
    IReadOnlyList<string> SuggestedHealthPaths,
    string? SuggestedStaticOutputDirectory,
    string? SuggestedPortEnvironmentVariable,
    int SuggestedPort,
    IReadOnlyList<string> Warnings);

public sealed record OnboardingValidation(
    bool IsValid,
    IReadOnlyList<ContractValidationIssue> Issues,
    AppManifest? NormalizedManifest);

public sealed record ContractValidationIssue(string Code, string Path, string Message, bool IsWarning);

public sealed record ConfigurationValidation(bool IsValid, IReadOnlyList<ContractValidationIssue> Issues);

public sealed record CreateProjectRequest(
    Guid DetectionId,
    string Id,
    string Name,
    string? Description,
    OnboardingRepositoryProvider? RepositoryProvider,
    string? RepositoryRemote,
    string? RepositoryBranch,
    string? InstallSuggestionId,
    string? BuildSuggestionId,
    string? StartSuggestionId,
    string? StaticOutputDirectory,
    string? PortEnvironmentVariable,
    string? HealthPath,
    int? InternalPort,
    string Hostname,
    string? AccessApplicationAudience,
    AppVisibility Visibility,
    bool Autostart,
    bool Protected,
    bool RunTestBuild);

public sealed record PortfolioProjectExport(
    string Id,
    string Name,
    string? Description,
    string? Url,
    bool Online,
    HealthState Health,
    DateTimeOffset GeneratedAt);
