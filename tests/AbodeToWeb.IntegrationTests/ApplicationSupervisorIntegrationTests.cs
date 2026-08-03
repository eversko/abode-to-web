using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Processes;
using AbodeToWeb.Core.Supervision;
using AbodeToWeb.Infrastructure.Logging;
using AbodeToWeb.Infrastructure.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace AbodeToWeb.IntegrationTests;

public sealed class ApplicationSupervisorIntegrationTests
{
    [Fact]
    public async Task Starts_Node_fixture_captures_output_serves_health_and_stops_cleanly()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var port = IntegrationTestWait.GetAvailableLoopbackPort();
        var logStore = CreateLogStore(temporary);
        await using var supervisor = new ApplicationSupervisor(
            logStore, NullLogger<ApplicationSupervisor>.Instance, new CurrentIdentityTestProcessLauncher());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = CreateRequest(temporary, port, "healthy", enableAutomaticRestart: false);

        var started = await supervisor.StartAsync(request, timeout.Token);
        Assert.Equal(ManagedRuntimeState.Running, started.State);
        Assert.NotNull(started.ProcessId);
        Assert.NotNull(started.StartedUtc);

        await WaitForReadyLogAsync(supervisor, logStore, request, timeout.Token);
        using var client = CreateLoopbackClient();
        var responseBody = await GetEventuallyAsync(client, port, timeout.Token);
        Assert.Equal("healthy", responseBody);

        await IntegrationTestWait.UntilAsync(
            async () => (await QueryRuntimeLogsAsync(logStore, request.AppId, timeout.Token)).Count >= 2,
            TimeSpan.FromSeconds(5),
            "The Node fixture did not produce both stdout and stderr log records.",
            timeout.Token);
        var logs = await QueryRuntimeLogsAsync(logStore, request.AppId, timeout.Token);
        Assert.Contains(logs, item => item.Stream == "stdout" && item.Message.StartsWith("READY ", StringComparison.Ordinal));
        Assert.Contains(logs, item => item.Stream == "stderr" && item.Message == "fixture stderr ready");

        await supervisor.StopAsync(request.InstanceId, force: false, timeout.Token);

        Assert.True(supervisor.TryGetSnapshot(request.InstanceId, out var stopped));
        Assert.Equal(ManagedRuntimeState.Stopped, stopped.State);
        Assert.Null(stopped.ProcessId);
        Assert.Equal(0, stopped.LastExitCode);
        await AssertPortStopsRespondingAsync(client, port, timeout.Token);
    }

    [Fact]
    public async Task Unexpected_crash_is_restarted_after_backoff_and_recovers_health()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var port = IntegrationTestWait.GetAvailableLoopbackPort();
        var logStore = CreateLogStore(temporary);
        await using var supervisor = new ApplicationSupervisor(
            logStore, NullLogger<ApplicationSupervisor>.Instance, new CurrentIdentityTestProcessLauncher());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var states = new ConcurrentQueue<(ManagedProcessSnapshot Snapshot, DateTimeOffset At)>();
        supervisor.StateChanged += snapshot =>
        {
            states.Enqueue((snapshot, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        };
        var request = CreateRequest(temporary, port, "crash-once", enableAutomaticRestart: true);

        var first = await supervisor.StartAsync(request, timeout.Token);
        Assert.NotNull(first.ProcessId);
        await IntegrationTestWait.UntilAsync(
            () => states.Any(item => item.Snapshot.State == ManagedRuntimeState.Backoff),
            TimeSpan.FromSeconds(5),
            "The supervisor did not enter restart backoff after the simulated crash.",
            timeout.Token);
        var recovered = await IntegrationTestWait.UntilAsync(
            () => states
                .Where(item => item.Snapshot.State == ManagedRuntimeState.Running
                               && item.Snapshot.RestartCount == 1
                               && item.Snapshot.ProcessId != first.ProcessId)
                .Select(item => item.Snapshot)
                .LastOrDefault(),
            TimeSpan.FromSeconds(8),
            "The supervisor did not restart the fixture after its crash.",
            timeout.Token);

        Assert.Equal(1, recovered.RestartCount);
        Assert.NotEqual(first.ProcessId, recovered.ProcessId);
        var backoffAt = states.First(item => item.Snapshot.State == ManagedRuntimeState.Backoff).At;
        var recoveredAt = states.First(item => item.Snapshot.ProcessId == recovered.ProcessId
                                               && item.Snapshot.State == ManagedRuntimeState.Running).At;
        Assert.True(recoveredAt - backoffAt >= TimeSpan.FromMilliseconds(750),
            $"Expected approximately one second of restart backoff; observed {recoveredAt - backoffAt}.");

        await WaitForReadyLogAsync(supervisor, logStore, request, timeout.Token);
        using var client = CreateLoopbackClient();
        Assert.Equal("healthy", await GetEventuallyAsync(client, port, timeout.Token));
        var logs = await QueryRuntimeLogsAsync(logStore, request.AppId, timeout.Token);
        Assert.Contains(logs, item => item.Message == "fixture simulated crash");
        Assert.Contains(logs, item => item.Message.StartsWith("READY ", StringComparison.Ordinal));

        await supervisor.StopAsync(request.InstanceId, force: false, timeout.Token);
        Assert.True(supervisor.TryGetSnapshot(request.InstanceId, out var stopped));
        Assert.Equal(ManagedRuntimeState.Stopped, stopped.State);
    }

    [Fact]
    public async Task Stop_cancels_a_pending_restart_without_waiting_for_the_backoff_window()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var logStore = CreateLogStore(temporary);
        await using var supervisor = new ApplicationSupervisor(
            logStore, NullLogger<ApplicationSupervisor>.Instance, new CurrentIdentityTestProcessLauncher());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = CreateRequest(temporary, IntegrationTestWait.GetAvailableLoopbackPort(), "crash-once", enableAutomaticRestart: true)
            with
        {
            RestartSettings = new RestartPolicySettings(
                    RestartPolicy.OnFailure,
                    MaxRestarts: 3,
                    Window: TimeSpan.FromMinutes(1),
                    InitialDelay: TimeSpan.FromSeconds(30),
                    MaximumDelay: TimeSpan.FromSeconds(30),
                    StableRuntime: TimeSpan.FromMinutes(1))
        };

        await supervisor.StartAsync(request, timeout.Token);
        await IntegrationTestWait.UntilAsync(
            () => supervisor.TryGetSnapshot(request.InstanceId, out var snapshot)
                  && snapshot.State == ManagedRuntimeState.Backoff,
            TimeSpan.FromSeconds(5),
            "The fixture did not enter restart backoff.",
            timeout.Token);

        var stopwatch = Stopwatch.StartNew();
        await supervisor.StopAsync(request.InstanceId, cancellationToken: timeout.Token);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Stop waited {stopwatch.Elapsed} for the backoff delay.");
        Assert.True(supervisor.TryGetSnapshot(request.InstanceId, out var stopped));
        Assert.Equal(ManagedRuntimeState.Stopped, stopped.State);
        Assert.Null(stopped.ProcessId);
    }

    [Fact]
    public async Task Unexpected_root_exit_terminates_surviving_child_processes_in_the_job()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var port = IntegrationTestWait.GetAvailableLoopbackPort();
        var logStore = CreateLogStore(temporary);
        await using var supervisor = new ApplicationSupervisor(
            logStore, NullLogger<ApplicationSupervisor>.Instance, new CurrentIdentityTestProcessLauncher());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = CreateRequest(temporary, port, "orphan-parent", enableAutomaticRestart: false);

        await supervisor.StartAsync(request, timeout.Token);
        using var client = CreateLoopbackClient();
        Assert.Equal("healthy", await GetEventuallyAsync(client, port, timeout.Token));

        await IntegrationTestWait.UntilAsync(
            () => supervisor.TryGetSnapshot(request.InstanceId, out var snapshot)
                  && snapshot.State == ManagedRuntimeState.Exited,
            TimeSpan.FromSeconds(5),
            "The fixture root process did not exit as expected.",
            timeout.Token);
        await AssertPortStopsRespondingAsync(client, port, timeout.Token);

        await supervisor.StopAsync(request.InstanceId, cancellationToken: timeout.Token);
        Assert.True(supervisor.TryGetSnapshot(request.InstanceId, out var stopped));
        Assert.Equal(ManagedRuntimeState.Stopped, stopped.State);
    }

    [Fact]
    public async Task Multi_megabyte_unterminated_output_is_truncated_without_blocking_supervision()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        const long maximumLogFileBytes = 128 * 1024;
        var logStore = new BoundedLogStore(
            temporary.Paths,
            new LogStreamHub(),
            maxFileBytes: maximumLogFileBytes,
            retainedFiles: 2);
        await using var supervisor = new ApplicationSupervisor(
            logStore, NullLogger<ApplicationSupervisor>.Instance, new CurrentIdentityTestProcessLauncher());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = CreateLargeOutputRequest(temporary);
        var stopwatch = Stopwatch.StartNew();

        await supervisor.StartAsync(request, timeout.Token);
        await IntegrationTestWait.UntilAsync(
            async () =>
            {
                var logs = await QueryRuntimeLogsAsync(logStore, request.AppId, timeout.Token);
                return logs.Any(item => item.Stream == "stdout" && item.Message == "normal stdout €")
                       && logs.Any(item => item.Stream == "stderr" && item.Message == "normal stderr 界");
            },
            TimeSpan.FromSeconds(15),
            "The supervisor did not drain and preserve the output following oversized lines.",
            timeout.Token);
        await IntegrationTestWait.UntilAsync(
            () => supervisor.TryGetSnapshot(request.InstanceId, out var snapshot)
                  && snapshot.State == ManagedRuntimeState.Exited,
            TimeSpan.FromSeconds(5),
            "The oversized output prevented the supervisor from observing process exit.",
            timeout.Token);

        var logs = await QueryRuntimeLogsAsync(logStore, request.AppId, timeout.Token);
        var oversized = logs.Where(item => item.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Contains(oversized, item => item.Stream == "stdout");
        Assert.Contains(oversized, item => item.Stream == "stderr");
        Assert.All(oversized, item => Assert.True(item.Message.Length < 32 * 1024));
        Assert.Contains(logs, item => item.Stream == "stdout" && item.Message == "normal stdout €");
        Assert.Contains(logs, item => item.Stream == "stderr" && item.Message == "normal stderr 界");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Supervision took {stopwatch.Elapsed}.");
        Assert.All(
            Directory.EnumerateFiles(temporary.Paths.LogsRoot, "*.jsonl*", SearchOption.AllDirectories),
            path => Assert.True(new FileInfo(path).Length <= maximumLogFileBytes,
                $"Log file '{path}' exceeded its {maximumLogFileBytes}-byte quota."));
    }

    private static BoundedLogStore CreateLogStore(TemporaryAbodeToWebDirectory temporary) =>
        new(temporary.Paths, new LogStreamHub(), maxFileBytes: 1024 * 1024, retainedFiles: 2);

    private static ManagedProcessRequest CreateRequest(
        TemporaryAbodeToWebDirectory temporary,
        int port,
        string mode,
        bool enableAutomaticRestart)
    {
        var marker = temporary.GetPath("crash.marker");
        return new ManagedProcessRequest(
            InstanceId: "fixture-" + Guid.NewGuid().ToString("N"),
            AppId: "node-fixture",
            DeploymentId: "fixture-deployment",
            Command: new CommandSpec
            {
                Executable = FindNodeExecutable(),
                Arguments = [FixtureScript],
                TimeoutSeconds = 30
            },
            WorkingDirectory: temporary.Root,
            Environment: new Dictionary<string, string?>
            {
                ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ABODE_TO_WEB_FIXTURE_MODE"] = mode,
                ["ABODE_TO_WEB_CRASH_MARKER"] = marker
            },
            RestartSettings: new RestartPolicySettings(
                RestartPolicy.OnFailure,
                MaxRestarts: 3,
                Window: TimeSpan.FromSeconds(30),
                InitialDelay: TimeSpan.FromSeconds(1),
                MaximumDelay: TimeSpan.FromSeconds(1),
                StableRuntime: TimeSpan.FromMinutes(1)),
            GracefulShutdown: TimeSpan.FromSeconds(3),
            EnableAutomaticRestart: enableAutomaticRestart);
    }

    private static ManagedProcessRequest CreateLargeOutputRequest(TemporaryAbodeToWebDirectory temporary) =>
        new(
            InstanceId: "large-output-" + Guid.NewGuid().ToString("N"),
            AppId: "large-output-fixture",
            DeploymentId: "fixture-deployment",
            Command: new CommandSpec
            {
                Executable = FindNodeExecutable(),
                Arguments =
                [
                    "-e",
                    "process.stdout.write('€'.repeat(2*1024*1024));process.stdout.write('\\nnormal stdout €\\n');" +
                    "process.stderr.write('界'.repeat(2*1024*1024));process.stderr.write('\\nnormal stderr 界\\n');" +
                    "setTimeout(()=>process.exit(0),250);"
                ],
                TimeoutSeconds = 30
            },
            WorkingDirectory: temporary.Root,
            Environment: new Dictionary<string, string?>(),
            RestartSettings: new RestartPolicySettings(
                RestartPolicy.Never,
                MaxRestarts: 0,
                Window: TimeSpan.FromMinutes(1),
                InitialDelay: TimeSpan.Zero,
                MaximumDelay: TimeSpan.Zero,
                StableRuntime: TimeSpan.FromMinutes(1)),
            GracefulShutdown: TimeSpan.FromSeconds(3),
            EnableAutomaticRestart: false);

    private static string FixtureScript => Path.Combine(AppContext.BaseDirectory, "fixtures", "node-fixture.js");

    private static string FindNodeExecutable()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe", "node" } : new[] { "node" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new Xunit.Sdk.XunitException("Node.js is required for the process-supervisor integration fixture but was not found on PATH.");
    }

    private static Task<IReadOnlyList<AbodeToWebLogEvent>> QueryRuntimeLogsAsync(
        BoundedLogStore store,
        string appId,
        CancellationToken cancellationToken) =>
        store.QueryAsync(new LogQuery(AbodeToWebLogKind.Runtime, appId, Limit: 100), cancellationToken);

    private static HttpClient CreateLoopbackClient() => new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(1)
    };

    private static async Task WaitForReadyLogAsync(
        ApplicationSupervisor supervisor,
        BoundedLogStore logStore,
        ManagedProcessRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logs = await QueryRuntimeLogsAsync(logStore, request.AppId, cancellationToken);
            if (logs.Any(item => item.Message.StartsWith("READY ", StringComparison.Ordinal)))
            {
                return;
            }

            Assert.True(supervisor.TryGetSnapshot(request.InstanceId, out var snapshot));
            if (snapshot.State is ManagedRuntimeState.Exited or ManagedRuntimeState.CircuitOpen)
            {
                var messages = string.Join(" | ", logs.Select(item => $"{item.Stream}:{item.Message}"));
                Assert.Fail($"Node fixture exited before readiness. Exit={snapshot.LastExitCode}; Error={snapshot.LastError}; Logs={messages}");
            }

            await Task.Delay(25, cancellationToken);
        }

        var finalLogs = await QueryRuntimeLogsAsync(logStore, request.AppId, cancellationToken);
        supervisor.TryGetSnapshot(request.InstanceId, out var finalSnapshot);
        Assert.Fail($"Node fixture produced no readiness log. State={finalSnapshot?.State}; Exit={finalSnapshot?.LastExitCode}; Logs={string.Join(" | ", finalLogs.Select(item => $"{item.Stream}:{item.Message}"))}");
    }

    private static async Task<string> GetEventuallyAsync(HttpClient client, int port, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"), cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = exception;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"Fixture health endpoint did not become ready: {lastError?.Message ?? "no successful response"}");
    }

    private static async Task AssertPortStopsRespondingAsync(HttpClient client, int port, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"), cancellationToken);
            }
            catch (HttpRequestException)
            {
                return;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"Port {port} continued responding after a clean stop.");
    }
}
