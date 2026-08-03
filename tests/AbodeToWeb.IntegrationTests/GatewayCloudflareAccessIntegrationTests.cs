using System.Text.Json;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Server.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Forwarder;

namespace AbodeToWeb.IntegrationTests;

public sealed class GatewayCloudflareAccessIntegrationTests
{
    private const string Audience = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Protected_routes_fail_closed_validate_the_assertion_and_preserve_public_routes()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var staticRoot = temporary.GetPath("static");
        Directory.CreateDirectory(staticRoot);
        await File.WriteAllTextAsync(Path.Combine(staticRoot, "index.html"), "protected fixture");
        var manifest = CreateManifest(temporary.GetPath("projects"), staticRoot);
        await File.WriteAllTextAsync(
            temporary.Paths.ConfigurationFile,
            JsonSerializer.Serialize(manifest, JsonManifestParser.CreateSerializerOptions()));

        var parser = new YamlManifestParser();
        var validator = new ManifestValidator();
        using var provider = new ManifestProvider(
            temporary.Paths,
            parser,
            item => validator.Validate(item));
        Assert.True((await provider.LoadAsync()).Success);

        var database = new AbodeToWebDatabase(temporary.Paths);
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await database.UpsertRouteAsync(new RouteStateRecord(
            "protected", "protected.abode-to-web.test", "protected-deployment", null,
            "healthy", staticRoot, now));
        await database.UpsertRouteAsync(new RouteStateRecord(
            "public", "public.abode-to-web.test", "public-deployment", null,
            "healthy", staticRoot, now));
        var routes = new GatewayRouteRegistry(database);
        await routes.InitializeAsync(manifest);

        var accessValidator = new RecordingAccessValidator
        {
            Result = CloudflareAccessValidationResult.Failed(CloudflareAccessValidationFailure.SignatureInvalid)
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpForwarder();
        await using var serviceProvider = services.BuildServiceProvider();
        using var forwarderClient = new GatewayForwarderClient();
        var middleware = new GatewayMiddleware(
            serviceProvider.GetRequiredService<IHttpForwarder>(),
            routes,
            forwarderClient,
            accessValidator,
            provider,
            NullLogger<GatewayMiddleware>.Instance);

        var missing = CreateContext("protected.abode-to-web.test");
        await middleware.HandleAsync(missing);
        Assert.Equal(StatusCodes.Status403Forbidden, missing.Response.StatusCode);
        Assert.Equal(0, accessValidator.Calls);

        var rejected = CreateContext("protected.abode-to-web.test", "invalid-token");
        await middleware.HandleAsync(rejected);
        Assert.Equal(StatusCodes.Status403Forbidden, rejected.Response.StatusCode);
        Assert.Equal(1, accessValidator.Calls);
        Assert.Equal("https://abode-to-web-tests.cloudflareaccess.com", accessValidator.TeamDomain);
        Assert.Equal(Audience, accessValidator.Audience);

        accessValidator.Result = CloudflareAccessValidationResult.Success("verified@example.com");
        var accepted = CreateContext("protected.abode-to-web.test", "valid-token");
        accepted.Request.Headers["Cf-Access-Authenticated-User-Email"] = "spoofed@example.invalid";
        accepted.Request.Headers["Cf-Access-Authenticated-User-Groups"] = "spoofed-group";
        accepted.Request.Headers["Cf-Access-Client-Id"] = "spoofed-client";
        await middleware.HandleAsync(accepted);
        Assert.Equal(StatusCodes.Status200OK, accepted.Response.StatusCode);
        Assert.Equal("protected fixture", System.Text.Encoding.UTF8.GetString(((MemoryStream)accepted.Response.Body).ToArray()));
        Assert.Equal("verified@example.com", accepted.Request.Headers["Cf-Access-Authenticated-User-Email"]);
        Assert.False(accepted.Request.Headers.ContainsKey("Cf-Access-Jwt-Assertion"));
        Assert.False(accepted.Request.Headers.ContainsKey("Cf-Access-Authenticated-User-Groups"));
        Assert.False(accepted.Request.Headers.ContainsKey("Cf-Access-Client-Id"));

        var publicRequest = CreateContext("public.abode-to-web.test");
        publicRequest.Request.Headers["Cf-Access-Authenticated-User-Email"] = "spoofed@example.invalid";
        publicRequest.Request.Headers["Cf-Access-Jwt-Assertion"] = "spoofed-token";
        publicRequest.Request.Headers["Cf-Access-Authenticated-User-Groups"] = "spoofed-group";
        await middleware.HandleAsync(publicRequest);
        Assert.Equal(StatusCodes.Status200OK, publicRequest.Response.StatusCode);
        Assert.Equal(2, accessValidator.Calls);
        Assert.DoesNotContain(
            publicRequest.Request.Headers.Keys,
            key => key.StartsWith("Cf-Access-", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https", "http", "https")]
    [InlineData("http", "https", "http")]
    public async Task Forwarded_scheme_comes_from_gateway_policy_not_the_caller(
        string configuredScheme,
        string callerScheme,
        string expectedScheme)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("app.abode-to-web.test");
        context.Request.Headers["X-Forwarded-Proto"] = callerScheme;
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5000/");

        await new AbodeToWebTransformer(configuredScheme).TransformRequestAsync(
            context,
            request,
            "http://127.0.0.1:5000",
            CancellationToken.None);

        Assert.Equal(expectedScheme, Assert.Single(request.Headers.GetValues("X-Forwarded-Proto")));
    }

    private static DefaultHttpContext CreateContext(string hostname, string? assertion = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(hostname);
        context.Request.Path = "/";
        if (assertion is not null)
        {
            context.Request.Headers["Cf-Access-Jwt-Assertion"] = assertion;
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static AbodeToWebManifest CreateManifest(string projectsRoot, string staticRoot) => new()
    {
        ManifestVersion = 1,
        AbodeToWeb = new AbodeToWebOptions
        {
            ProjectsRoots = [projectsRoot],
            Cloudflare = new CloudflareOptions
            {
                Access = new CloudflareAccessOptions
                {
                    TeamDomain = "https://abode-to-web-tests.cloudflareaccess.com"
                }
            }
        },
        Apps =
        [
            CreateStaticApp("protected", "protected.abode-to-web.test", AppVisibility.Protected, Audience, projectsRoot),
            CreateStaticApp("public", "public.abode-to-web.test", AppVisibility.Public, null, projectsRoot)
        ]
    };

    private static AppManifest CreateStaticApp(
        string id,
        string hostname,
        AppVisibility visibility,
        string? audience,
        string repositoryPath) => new()
        {
            Id = id,
            Name = id,
            Repository = new RepositoryOptions
            {
                LocalPath = repositoryPath,
                ExpectedRemoteUrl = $"https://github.com/example/{id}.git"
            },
            Deployment = new DeploymentOptions
            {
                Adapter = DeploymentAdapter.Static,
                Mode = DeploymentMode.Worktree,
                WorkingDirectory = ".",
                StaticOutputDirectory = "."
            },
            Runtime = new RuntimeOptions(),
            Health = new HealthCheckOptions { Type = HealthCheckType.None },
            Routing = new RoutingOptions
            {
                Hostname = hostname,
                AccessApplicationAudience = audience
            },
            Visibility = visibility
        };

    private sealed class RecordingAccessValidator : ICloudflareAccessTokenValidator
    {
        internal CloudflareAccessValidationResult Result { get; set; }

        internal int Calls { get; private set; }

        internal string? TeamDomain { get; private set; }

        internal string? Audience { get; private set; }

        public ValueTask<CloudflareAccessValidationResult> ValidateAsync(
            string assertion,
            string teamDomain,
            string applicationAudience,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            TeamDomain = teamDomain;
            Audience = applicationAudience;
            return ValueTask.FromResult(Result);
        }
    }
}
