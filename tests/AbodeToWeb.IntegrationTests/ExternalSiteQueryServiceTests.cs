using System.Net;
using System.Text.Json;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Server.Services;

namespace AbodeToWeb.IntegrationTests;

public sealed class ExternalSiteQueryServiceTests
{
    [Fact]
    public async Task External_sites_are_probed_without_reading_response_bodies()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var manifest = new AbodeToWebManifest
        {
            ManifestVersion = 1,
            AbodeToWeb = new AbodeToWebOptions { ProjectsRoots = [temporary.GetPath("projects")] },
            ExternalSites =
            [
                new ExternalSiteManifest
                {
                    Id = "status-page",
                    Name = "Status Page",
                    Provider = "Example Hosting",
                    Url = "https://status.example.com/"
                }
            ]
        };
        await File.WriteAllTextAsync(
            temporary.Paths.ConfigurationFile,
            JsonSerializer.Serialize(manifest, JsonManifestParser.CreateSerializerOptions()),
            CancellationToken.None);
        var parser = new YamlManifestParser();
        using var provider = new ManifestProvider(
            temporary.Paths,
            parser,
            value => new ManifestValidator().Validate(value));
        var loaded = await provider.LoadAsync(allowLastKnownGood: false, CancellationToken.None);
        Assert.True(loaded.Success);
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK));
        var service = new ExternalSiteQueryService(provider, client);

        var result = Assert.Single(await service.GetAsync(CancellationToken.None));

        Assert.Equal("status-page", result.Id);
        Assert.Equal("Example Hosting", result.Provider);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(AbodeToWeb.Core.Health.HealthState.Healthy, result.Health);
        Assert.Null(result.SafeStatusMessage);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.1.2", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("fc00::1", false)]
    public void External_site_probes_connect_only_to_public_addresses(string value, bool expected)
    {
        Assert.Equal(expected, ExternalSiteQueryService.IsPublicAddress(IPAddress.Parse(value)));
    }

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new ThrowingContent()
            });
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The health probe must not read the response body.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }
    }
}
