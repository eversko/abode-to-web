using System.Net;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Infrastructure.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace AbodeToWeb.IntegrationTests;

public sealed class ApplicationHealthProbeIntegrationTests
{
    [Fact]
    public async Task Healthy_response_on_a_reused_port_is_rejected_without_managed_process_ownership()
    {
        var handler = new AlwaysHealthyHandler();
        using var client = new HttpClient(handler);
        var probe = new ApplicationHealthProbe(client);

        var result = await probe.ProbeAsync(
            new HealthCheckOptions { Type = HealthCheckType.Http, Path = "/health" },
            51_001,
            processRunning: false);

        Assert.False(result.Success);
        Assert.Contains("managed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Redirect_to_a_healthy_endpoint_does_not_pass_the_candidate_health_gate()
    {
        var port = IntegrationTestWait.GetAvailableLoopbackPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        await using var server = builder.Build();
        server.MapGet("/health", () => Results.Redirect("/healthy"));
        server.MapGet("/healthy", () => Results.Ok("healthy"));
        await server.StartAsync();

        using var client = new HttpClient(ApplicationHealthProbe.CreateHttpMessageHandler());
        var probe = new ApplicationHealthProbe(client);
        var result = await probe.ProbeAsync(
            new HealthCheckOptions { Type = HealthCheckType.Http, Path = "/health" },
            port,
            processRunning: true);

        Assert.False(result.Success);
        Assert.Equal((int)HttpStatusCode.Redirect, result.StatusCode);
        Assert.Contains("302", result.Detail, StringComparison.Ordinal);
    }

    private sealed class AlwaysHealthyHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
