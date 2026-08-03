using System.Collections.Concurrent;
using AbodeToWeb.Infrastructure.Operations;
using AbodeToWeb.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AbodeToWeb.IntegrationTests;

public sealed class OperationCoordinatorIntegrationTests
{
    [Fact]
    public async Task Cancelled_persistence_releases_the_target_reservation()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var database = new AbodeToWebDatabase(temporary.Paths);
        await database.InitializeAsync();
        using var coordinator = new OperationCoordinator(
            database, new OperationStreamHub(), NullLogger<OperationCoordinator>.Instance);
        var request = Request("cancelled-target");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.EnqueueAsync(request, cancellation.Token));

        var operationId = await coordinator.EnqueueAsync(request);
        Assert.NotEmpty(operationId);
    }

    [Fact]
    public async Task Full_queue_rejects_without_dropping_or_permanently_reserving_the_target()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var database = new AbodeToWebDatabase(temporary.Paths);
        await database.InitializeAsync();
        using var coordinator = new OperationCoordinator(
            database, new OperationStreamHub(), NullLogger<OperationCoordinator>.Instance);

        for (var index = 0; index < 128; index++)
            _ = await coordinator.EnqueueAsync(Request($"queued-{index}"));

        await Assert.ThrowsAsync<OperationQueueFullException>(() =>
            coordinator.EnqueueAsync(Request("overflow")));
        await Assert.ThrowsAsync<OperationQueueFullException>(() =>
            coordinator.EnqueueAsync(Request("overflow")));

        var rejected = (await database.GetRecentOperationsAsync(10))
            .Where(item => item.TargetId == "overflow")
            .ToArray();
        Assert.Equal(2, rejected.Length);
        Assert.All(rejected, item =>
        {
            Assert.Equal("failed", item.Status);
            Assert.Equal("operation.queue_full", item.ErrorCode);
        });
    }

    [Fact]
    public async Task Failure_reporting_persistence_errors_do_not_stop_the_worker_or_retain_the_target()
    {
        var persistence = new FaultingOperationPersistence("faulting-target");
        using var coordinator = new OperationCoordinator(
            persistence, new OperationStreamHub(), NullLogger<OperationCoordinator>.Instance, workerCount: 1);
        await coordinator.StartAsync(CancellationToken.None);

        try
        {
            _ = await coordinator.EnqueueAsync(new OperationRequest(
                "test", "faulting-target", "integration-test", null,
                (_, _) => throw new PrimaryOperationException()));

            await persistence.WriteFailureAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await persistence.AuditFailureAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var recoveredExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (true)
            {
                try
                {
                    _ = await coordinator.EnqueueAsync(new OperationRequest(
                        "test", "faulting-target", "integration-test", null,
                        (_, _) =>
                        {
                            recoveredExecution.TrySetResult();
                            return Task.CompletedTask;
                        }));
                    break;
                }
                catch (OperationConflictException) when (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(10);
                }
            }

            await recoveredExecution.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var failed = Assert.Single(persistence.WriteAttempts, item => item.Status == "failed");
            Assert.Equal("primary.failure", failed.ErrorCode);
            Assert.Equal("The requested test action failed safely.", failed.ErrorMessage);
            Assert.NotNull(persistence.FailedAudit);
            Assert.Contains("The requested test action failed safely.", persistence.FailedAudit.DetailJson, StringComparison.Ordinal);
            Assert.DoesNotContain("simulated persistence failure", persistence.FailedAudit.DetailJson, StringComparison.Ordinal);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await coordinator.StopAsync(stop.Token);
        }
    }

    [Fact]
    public async Task Progress_persistence_errors_do_not_abort_the_action_or_erase_its_final_message()
    {
        var persistence = new FaultingOperationPersistence(
            "progress-target",
            operation => operation.Status == "running" && operation.Progress == 50,
            _ => false);
        using var coordinator = new OperationCoordinator(
            persistence, new OperationStreamHub(), NullLogger<OperationCoordinator>.Instance, workerCount: 1);
        await coordinator.StartAsync(CancellationToken.None);

        try
        {
            _ = await coordinator.EnqueueAsync(new OperationRequest(
                "test", "progress-target", "integration-test", null,
                async (context, _) => await context.ReportAsync(50, "Candidate is healthy")));

            await persistence.WriteFailureAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var succeeded = await persistence.SuccessfulTerminalWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(100, succeeded.Progress);
            Assert.Equal("Candidate is healthy", succeeded.Message);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await coordinator.StopAsync(stop.Token);
        }
    }

    private static OperationRequest Request(string target) => new(
        "test", target, "integration-test", null, (_, _) => Task.CompletedTask);

    private sealed class FaultingOperationPersistence(
        string targetId,
        Func<OperationRecord, bool>? shouldFailWrite = null,
        Func<AuditEventRecord, bool>? shouldFailAudit = null) : IOperationPersistence
    {
        private readonly ConcurrentQueue<OperationRecord> _writeAttempts = new();
        private readonly Func<OperationRecord, bool> _shouldFailWrite = shouldFailWrite ?? (operation => operation.Status == "failed");
        private readonly Func<AuditEventRecord, bool> _shouldFailAudit = shouldFailAudit ?? (audit => audit.Outcome == "failed");

        internal TaskCompletionSource WriteFailureAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AuditFailureAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<OperationRecord> SuccessfulTerminalWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IReadOnlyList<OperationRecord> WriteAttempts => _writeAttempts.ToArray();
        internal AuditEventRecord? FailedAudit { get; private set; }

        public Task UpsertOperationAsync(OperationRecord operation, CancellationToken cancellationToken)
        {
            _writeAttempts.Enqueue(operation);
            if (operation.TargetId.Equals(targetId, StringComparison.OrdinalIgnoreCase) && _shouldFailWrite(operation))
            {
                WriteFailureAttempted.TrySetResult();
                return Task.FromException(new IOException("simulated persistence failure"));
            }
            if (operation.Status == "succeeded") SuccessfulTerminalWrite.TrySetResult(operation);
            return Task.CompletedTask;
        }

        public Task<long> AppendAuditAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken)
        {
            if (auditEvent.TargetId.Equals(targetId, StringComparison.OrdinalIgnoreCase) && _shouldFailAudit(auditEvent))
            {
                FailedAudit = auditEvent;
                AuditFailureAttempted.TrySetResult();
                return Task.FromException<long>(new IOException("simulated persistence failure"));
            }
            return Task.FromResult(1L);
        }
    }

    private sealed class PrimaryOperationException : InvalidOperationException, ISafeOperationError
    {
        public string Code => "primary.failure";
        public string SafeMessage => "The requested test action failed safely.";
    }
}
