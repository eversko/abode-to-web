namespace AbodeToWeb.Infrastructure.Logging;

public enum AbodeToWebLogKind
{
    System,
    Runtime,
    Build,
    Audit,
    Tunnel
}

public enum AbodeToWebLogSeverity
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public sealed record AbodeToWebLogEvent(
    DateTimeOffset Timestamp,
    AbodeToWebLogKind Kind,
    AbodeToWebLogSeverity Severity,
    string Source,
    string Message,
    string? OperationId = null,
    string? Stream = null);

public sealed record LogQuery(
    AbodeToWebLogKind Kind,
    string Source,
    int Limit = 500,
    string? Search = null,
    AbodeToWebLogSeverity? MinimumSeverity = null);
