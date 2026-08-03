using System.Collections.Concurrent;
using System.Diagnostics;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Core.Supervision;
using AbodeToWeb.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace AbodeToWeb.Infrastructure.Runtime;

public enum ManagedRuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Backoff,
    CircuitOpen,
    Exited
}

public sealed record ManagedProcessRequest(
    string InstanceId,
    string AppId,
    string? DeploymentId,
    CommandSpec Command,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    RestartPolicySettings RestartSettings,
    TimeSpan GracefulShutdown,
    bool EnableAutomaticRestart = true);

public sealed record ManagedProcessSnapshot(
    string InstanceId,
    string AppId,
    string? DeploymentId,
    ManagedRuntimeState State,
    int? ProcessId,
    DateTimeOffset? StartedUtc,
    string? ExecutablePath,
    int RestartCount,
    DateTimeOffset? RetryAfter,
    int? LastExitCode,
    string? LastError);

public sealed partial class ApplicationSupervisor : IAsyncDisposable, IManagedProcessTreeSnapshotProvider
{
    private readonly ConcurrentDictionary<string, ManagedInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLogStore _logs;
    private readonly ILogger<ApplicationSupervisor> _logger;
    private readonly IProcessLauncher _processLauncher;
    private readonly CancellationTokenSource _lifetime = new();

    public ApplicationSupervisor(
        BoundedLogStore logs,
        ILogger<ApplicationSupervisor> logger,
        IProcessLauncher? processLauncher = null)
    {
        _logs = logs;
        _logger = logger;
        _processLauncher = processLauncher ?? DedicatedRuntimeProcessLauncher.Instance;
    }

    public event Func<ManagedProcessSnapshot, Task>? StateChanged;

    public IReadOnlyList<ManagedProcessSnapshot> GetSnapshots() => _instances.Values.Select(instance => instance.Snapshot()).ToArray();

    public bool TryGetSnapshot(string instanceId, out ManagedProcessSnapshot snapshot)
    {
        if (_instances.TryGetValue(instanceId, out var instance)) { snapshot = instance.Snapshot(); return true; }
        snapshot = default!;
        return false;
    }

    public async ValueTask<ManagedProcessTreeSnapshot?> CaptureProcessTreeAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (!_instances.TryGetValue(instanceId, out var instance)) return null;

        await instance.Gate.WaitAsync(cancellationToken);
        try
        {
            if (instance.Process is not { } launched || launched.Process.HasExited) return null;
            return new ManagedProcessTreeSnapshot(launched.Process.Id, launched.Job.GetProcessIds());
        }
        finally
        {
            instance.Gate.Release();
        }
    }

    public async Task<ManagedProcessSnapshot> StartAsync(ManagedProcessRequest request, CancellationToken cancellationToken = default)
    {
        var instance = _instances.GetOrAdd(request.InstanceId, _ => new ManagedInstance(request));
        await instance.Gate.WaitAsync(cancellationToken);
        try
        {
            if (instance.Process is { Process.HasExited: false }) return instance.Snapshot();
            instance.CancelPendingRestart();
            instance.Process?.Dispose();
            instance.Process = null;
            instance.Request = request;
            instance.StopRequested = false;
            instance.History = RestartHistory.Empty;
            await LaunchAsync(instance, cancellationToken);
            return instance.Snapshot();
        }
        finally { instance.Gate.Release(); }
    }

    public async Task StopAsync(string instanceId, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance)) return;
        await instance.Gate.WaitAsync(cancellationToken);
        try
        {
            instance.StopRequested = true;
            instance.CancelPendingRestart();
            instance.State = ManagedRuntimeState.Stopping;
            await NotifyAsync(instance);
            if (instance.Process is not { } launched)
            {
                instance.State = ManagedRuntimeState.Stopped;
                ClearEnvironment(instance);
                await NotifyAsync(instance);
                return;
            }

            try
            {
                try { launched.StandardInput.Close(); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
                if (!force && !launched.Process.HasExited)
                {
                    try { launched.Process.CloseMainWindow(); } catch (InvalidOperationException) { }
                    using var graceful = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    graceful.CancelAfter(instance.Request.GracefulShutdown);
                    try { await launched.Process.WaitForExitAsync(graceful.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                }

                // A root process can exit while descendants remain. The Job is the
                // authoritative process-tree boundary, so always terminate it on stop.
                launched.Job.Terminate(force ? 137u : 143u);
                if (!launched.Process.HasExited) await launched.Process.WaitForExitAsync(cancellationToken);
                instance.State = ManagedRuntimeState.Stopped;
                instance.LastExitCode = SafeExitCode(launched.Process);
                await NotifyAsync(instance);
            }
            finally
            {
                if (ReferenceEquals(instance.Process, launched)) instance.Process = null;
                launched.Dispose();
                ClearEnvironment(instance);
            }
        }
        finally { instance.Gate.Release(); }
    }

    public async Task StopAllAsync(Func<ManagedProcessSnapshot, bool>? predicate = null, CancellationToken cancellationToken = default)
    {
        var snapshots = GetSnapshots().Where(snapshot => predicate?.Invoke(snapshot) ?? true).ToArray();
        await Task.WhenAll(snapshots.Select(snapshot => StopAsync(snapshot.InstanceId, false, cancellationToken)));
    }

    public async Task<ManagedProcessSnapshot> RestartAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance)) throw new KeyNotFoundException($"No managed process '{instanceId}' exists.");
        var request = instance.Request;
        await StopAsync(instanceId, false, cancellationToken);
        return await StartAsync(request, cancellationToken);
    }

    private async Task LaunchAsync(ManagedInstance instance, CancellationToken cancellationToken)
    {
        instance.State = ManagedRuntimeState.Starting;
        instance.LastError = null;
        await NotifyAsync(instance);
        var startInfo = ProcessStartInfoFactory.Create(instance.Request.Command, instance.Request.WorkingDirectory, instance.Request.Environment);
        try
        {
            var launched = _processLauncher.Start(startInfo, instance.Request.InstanceId, instance.Request.AppId);
            var previous = instance.Process;
            instance.Process = launched;
            previous?.Dispose();
            instance.StartedUtc = new DateTimeOffset(launched.Process.StartTime.ToUniversalTime());
            instance.ExecutablePath = launched.Process.ExecutablePath;
            instance.State = ManagedRuntimeState.Running;
            _ = PumpAsync(instance, launched.StandardOutput, "stdout", AbodeToWebLogSeverity.Information, _lifetime.Token);
            _ = PumpAsync(instance, launched.StandardError, "stderr", AbodeToWebLogSeverity.Error, _lifetime.Token);
            _ = MonitorExitAsync(instance, launched, _lifetime.Token);
            await NotifyAsync(instance);
        }
        catch (Exception exception)
        {
            instance.State = ManagedRuntimeState.Exited;
            instance.LastError = SafeError(exception);
            await NotifyAsync(instance);
            throw;
        }
    }

    private async Task PumpAsync(ManagedInstance instance, StreamReader reader, string stream, AbodeToWebLogSeverity severity, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in BoundedTextLineReader.ReadLinesAsync(reader, cancellationToken))
            {
                await _logs.AppendAsync(new AbodeToWebLogEvent(DateTimeOffset.UtcNow, AbodeToWebLogKind.Runtime, severity,
                    instance.Request.AppId, line, instance.Request.DeploymentId, stream), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            LogPipeClosed(_logger, exception, instance.Request.InstanceId);
        }
    }

    private async Task MonitorExitAsync(ManagedInstance instance, LaunchedProcess launched, CancellationToken cancellationToken)
    {
        try { await launched.Process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }

        CancellationTokenSource? pendingRestart = null;
        var restartDelay = TimeSpan.Zero;
        try { await instance.Gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (!ReferenceEquals(instance.Process, launched)) return;
            var exitCode = SafeExitCode(launched.Process);
            instance.LastExitCode = exitCode;
            instance.State = instance.StopRequested ? ManagedRuntimeState.Stopped : ManagedRuntimeState.Exited;
            instance.Process = null;

            var treeStopped = true;
            try { launched.Job.Terminate(instance.StopRequested ? 143u : 137u); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or ObjectDisposedException)
            {
                treeStopped = false;
                instance.LastError = "Windows could not confirm that the complete application process tree stopped.";
                LogTreeTerminationFailed(_logger, exception, instance.Request.InstanceId);
            }
            await NotifyAsync(instance);

            if (!treeStopped)
            {
                instance.State = ManagedRuntimeState.CircuitOpen;
                ClearEnvironment(instance);
                await NotifyAsync(instance);
                return;
            }
            if (!instance.Request.EnableAutomaticRestart)
            {
                ClearEnvironment(instance);
                return;
            }
            var exitedAt = DateTimeOffset.UtcNow;
            var decision = RestartPolicyEvaluator.EvaluateExit(instance.Request.RestartSettings, instance.History,
                instance.StartedUtc ?? exitedAt, exitedAt, exitCode ?? -1, instance.StopRequested);
            instance.History = decision.History;
            instance.RestartCount = decision.AttemptsInWindow;
            instance.RetryAfter = decision.RetryAfter;
            if (decision.CircuitOpen)
            {
                instance.State = ManagedRuntimeState.CircuitOpen;
                instance.LastError = decision.Reason;
                ClearEnvironment(instance);
                await NotifyAsync(instance);
                return;
            }
            if (!decision.ShouldRestart)
            {
                ClearEnvironment(instance);
                return;
            }
            instance.State = ManagedRuntimeState.Backoff;
            pendingRestart = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            instance.PendingRestart = pendingRestart;
            restartDelay = decision.Delay;
            await NotifyAsync(instance);
        }
        catch (Exception exception)
        {
            instance.State = ManagedRuntimeState.Exited;
            instance.LastError = SafeError(exception);
            ClearEnvironment(instance);
            await NotifyAsync(instance);
        }
        finally
        {
            instance.Gate.Release();
            launched.Dispose();
        }

        if (pendingRestart is null) return;
        try
        {
            await Task.Delay(restartDelay, pendingRestart.Token);
            await instance.Gate.WaitAsync(pendingRestart.Token);
            try
            {
                if (!ReferenceEquals(instance.PendingRestart, pendingRestart) || instance.StopRequested) return;
                instance.PendingRestart = null;
                await LaunchAsync(instance, pendingRestart.Token);
            }
            finally { instance.Gate.Release(); }
        }
        catch (OperationCanceledException) when (pendingRestart.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await instance.Gate.WaitAsync(CancellationToken.None);
            try
            {
                instance.State = ManagedRuntimeState.Exited;
                instance.LastError = SafeError(exception);
                ClearEnvironment(instance);
                await NotifyAsync(instance);
            }
            finally { instance.Gate.Release(); }
        }
        finally
        {
            if (ReferenceEquals(instance.PendingRestart, pendingRestart)) instance.PendingRestart = null;
            pendingRestart.Dispose();
        }
    }

    private static void ClearEnvironment(ManagedInstance instance) =>
        instance.Request = instance.Request with
        {
            Environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        };

    private async Task NotifyAsync(ManagedInstance instance)
    {
        var handler = StateChanged;
        if (handler is null) return;
        var snapshot = instance.Snapshot();
        foreach (var subscriber in handler.GetInvocationList().Cast<Func<ManagedProcessSnapshot, Task>>())
        {
            try { await subscriber(snapshot); }
            catch (Exception exception) { LogStateSubscriberFailed(_logger, exception, snapshot.InstanceId); }
        }
    }

    private static int? SafeExitCode(SupervisedProcess process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string SafeError(Exception exception) => exception switch
    {
        RuntimeCredentialException credential => credential.Message,
        FileNotFoundException => "The configured executable was not found.",
        UnauthorizedAccessException => "AbodeToWeb cannot access the executable or working directory.",
        System.ComponentModel.Win32Exception => "Windows could not start or manage the application process.",
        _ => "The application process could not be started."
    };

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await StopAllAsync(cancellationToken: CancellationToken.None);
        foreach (var instance in _instances.Values) instance.Dispose();
        _lifetime.Dispose();
    }

    [LoggerMessage(2, LogLevel.Debug, "Application log pipe closed for {instanceId}")]
    private static partial void LogPipeClosed(ILogger logger, Exception exception, string instanceId);

    [LoggerMessage(3, LogLevel.Error, "Could not terminate the complete process tree for {instanceId}")]
    private static partial void LogTreeTerminationFailed(ILogger logger, Exception exception, string instanceId);

    [LoggerMessage(4, LogLevel.Error, "A process-state subscriber failed for {instanceId}")]
    private static partial void LogStateSubscriberFailed(ILogger logger, Exception exception, string instanceId);

    private sealed class ManagedInstance(ManagedProcessRequest request) : IDisposable
    {
        internal ManagedProcessRequest Request = request;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal LaunchedProcess? Process;
        internal CancellationTokenSource? PendingRestart;
        internal ManagedRuntimeState State = ManagedRuntimeState.Stopped;
        internal DateTimeOffset? StartedUtc;
        internal string? ExecutablePath;
        internal bool StopRequested;
        internal int RestartCount;
        internal DateTimeOffset? RetryAfter;
        internal int? LastExitCode;
        internal string? LastError;
        internal RestartHistory History = RestartHistory.Empty;

        internal ManagedProcessSnapshot Snapshot() => new(Request.InstanceId, Request.AppId, Request.DeploymentId,
            State, Process is { Process.HasExited: false } ? Process.Process.Id : null, StartedUtc, ExecutablePath,
            RestartCount, RetryAfter, LastExitCode, LastError);

        internal void CancelPendingRestart()
        {
            var pending = PendingRestart;
            PendingRestart = null;
            if (pending is not null && !pending.IsCancellationRequested) pending.Cancel();
        }

        public void Dispose()
        {
            CancelPendingRestart();
            Process?.Dispose();
            Gate.Dispose();
        }
    }
}
