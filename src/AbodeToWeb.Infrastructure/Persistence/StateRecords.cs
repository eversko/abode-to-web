namespace AbodeToWeb.Infrastructure.Persistence;

public sealed record ProjectStateRecord(
    string AppId,
    string DesiredState,
    string RuntimeState,
    string HealthState,
    string? ActiveDeploymentId,
    int? ProcessId,
    DateTimeOffset? ProcessStartedUtc,
    string? ExecutablePath,
    int RestartCount,
    DateTimeOffset? CircuitOpenUntilUtc,
    DateTimeOffset UpdatedUtc,
    string? LastError);

public sealed record DeploymentRecord(
    string Id,
    string AppId,
    string OperationId,
    string CommitSha,
    string Branch,
    string WorktreePath,
    string State,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    long? DurationMilliseconds,
    string? BuildOutcome,
    string? HealthOutcome,
    string? CommitMessage,
    string? CommitAuthor,
    bool IsKnownGood,
    string? FailureReason,
    int? InternalPort);

public sealed record OperationRecord(
    string Id,
    string Action,
    string TargetId,
    string Status,
    int Progress,
    string Message,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record AuditEventRecord(
    long Id,
    DateTimeOffset OccurredUtc,
    string Actor,
    string Action,
    string TargetId,
    string Outcome,
    string? OperationId,
    string DetailJson,
    string? RemoteAddress);

public sealed record RouteStateRecord(
    string AppId,
    string Hostname,
    string? DeploymentId,
    int? InternalPort,
    string Status,
    string? StaticPath,
    DateTimeOffset UpdatedUtc);
