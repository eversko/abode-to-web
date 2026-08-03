using System.Collections.Concurrent;
using System.Threading.Channels;
using AbodeToWeb.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AbodeToWeb.Infrastructure.Operations;

public interface ISafeOperationError
{
    string Code { get; }
    string SafeMessage { get; }
}

public sealed record OperationProgressEvent(
    string OperationId,
    string TargetId,
    string Action,
    string Status,
    int Progress,
    string Message,
    DateTimeOffset Timestamp,
    string? ErrorCode = null);

public sealed record OperationRequest(
    string Action,
    string TargetId,
    string Actor,
    string? RemoteAddress,
    Func<OperationExecutionContext, CancellationToken, Task> ExecuteAsync);

public sealed class OperationExecutionContext
{
    private readonly Func<int, string, Task> _report;

    internal OperationExecutionContext(string operationId, Func<int, string, Task> report)
    {
        OperationId = operationId;
        _report = report;
    }

    public string OperationId { get; }
    public Task ReportAsync(int progress, string message) => _report(Math.Clamp(progress, 0, 100), message);

    public static OperationExecutionContext CreateSystem(string operationId = "system") =>
        new(operationId, (_, _) => Task.CompletedTask);
}

public sealed class OperationStreamHub
{
    private readonly ConcurrentDictionary<Guid, Channel<OperationProgressEvent>> _channels = new();

    public IAsyncEnumerable<OperationProgressEvent> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<OperationProgressEvent>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _channels[id] = channel;
        return ReadAndRemoveAsync(id, channel.Reader, cancellationToken);
    }

    public void Publish(OperationProgressEvent progress)
    {
        foreach (var channel in _channels.Values) channel.Writer.TryWrite(progress);
    }

    private async IAsyncEnumerable<OperationProgressEvent> ReadAndRemoveAsync(
        Guid id,
        ChannelReader<OperationProgressEvent> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try { await foreach (var item in reader.ReadAllAsync(cancellationToken)) yield return item; }
        finally
        {
            if (_channels.TryRemove(id, out var channel)) channel.Writer.TryComplete();
        }
    }
}

public sealed partial class OperationCoordinator : BackgroundService
{
    private readonly Channel<QueuedOperation> _queue = Channel.CreateBounded<QueuedOperation>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<string, byte> _pendingTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOperationPersistence _persistence;
    private readonly OperationStreamHub _streamHub;
    private readonly ILogger<OperationCoordinator> _logger;
    private readonly int _workerCount;

    public OperationCoordinator(AbodeToWebDatabase database, OperationStreamHub streamHub, ILogger<OperationCoordinator> logger)
        : this(new DatabaseOperationPersistence(database), streamHub, logger)
    {
    }

    internal OperationCoordinator(
        IOperationPersistence persistence,
        OperationStreamHub streamHub,
        ILogger<OperationCoordinator> logger,
        int? workerCount = null)
    {
        _persistence = persistence;
        _streamHub = streamHub;
        _logger = logger;
        _workerCount = workerCount ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    }

    public async Task<string> EnqueueAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetId);
        var conflictKey = request.TargetId.ToLowerInvariant();
        if (!_pendingTargets.TryAdd(conflictKey, 0))
            throw new OperationConflictException(request.TargetId);

        var reservationTransferred = false;
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var operation = new OperationRecord(id, request.Action, request.TargetId, "queued", 0, "Queued", now, null, null, null, null);
            await _persistence.UpsertOperationAsync(operation, cancellationToken);
            if (!_queue.Writer.TryWrite(new QueuedOperation(id, request, now, conflictKey)))
            {
                var rejected = operation with
                {
                    Status = "failed",
                    Message = "The operation queue is full. Try again shortly.",
                    CompletedUtc = DateTimeOffset.UtcNow,
                    ErrorCode = "operation.queue_full",
                    ErrorMessage = "The operation queue is full. Try again shortly."
                };
                await PersistOperationBestEffortAsync(rejected, "queue rejection", CancellationToken.None);
                Publish(rejected);
                throw new OperationQueueFullException();
            }
            reservationTransferred = true;
            Publish(operation);
            return id;
        }
        finally
        {
            // Once queued, the worker owns this reservation. Every failure before
            // that point must release it so a transient SQLite/cancellation error
            // cannot block the target until the service restarts.
            if (!reservationTransferred) _pendingTargets.TryRemove(conflictKey, out _);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.WhenAll(Enumerable.Range(0, _workerCount).Select(_ => WorkerAsync(stoppingToken)));
    }

    private async Task WorkerAsync(CancellationToken stoppingToken)
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(queued, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // ProcessAsync contains operation and persistence failures. Keep this
                // last-resort boundary so a future reporting bug cannot fault the
                // BackgroundService and stop AbodeToWeb's remaining operation workers.
                LogWorkerRecovered(_logger, exception, queued.Id);
                _pendingTargets.TryRemove(queued.ConflictKey, out _);
            }
        }
    }

    private async Task ProcessAsync(QueuedOperation queued, CancellationToken stoppingToken)
    {
        var started = DateTimeOffset.UtcNow;
        var state = new OperationRecord(queued.Id, queued.Request.Action, queued.Request.TargetId,
            "running", 1, "Started", queued.CreatedUtc, started, null, null, null);
        try
        {
            await PersistOperationBestEffortAsync(state, "start", stoppingToken);
            Publish(state);
            var context = new OperationExecutionContext(queued.Id, async (progress, message) =>
            {
                state = state with { Progress = progress, Message = message };
                await PersistOperationBestEffortAsync(state, "progress", stoppingToken);
                Publish(state);
            });
            await queued.Request.ExecuteAsync(context, stoppingToken);
            var completionMessage = state.Message is "Queued" or "Started" ? "Complete" : state.Message;
            state = state with { Status = "succeeded", Progress = 100, Message = completionMessage, CompletedUtc = DateTimeOffset.UtcNow };
            await PersistOperationBestEffortAsync(state, "success", stoppingToken);
            await AppendAuditBestEffortAsync(queued, "succeeded", null, stoppingToken);
            Publish(state);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            state = state with { Status = "cancelled", Message = "AbodeToWeb is stopping", CompletedUtc = DateTimeOffset.UtcNow };
            await PersistOperationBestEffortAsync(state, "cancellation", CancellationToken.None);
            Publish(state);
        }
        catch (Exception exception)
        {
            LogOperationFailed(_logger, exception, queued.Id, queued.Request.Action, queued.Request.TargetId);
            var safeError = SafeError(exception);
            state = state with
            {
                Status = "failed",
                Message = safeError.Message,
                CompletedUtc = DateTimeOffset.UtcNow,
                ErrorCode = safeError.Code,
                ErrorMessage = safeError.Message
            };
            // A secondary SQLite failure must not replace the original operation
            // error, escape this worker, or keep the target reserved forever.
            await PersistOperationBestEffortAsync(state, "failure", CancellationToken.None);
            await AppendAuditBestEffortAsync(queued, "failed", safeError.Message, CancellationToken.None);
            Publish(state);
        }
        finally
        {
            _pendingTargets.TryRemove(queued.ConflictKey, out _);
        }
    }

    private async Task PersistOperationBestEffortAsync(
        OperationRecord state,
        string stage,
        CancellationToken cancellationToken)
    {
        try
        {
            await _persistence.UpsertOperationAsync(state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogOperationPersistenceFailed(_logger, exception, state.Id, stage);
        }
    }

    private async Task AppendAuditBestEffortAsync(
        QueuedOperation queued,
        string outcome,
        string? error,
        CancellationToken cancellationToken)
    {
        var detail = error is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(new { error });
        try
        {
            await _persistence.AppendAuditAsync(new AuditEventRecord(0, DateTimeOffset.UtcNow, queued.Request.Actor,
                queued.Request.Action, queued.Request.TargetId, outcome, queued.Id, detail, queued.Request.RemoteAddress), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogOperationAuditPersistenceFailed(_logger, exception, queued.Id, outcome);
        }
    }

    private void Publish(OperationRecord state) => _streamHub.Publish(new OperationProgressEvent(
        state.Id, state.TargetId, state.Action, state.Status, state.Progress, state.Message,
        DateTimeOffset.UtcNow, state.ErrorCode));

    private static (string Code, string Message) SafeError(Exception exception) => exception switch
    {
        ISafeOperationError safe => (safe.Code, safe.SafeMessage),
        TimeoutException => ("operation.timeout", "The operation timed out."),
        UnauthorizedAccessException => ("operation.permission_denied", "AbodeToWeb does not have permission to access a required resource."),
        IOException => ("operation.resource_unavailable", "A required file or process resource was unavailable."),
        _ => ("operation_failed", "The operation failed. See its build or runtime log for details.")
    };

    private sealed record QueuedOperation(string Id, OperationRequest Request, DateTimeOffset CreatedUtc, string ConflictKey);

    [LoggerMessage(1, LogLevel.Error, "Operation {operationId} ({action} {target}) failed")]
    private static partial void LogOperationFailed(ILogger logger, Exception exception, string operationId, string action, string target);

    [LoggerMessage(2, LogLevel.Error, "Could not persist operation {operationId} during {stage}; processing will continue")]
    private static partial void LogOperationPersistenceFailed(ILogger logger, Exception exception, string operationId, string stage);

    [LoggerMessage(3, LogLevel.Error, "Could not append the {outcome} audit event for operation {operationId}; processing will continue")]
    private static partial void LogOperationAuditPersistenceFailed(ILogger logger, Exception exception, string operationId, string outcome);

    [LoggerMessage(4, LogLevel.Critical, "Operation worker recovered from an unexpected reporting failure for {operationId}")]
    private static partial void LogWorkerRecovered(ILogger logger, Exception exception, string operationId);
}

internal interface IOperationPersistence
{
    Task UpsertOperationAsync(OperationRecord operation, CancellationToken cancellationToken);
    Task<long> AppendAuditAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken);
}

internal sealed class DatabaseOperationPersistence(AbodeToWebDatabase database) : IOperationPersistence
{
    public Task UpsertOperationAsync(OperationRecord operation, CancellationToken cancellationToken) =>
        database.UpsertOperationAsync(operation, cancellationToken);

    public Task<long> AppendAuditAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken) =>
        database.AppendAuditAsync(auditEvent, cancellationToken);
}

public sealed class OperationConflictException(string targetId)
    : InvalidOperationException($"Another operation for '{targetId}' is already queued or running.");

public sealed class OperationQueueFullException()
    : InvalidOperationException("The AbodeToWeb operation queue is full. Try again shortly."), ISafeOperationError
{
    public string Code => "operation.queue_full";
    public string SafeMessage => Message;
}
