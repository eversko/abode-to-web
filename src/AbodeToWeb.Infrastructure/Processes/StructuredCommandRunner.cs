using System.Diagnostics;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Infrastructure.Logging;
using AbodeToWeb.Infrastructure.Runtime;

namespace AbodeToWeb.Infrastructure.Processes;

public sealed record CommandExecutionRequest(
    CommandSpec Command,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    string LogSource,
    string? OperationId,
    AbodeToWebLogKind LogKind = AbodeToWebLogKind.Build,
    bool CaptureOutput = false,
    string? IsolationKey = null,
    bool TrustedControlPlaneCommand = false,
    bool RedactOutputInLogs = false);

public sealed record CommandExecutionResult(
    int ExitCode,
    TimeSpan Duration,
    bool TimedOut,
    string CapturedOutput,
    string SafeSummary)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public sealed class StructuredCommandRunner
{
    private const int MaximumCapturedCharacters = 1024 * 1024;
    private readonly BoundedLogStore _logs;
    private readonly IProcessLauncher _processLauncher;

    public StructuredCommandRunner(BoundedLogStore logs, IProcessLauncher? processLauncher = null)
    {
        _logs = logs;
        _processLauncher = processLauncher ?? DedicatedRuntimeProcessLauncher.Instance;
    }

    public async Task<CommandExecutionResult> RunAsync(CommandExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        if (!request.TrustedControlPlaneCommand && string.IsNullOrWhiteSpace(request.IsolationKey))
            throw new ArgumentException("Untrusted commands require an application isolation key.", nameof(request));
        if (request.TrustedControlPlaneCommand && request.IsolationKey is not null)
            throw new ArgumentException("A control-plane command cannot also request application isolation.", nameof(request));
        var startInfo = ProcessStartInfoFactory.Create(request.Command, request.WorkingDirectory, request.Environment,
            inheritParentEnvironment: request.TrustedControlPlaneCommand);
        using var process = _processLauncher.Start(startInfo, "command-" + Guid.NewGuid().ToString("N"), request.IsolationKey);
        var captured = request.CaptureOutput ? new System.Text.StringBuilder() : null;
        var captureLock = new Lock();
        var timedOut = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Command.TimeoutSeconds));
        var stdout = PumpAsync(process.StandardOutput, "stdout", AbodeToWebLogSeverity.Information, request, captured, captureLock, timeout.Token);
        var stderr = PumpAsync(process.StandardError, "stderr", AbodeToWebLogSeverity.Error, request, captured, captureLock, timeout.Token);
        var pumps = Task.WhenAll(stdout, stderr);
        try
        {
            await process.Process.WaitForExitAsync(timeout.Token);
            if (!pumps.IsCompleted)
            {
                using var drain = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                drain.CancelAfter(TimeSpan.FromSeconds(2));
                try { await pumps.WaitAsync(drain.Token); }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
                {
                    // The root exited but descendants retained its output handles.
                    // A one-shot build/install command may not daemonize children.
                    process.Job.Terminate(125);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            process.Job.Terminate(124);
            await process.Process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            process.Job.Terminate(130);
            throw;
        }
        finally
        {
            if (!pumps.IsCompleted)
            {
                using var finalDrain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await pumps.WaitAsync(finalDrain.Token); } catch (OperationCanceledException) { }
            }
        }
        if (pumps.IsCompleted)
        {
            try { await pumps; }
            catch (OperationCanceledException) when (timedOut) { }
        }
        else
        {
            _ = pumps.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        var exitCode = process.Process.ExitCode;
        var summary = timedOut ? "Command timed out." : exitCode == 0 ? "Command completed." : $"Command exited with code {exitCode}.";
        await _logs.AppendAsync(new AbodeToWebLogEvent(DateTimeOffset.UtcNow, request.LogKind,
            exitCode == 0 && !timedOut ? AbodeToWebLogSeverity.Information : AbodeToWebLogSeverity.Error,
            request.LogSource, summary, request.OperationId, "abode-to-web"), cancellationToken);
        return new CommandExecutionResult(exitCode, started.Elapsed, timedOut, captured?.ToString() ?? string.Empty, summary);
    }

    private async Task PumpAsync(
        StreamReader reader,
        string stream,
        AbodeToWebLogSeverity severity,
        CommandExecutionRequest request,
        System.Text.StringBuilder? captured,
        Lock captureLock,
        CancellationToken cancellationToken)
    {
        await foreach (var line in BoundedTextLineReader.ReadLinesAsync(reader, cancellationToken))
        {
            var capturedLine = ScrubCredentialUrls(line);
            if (captured is not null)
            {
                lock (captureLock)
                {
                    if (captured.Length < MaximumCapturedCharacters)
                        captured.Append(capturedLine.AsSpan(0, Math.Min(capturedLine.Length, MaximumCapturedCharacters - captured.Length))).AppendLine();
                }
            }
            var logLine = request.RedactOutputInLogs ? "[sensitive command output redacted]" : capturedLine;
            await _logs.AppendAsync(new AbodeToWebLogEvent(DateTimeOffset.UtcNow, request.LogKind, severity,
                request.LogSource, logLine, request.OperationId, stream), cancellationToken);
        }
    }

    private static string ScrubCredentialUrls(string value)
    {
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return value;
        var authorityStart = scheme + 3;
        var slash = value.IndexOf('/', authorityStart);
        var at = value.IndexOf('@', authorityStart);
        if (at < 0 || (slash >= 0 && at > slash)) return value;
        return value[..authorityStart] + "[redacted]@" + value[(at + 1)..];
    }
}
