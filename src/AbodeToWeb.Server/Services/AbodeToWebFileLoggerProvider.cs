using AbodeToWeb.Infrastructure.Logging;

namespace AbodeToWeb.Server.Services;

public sealed class AbodeToWebFileLoggerProvider(BoundedLogStore logs) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new AbodeToWebFileLogger(logs, categoryName);
    public void Dispose() { }

    private sealed class AbodeToWebFileLogger(BoundedLogStore logs, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null)
            {
                // Keep production responses free of exception details, but retain a
                // bounded, redacted diagnostic in the local administrator-only log.
                // Stack traces are deliberately omitted.
                var root = exception.GetBaseException();
                var detail = $"{root.GetType().Name}: {root.Message}"
                    .Replace('\r', ' ')
                    .Replace('\n', ' ');
                if (detail.Length > 2_048) detail = detail[..2_048] + "…";
                message = $"{message} [{detail}]";
            }
            if (string.IsNullOrWhiteSpace(message)) return;
            _ = logs.AppendAsync(new AbodeToWebLogEvent(DateTimeOffset.UtcNow, AbodeToWebLogKind.System,
                logLevel switch
                {
                    LogLevel.Trace => AbodeToWebLogSeverity.Trace,
                    LogLevel.Debug => AbodeToWebLogSeverity.Debug,
                    LogLevel.Information => AbodeToWebLogSeverity.Information,
                    LogLevel.Warning => AbodeToWebLogSeverity.Warning,
                    LogLevel.Error => AbodeToWebLogSeverity.Error,
                    LogLevel.Critical => AbodeToWebLogSeverity.Critical,
                    _ => AbodeToWebLogSeverity.Information
                }, "abode-to-web", message, null, category));
        }
    }
}
