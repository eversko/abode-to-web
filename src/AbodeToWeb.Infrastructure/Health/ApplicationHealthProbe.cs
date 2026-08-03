using System.Net;
using System.Net.Sockets;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Health;

namespace AbodeToWeb.Infrastructure.Health;

public sealed record ProbeResult(bool Success, string Detail, TimeSpan Duration, int? StatusCode = null);

public sealed class ApplicationHealthProbe(HttpClient httpClient)
{
    public static SocketsHttpHandler CreateHttpMessageHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false
    };

    public async Task<ProbeResult> ProbeAsync(
        HealthCheckOptions options,
        int? port,
        bool processRunning,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!processRunning)
                return new ProbeResult(false, "The managed application process is not running.", started.Elapsed);
            return options.Type switch
            {
                HealthCheckType.None => new ProbeResult(true, "Health check is disabled.", started.Elapsed),
                HealthCheckType.Process => new ProbeResult(processRunning, processRunning ? "Process is running." : "Process is not running.", started.Elapsed),
                HealthCheckType.Http => await ProbeHttpAsync(options, RequirePort(port), started, cancellationToken),
                HealthCheckType.Tcp => await ProbeTcpAsync(options, RequirePort(port), started, cancellationToken),
                _ => new ProbeResult(false, "Unsupported health check type.", started.Elapsed)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(false, "Health check timed out.", started.Elapsed);
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException)
        {
            return new ProbeResult(false, "Application did not answer its health check.", started.Elapsed);
        }
    }

    public async Task<(bool Healthy, HealthSnapshot Snapshot, ProbeResult LastProbe)> WaitForHealthyAsync(
        HealthCheckOptions options,
        int? port,
        Func<bool> processRunning,
        TimeSpan maximumWait,
        CancellationToken cancellationToken = default)
    {
        var snapshot = HealthStateMachine.MarkStarted(HealthSnapshot.Unknown, DateTimeOffset.UtcNow);
        var last = new ProbeResult(false, "Waiting for candidate.", TimeSpan.Zero);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maximumWait);
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var running = processRunning();
                if (!running)
                    return (false, snapshot, last with { Detail = "Candidate process exited before becoming healthy." });
                last = await ProbeAsync(options, port, running, timeout.Token);
                snapshot = HealthStateMachine.Apply(snapshot,
                    last.Success ? HealthObservation.Succeeded(last.Detail) : HealthObservation.Failed(last.Detail),
                    new HealthThresholds(options.HealthyThreshold, options.UnhealthyThreshold), DateTimeOffset.UtcNow);
                if (snapshot.State == HealthState.Healthy && processRunning()) return (true, snapshot, last);
                if (!processRunning()) return (false, snapshot, last with { Detail = "Candidate process exited before becoming healthy." });
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1_000, options.IntervalSeconds * 1_000)), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return (false, snapshot, last with { Detail = "Candidate did not become healthy before the startup timeout." });
    }

    private async Task<ProbeResult> ProbeHttpAsync(HealthCheckOptions options, int port, System.Diagnostics.Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var uri = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), port, options.Path).Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("AbodeToWeb-Health/1.0");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var success = response.IsSuccessStatusCode;
        return new ProbeResult(success, success ? "HTTP health check succeeded." : $"HTTP health check returned {(int)response.StatusCode}.", stopwatch.Elapsed, (int)response.StatusCode);
    }

    private static async Task<ProbeResult> ProbeTcpAsync(HealthCheckOptions options, int port, System.Diagnostics.Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        return new ProbeResult(true, "TCP health check connected.", stopwatch.Elapsed);
    }

    private static int RequirePort(int? port) => port is > 0 and <= 65_535 ? port.Value : throw new InvalidOperationException("Health check requires an assigned internal port.");
}
