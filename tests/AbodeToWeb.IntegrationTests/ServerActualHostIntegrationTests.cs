using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AbodeToWeb.Infrastructure;
using AbodeToWeb.Infrastructure.Persistence;

namespace AbodeToWeb.IntegrationTests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class ActualHostIntegrationSuite
{
    public const string CollectionName = "AbodeToWeb actual-host integration";
}

[Collection(ActualHostIntegrationSuite.CollectionName)]
public sealed class ServerActualHostIntegrationTests
{
    [Fact]
    public async Task Dashboard_health_and_OpenAPI_are_available_only_on_the_dashboard_listener()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var dashboard = host.CreateDashboardClient();
        using var anonymousDashboard = host.CreateDashboardClient(useDefaultCredentials: false);
        using var gateway = host.CreateGatewayClient();

        using var anonymousHealth = await anonymousDashboard.GetAsync("/health", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousHealth.StatusCode);
        Assert.Contains(anonymousHealth.Headers.WwwAuthenticate,
            value => value.Scheme.Equals("Negotiate", StringComparison.OrdinalIgnoreCase));

        using var health = await dashboard.GetAsync("/health", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("healthy", (await health.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None))
            .GetProperty("status").GetString());

        using var openApi = await dashboard.GetAsync("/openapi/v1.json", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        var document = await openApi.Content.ReadFromJsonAsync<JsonDocument>(CancellationToken.None);
        Assert.NotNull(document);
        using (document)
        {
            Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/projects", out _));
            Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/external-sites", out _));
            Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/local-projects", out _));
            var createProject = document.RootElement.GetProperty("components").GetProperty("schemas")
                .GetProperty("CreateProjectRequest");
            Assert.True(createProject.GetProperty("properties").TryGetProperty("accessApplicationAudience", out var accessAudience));
            Assert.Contains(accessAudience.GetProperty("type").EnumerateArray(), value => value.GetString() == "null");
            Assert.Contains(accessAudience.GetProperty("type").EnumerateArray(), value => value.GetString() == "string");
        }

        using var gatewayDashboardProbe = await gateway.GetAsync("/openapi/v1.json", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, gatewayDashboardProbe.StatusCode);
    }

    [Fact]
    public async Task State_changing_API_rejects_missing_CSRF_and_accepts_cookie_bound_token()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var client = host.CreateDashboardClient(useCookies: true);
        var request = new { content = JsonSerializer.Serialize(host.Manifest, AbodeToWeb.Core.Configuration.JsonManifestParser.CreateSerializerOptions()) };

        using var rejected = await client.PostAsJsonAsync(
            "/api/v1/configuration/validate", request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Equal("csrf_invalid", rejectedBody.GetProperty("code").GetString());

        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        using var accepted = await client.PostAsJsonAsync(
            "/api/v1/configuration/validate", request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.True(acceptedBody.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task Dashboard_rejects_DNS_rebinding_hosts_and_cross_origin_mutations()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var client = host.CreateDashboardClient(useCookies: true);

        using (var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/v1/security/csrf"))
        {
            rebound.Headers.Host = "attacker.example";
            using var response = await client.SendAsync(rebound, CancellationToken.None);
            Assert.Equal((HttpStatusCode)421, response.StatusCode);
            Assert.Equal("dashboard_host_invalid",
                (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None)).GetProperty("code").GetString());
        }

        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/configuration/validate")
        {
            Content = JsonContent.Create(new { content = "manifestVersion: 1" })
        };
        crossOrigin.Headers.Add("X-CSRF-TOKEN", token);
        crossOrigin.Headers.Add("Origin", "https://attacker.example");
        using var rejected = await client.SendAsync(crossOrigin, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal("origin_invalid",
            (await rejected.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Secret_API_rejects_process_injection_keys_and_never_returns_values()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var client = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        using var dangerous = await client.PutAsJsonAsync(
            "/api/v1/projects/stopped-site/secrets/NODE_OPTIONS", new { value = "--require attacker.js" }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, dangerous.StatusCode);
        Assert.Equal("invalid_request",
            (await dangerous.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None)).GetProperty("code").GetString());

        const string secretValue = "abode-to-web-secret-value";
        using var stored = await client.PutAsJsonAsync(
            "/api/v1/projects/stopped-site/secrets/API_TOKEN", new { value = secretValue }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, stored.StatusCode);
        using var listed = await client.GetAsync("/api/v1/projects/stopped-site/secrets", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var body = await listed.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("API_TOKEN", body, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, body, StringComparison.Ordinal);

        var arbitraryDestination = Path.Combine(host.DataRoot, "must-not-be-written.env.example");
        using var example = await client.GetAsync(
            "/api/v1/projects/stopped-site/secrets/example?destination=" + Uri.EscapeDataString(arbitraryDestination),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, example.StatusCode);
        Assert.Equal("no-store", example.Headers.CacheControl?.ToString());
        Assert.Equal("text/plain", example.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", example.Content.Headers.ContentType?.CharSet);
        Assert.Equal("stopped-site.env.example", example.Content.Headers.ContentDisposition?.FileName);
        var exampleBody = await example.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Equal("API_TOKEN=\n", exampleBody);
        Assert.DoesNotContain(secretValue, exampleBody, StringComparison.Ordinal);
        Assert.False(File.Exists(arbitraryDestination));

        using var missing = await client.GetAsync(
            "/api/v1/projects/not-configured/secrets/example", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalid = await client.GetAsync(
            "/api/v1/projects/NOT_VALID/secrets/example", CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var missingList = await client.GetAsync(
            "/api/v1/projects/not-configured/secrets", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, missingList.StatusCode);
        using var missingWrite = await client.PutAsJsonAsync(
            "/api/v1/projects/not-configured/secrets/API_TOKEN", new { value = secretValue }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, missingWrite.StatusCode);
        using var missingDelete = await client.DeleteAsync(
            "/api/v1/projects/not-configured/secrets/API_TOKEN", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
        Assert.False(File.Exists(Path.Combine(host.DataRoot, "secrets", "not-configured.env")));
    }

    [Fact]
    public async Task External_site_inventory_is_saved_atomically_and_rejects_unsafe_urls()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var client = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        var valid = new
        {
            name = "Status Page",
            description = "Public service status",
            url = "https://status.example.com/",
            provider = "Example Hosting",
            managementUrl = "https://hosting.example.com/",
            repositoryPath = host.ProjectsRoot
        };

        using var saved = await client.PutAsJsonAsync(
            "/api/v1/external-sites/status-page", valid, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var manifest = await File.ReadAllTextAsync(host.ConfigurationFile, CancellationToken.None);
        Assert.Contains("status.example.com", manifest, StringComparison.Ordinal);
        Assert.Contains("Example Hosting", manifest, StringComparison.Ordinal);

        using var rejected = await client.PutAsJsonAsync(
            "/api/v1/external-sites/unsafe", valid with { url = "http://localhost:4545/private" }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var current = await File.ReadAllTextAsync(host.ConfigurationFile, CancellationToken.None);
        Assert.DoesNotContain("localhost:4545/private", current, StringComparison.Ordinal);

        using var nullFields = await client.PutAsJsonAsync(
            "/api/v1/external-sites/null-fields",
            new { name = (string?)null, description = (string?)null, url = (string?)null, provider = (string?)null, managementUrl = (string?)null, repositoryPath = (string?)null },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, nullFields.StatusCode);
    }

    [Fact]
    public async Task Gateway_health_is_available_without_disclosing_routes()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var gateway = host.CreateGatewayClient();

        using var response = await gateway.GetAsync(
            "/__abode-to-web/gateway/health", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Single(body.EnumerateObject());
    }

    [Fact]
    public async Task Gateway_rejects_an_unknown_hostname()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var gateway = host.CreateGatewayClient();

        using var response = await host.SendGatewayAsync(
            gateway, "unknown.abode-to-web.test", cancellationToken: CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Unknown site", body, StringComparison.Ordinal);
        Assert.DoesNotContain(host.DataRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Known_stopped_hostname_returns_polished_offline_page_without_local_details()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var gateway = host.CreateGatewayClient();

        using var response = await host.SendGatewayAsync(
            gateway, "stopped.abode-to-web.test", cancellationToken: CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Site is offline", body, StringComparison.Ordinal);
        Assert.Contains("deliberately stopped", body, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain(host.DataRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(host.StaticRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain(host.GatewayPort.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopped_hostname_can_disable_the_branded_offline_page()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var gateway = host.CreateGatewayClient();

        using var response = await host.SendGatewayAsync(
            gateway, "no-offline.abode-to-web.test", cancellationToken: CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(string.Empty, body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Persisted_unhealthy_route_returns_temporary_unavailable_page()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var gateway = host.CreateGatewayClient();

        using var response = await host.SendGatewayAsync(
            gateway, "unhealthy.abode-to-web.test", cancellationToken: CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("10", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Contains("Temporarily unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain(host.StaticRoot, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Manifest_route_reload_changes_hostname_and_preserves_stopped_state()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var gateway = host.CreateGatewayClient();

        await host.ReplaceHostnameAsync(
            "stopped-site", "reloaded.abode-to-web.test", CancellationToken.None);

        await IntegrationTestWait.UntilAsync(async () =>
        {
            using var candidate = await host.SendGatewayAsync(
                gateway, "reloaded.abode-to-web.test", cancellationToken: CancellationToken.None);
            return candidate.StatusCode == HttpStatusCode.ServiceUnavailable;
        }, TimeSpan.FromSeconds(8), "The actual AbodeToWeb gateway did not reload the changed hostname.",
            CancellationToken.None);

        using var current = await host.SendGatewayAsync(
            gateway, "reloaded.abode-to-web.test", cancellationToken: CancellationToken.None);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, current.StatusCode);
        Assert.Contains("Site is offline", await current.Content.ReadAsStringAsync(CancellationToken.None),
            StringComparison.Ordinal);

        using var previous = await host.SendGatewayAsync(
            gateway, "stopped.abode-to-web.test", cancellationToken: CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, previous.StatusCode);
    }

    [Fact]
    public async Task Hostname_change_API_writes_manifest_and_reloads_gateway_as_an_operation()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var dashboard = host.CreateDashboardClient(useCookies: true);
        using var gateway = host.CreateGatewayClient();
        var token = await ActualHostTestHost.GetCsrfTokenAsync(dashboard, CancellationToken.None);
        dashboard.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/projects/stopped-site/hostname")
        {
            Content = JsonContent.Create(new { hostname = "changed.abode-to-web.test" })
        };
        using var accepted = await dashboard.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var operation = await accepted.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        try
        {
            await WaitForOperationAsync(dashboard, operation.GetProperty("progressUrl").GetString()!, CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException(exception.Message + Environment.NewLine + host.ProcessOutput);
        }

        using var current = await host.SendGatewayAsync(gateway, "changed.abode-to-web.test", cancellationToken: CancellationToken.None);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, current.StatusCode);
        using var previous = await host.SendGatewayAsync(gateway, "stopped.abode-to-web.test", cancellationToken: CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, previous.StatusCode);
    }

    [Fact]
    public async Task Project_removal_retires_id_deletes_secrets_and_clears_mutable_route_state()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var dashboard = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(dashboard, CancellationToken.None);
        dashboard.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        using var secret = await dashboard.PutAsJsonAsync(
            "/api/v1/projects/stopped-site/secrets/API_KEY", new { value = "must-not-survive-removal" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, secret.StatusCode);
        using var accepted = await dashboard.DeleteAsync(
            "/api/v1/projects/stopped-site?removeRepository=false", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var operation = await accepted.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        await WaitForOperationAsync(dashboard, operation.GetProperty("progressUrl").GetString()!, CancellationToken.None);

        var paths = new AbodeToWebPaths
        {
            DataRoot = host.DataRoot,
            ConfigurationFile = host.ConfigurationFile,
            DatabaseFile = Path.Combine(host.DataRoot, "state", "abode-to-web.db"),
            LogsRoot = Path.Combine(host.DataRoot, "logs"),
            DeploymentsRoot = Path.Combine(host.DataRoot, "deployments"),
            SecretsRoot = Path.Combine(host.DataRoot, "secrets"),
            BackupsRoot = Path.Combine(host.DataRoot, "backups")
        };
        var database = new AbodeToWebDatabase(paths);
        Assert.True(await database.IsProjectIdRetiredAsync("stopped-site", CancellationToken.None));
        Assert.DoesNotContain(await database.GetRoutesAsync(CancellationToken.None),
            route => route.AppId.Equals("stopped-site", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(await database.GetProjectStatesAsync(CancellationToken.None),
            state => state.AppId.Equals("stopped-site", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(paths.SecretsRoot, "stopped-site.env")));
        Assert.DoesNotContain("id: stopped-site", await File.ReadAllTextAsync(host.ConfigurationFile),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quiet_mode_can_be_entered_and_left_without_restarting_projects()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var dashboard = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(dashboard, CancellationToken.None);
        dashboard.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        foreach (var enabled in new[] { true, false })
        {
            using var accepted = await dashboard.PostAsJsonAsync(
                "/api/v1/modes/quiet", new { enabled }, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var operation = await accepted.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            await WaitForOperationAsync(dashboard, operation.GetProperty("progressUrl").GetString()!, CancellationToken.None);

            using var status = await dashboard.GetAsync("/api/v1/system/status", CancellationToken.None);
            Assert.True(status.StatusCode == HttpStatusCode.OK,
                $"System status returned {(int)status.StatusCode}: {await status.Content.ReadAsStringAsync(CancellationToken.None)}{Environment.NewLine}{host.ProcessOutput}");
            var body = await status.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            Assert.Equal(enabled, body.GetProperty("quietMode").GetBoolean());
        }
    }

    [Fact]
    public async Task Persisted_static_route_serves_output_and_rejects_path_traversal()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var gateway = host.CreateGatewayClient();

        using var index = await host.SendGatewayAsync(
            gateway, "static.abode-to-web.test", cancellationToken: CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("STATIC ABODE_TO_WEB FIXTURE", await index.Content.ReadAsStringAsync(CancellationToken.None),
            StringComparison.Ordinal);
        Assert.Equal("no-cache", index.Headers.CacheControl?.ToString());

        using var asset = await host.SendGatewayAsync(
            gateway, "static.abode-to-web.test", "/app.js", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.EndsWith("javascript", asset.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);

        using var traversal = await host.SendGatewayAsync(
            gateway, "static.abode-to-web.test", "/%252e%252e/outside.txt", CancellationToken.None);
        var traversalBody = await traversal.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.True(traversal.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        Assert.DoesNotContain("TRAVERSAL MUST NOT BE SERVED", traversalBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_static_project_is_reported_as_running_and_healthy()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var dashboard = host.CreateDashboardClient();

        using var response = await dashboard.GetAsync("/api/v1/projects", CancellationToken.None);
        response.EnsureSuccessStatusCode();
        var projects = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        var project = projects.EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == "static-site");

        Assert.Equal("running", project.GetProperty("runtimeState").GetString());
        Assert.Equal("healthy", project.GetProperty("health").GetString());
    }

    [Fact]
    public async Task Missing_active_static_output_becomes_temporarily_unavailable()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var gateway = host.CreateGatewayClient();
        Directory.Delete(host.StaticRoot, recursive: true);

        await IntegrationTestWait.UntilAsync(async () =>
        {
            using var response = await host.SendGatewayAsync(
                gateway, "static.abode-to-web.test", cancellationToken: CancellationToken.None);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable) return false;
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            return body.Contains("Temporarily unavailable", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(10), "The missing static deployment was not marked unavailable.", CancellationToken.None);
    }

    [Fact]
    public async Task Empty_active_static_output_becomes_temporarily_unavailable()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var gateway = host.CreateGatewayClient();
        foreach (var file in Directory.EnumerateFiles(host.StaticRoot)) File.Delete(file);
        Directory.CreateDirectory(Path.Combine(host.StaticRoot, "empty-directory"));

        await IntegrationTestWait.UntilAsync(async () =>
        {
            using var response = await host.SendGatewayAsync(
                gateway, "static.abode-to-web.test", cancellationToken: CancellationToken.None);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable) return false;
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            return body.Contains("Temporarily unavailable", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(10), "The empty static deployment was not marked unavailable.", CancellationToken.None);
    }

    [Fact]
    public async Task Protected_project_onboarding_maps_and_validates_the_Access_application_audience()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        var repository = Path.Combine(host.ProjectsRoot, "protected-static");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "index.html"), "protected onboarding fixture");
        RunGit(repository, "init", "--initial-branch=main");
        RunGit(repository, "remote", "add", "origin", "https://github.com/example/protected-static.git");
        using var client = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        using var detectionResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/detect",
            new { localPath = repository },
            CancellationToken.None);
        detectionResponse.EnsureSuccessStatusCode();
        var detection = await detectionResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        var request = new Dictionary<string, object?>
        {
            ["detectionId"] = detection.GetProperty("detectionId").GetGuid(),
            ["id"] = "protected-static",
            ["name"] = "Protected static",
            ["description"] = null,
            ["repositoryProvider"] = "other",
            ["repositoryRemote"] = "origin",
            ["repositoryBranch"] = "main",
            ["installSuggestionId"] = null,
            ["buildSuggestionId"] = null,
            ["startSuggestionId"] = null,
            ["staticOutputDirectory"] = detection.GetProperty("suggestedStaticOutputDirectory").GetString(),
            ["portEnvironmentVariable"] = detection.GetProperty("suggestedPortEnvironmentVariable").GetString(),
            ["healthPath"] = detection.GetProperty("suggestedHealthPaths")[0].GetString(),
            ["internalPort"] = detection.GetProperty("suggestedPort").GetInt32(),
            ["hostname"] = "protected-static.abode-to-web.test",
            ["accessApplicationAudience"] = null,
            ["visibility"] = "protected",
            ["autostart"] = false,
            ["protected"] = false,
            ["runTestBuild"] = false
        };

        using var missingAudienceResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/validate", request, CancellationToken.None);
        missingAudienceResponse.EnsureSuccessStatusCode();
        var missingAudience = await missingAudienceResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.False(missingAudience.GetProperty("isValid").GetBoolean());
        Assert.Contains(missingAudience.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "routing.accessApplicationAudience.invalid");

        const string audience = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        request["accessApplicationAudience"] = audience;
        using var validResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/validate", request, CancellationToken.None);
        validResponse.EnsureSuccessStatusCode();
        var valid = await validResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.True(valid.GetProperty("isValid").GetBoolean(), valid.ToString());
        Assert.Equal(
            audience,
            valid.GetProperty("normalizedManifest").GetProperty("routing")
                .GetProperty("accessApplicationAudience").GetString());
    }

    [Fact]
    public async Task Onboarding_accepts_a_safe_custom_health_path_and_rejects_an_external_url()
    {
        await using var host = await ActualHostTestHost.StartAsync(
            experimentalProjectExecution: true,
            cancellationToken: CancellationToken.None);
        var repository = Path.Combine(host.ProjectsRoot, "custom-health-node");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "package.json"),
            """{"scripts":{"start":"node server.js"}}""");
        RunGit(repository, "init", "--initial-branch=main");
        RunGit(repository, "remote", "add", "origin", "https://github.com/example/custom-health-node.git");
        using var client = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        using var detectionResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/detect", new { localPath = repository }, CancellationToken.None);
        detectionResponse.EnsureSuccessStatusCode();
        var detection = await detectionResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        var startSuggestion = detection.GetProperty("suggestedCommands").EnumerateArray()
            .Single(item => item.GetProperty("purpose").GetString() == "start")
            .GetProperty("id").GetString();
        var request = new Dictionary<string, object?>
        {
            ["detectionId"] = detection.GetProperty("detectionId").GetGuid(),
            ["id"] = "custom-health-node",
            ["name"] = "Custom health Node",
            ["description"] = null,
            ["repositoryProvider"] = "other",
            ["repositoryRemote"] = "origin",
            ["repositoryBranch"] = "main",
            ["installSuggestionId"] = null,
            ["buildSuggestionId"] = null,
            ["startSuggestionId"] = startSuggestion,
            ["staticOutputDirectory"] = null,
            ["portEnvironmentVariable"] = detection.GetProperty("suggestedPortEnvironmentVariable").GetString(),
            ["healthPath"] = "/readyz",
            ["internalPort"] = detection.GetProperty("suggestedPort").GetInt32(),
            ["hostname"] = "custom-health.abode-to-web.test",
            ["accessApplicationAudience"] = null,
            ["visibility"] = "public",
            ["autostart"] = false,
            ["protected"] = false,
            ["runTestBuild"] = false
        };

        using var validResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/validate", request, CancellationToken.None);
        validResponse.EnsureSuccessStatusCode();
        var valid = await validResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.True(valid.GetProperty("isValid").GetBoolean(), valid.ToString());
        Assert.Equal("/readyz", valid.GetProperty("normalizedManifest").GetProperty("health").GetProperty("path").GetString());

        request["healthPath"] = "https://attacker.example/ready";
        using var invalidResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/validate", request, CancellationToken.None);
        invalidResponse.EnsureSuccessStatusCode();
        var invalid = await invalidResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.False(invalid.GetProperty("isValid").GetBoolean());
        Assert.Contains(invalid.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "health.path.invalid");
    }

    [Fact]
    public async Task Onboarding_reports_reviewable_Git_and_framework_defaults_without_exposing_remote_credentials()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        var repository = Path.Combine(host.ProjectsRoot, "cra-review");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "package.json"),
            """{"scripts":{"build":"react-scripts build"},"dependencies":{"react":"latest","react-scripts":"latest"}}""");
        RunGit(repository, "init", "--initial-branch=release");
        RunGit(repository, "remote", "add", "origin", "https://onboarding-secret@github.com/example/cra-review.git");

        using var client = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        using var detectionResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/detect", new { localPath = repository }, CancellationToken.None);
        detectionResponse.EnsureSuccessStatusCode();
        var responseText = await detectionResponse.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.DoesNotContain("onboarding-secret", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com/example", responseText, StringComparison.Ordinal);
        using var detection = JsonDocument.Parse(responseText);
        var root = detection.RootElement;
        Assert.Equal("github", root.GetProperty("suggestedRepositoryProvider").GetString());
        Assert.Equal(["origin"], root.GetProperty("gitRemotes").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("origin", root.GetProperty("suggestedRemote").GetString());
        Assert.Equal("release", root.GetProperty("suggestedBranch").GetString());
        Assert.Equal("build", root.GetProperty("suggestedStaticOutputDirectory").GetString());
        Assert.Equal("PORT", root.GetProperty("suggestedPortEnvironmentVariable").GetString());

        var vite = Path.Combine(host.ProjectsRoot, "vite-review");
        Directory.CreateDirectory(vite);
        await File.WriteAllTextAsync(Path.Combine(vite, "package.json"),
            """{"scripts":{"build":"vite build"},"devDependencies":{"vite":"latest"}}""");
        using var viteResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/detect", new { localPath = vite }, CancellationToken.None);
        viteResponse.EnsureSuccessStatusCode();
        var viteDetection = await viteResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Equal("dist", viteDetection.GetProperty("suggestedStaticOutputDirectory").GetString());

        var aspNet = Path.Combine(host.ProjectsRoot, "aspnet-review");
        Directory.CreateDirectory(aspNet);
        await File.WriteAllTextAsync(Path.Combine(aspNet, "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        using var aspNetResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/detect", new { localPath = aspNet }, CancellationToken.None);
        aspNetResponse.EnsureSuccessStatusCode();
        var aspNetDetection = await aspNetResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Equal("ASPNETCORE_HTTP_PORTS", aspNetDetection.GetProperty("suggestedPortEnvironmentVariable").GetString());

        var logText = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(host.DataRoot, "logs"), "*.jsonl", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain("onboarding-secret", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com/example", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Onboarding_rejects_guided_approval_of_a_local_path_remote()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        var repository = Path.Combine(host.ProjectsRoot, "local-remote-review");
        var localRemote = Path.Combine(host.ProjectsRoot, "local-remote-review.git");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "package.json"),
            """{"scripts":{"build":"vite build"},"devDependencies":{"vite":"latest"}}""");
        RunGit(host.ProjectsRoot, "init", "--bare", localRemote);
        RunGit(repository, "init", "--initial-branch=main");
        RunGit(repository, "remote", "add", "origin", localRemote);

        using var client = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        using var detectionResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/detect", new { localPath = repository }, CancellationToken.None);
        detectionResponse.EnsureSuccessStatusCode();
        var detection = await detectionResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Contains(detection.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()), warning =>
            warning?.Contains("local path", StringComparison.OrdinalIgnoreCase) == true);

        var request = new Dictionary<string, object?>
        {
            ["detectionId"] = detection.GetProperty("detectionId").GetGuid(),
            ["id"] = "local-remote-review",
            ["name"] = "Local remote review",
            ["description"] = null,
            ["repositoryProvider"] = "other",
            ["repositoryRemote"] = "origin",
            ["repositoryBranch"] = "main",
            ["installSuggestionId"] = null,
            ["buildSuggestionId"] = null,
            ["startSuggestionId"] = null,
            ["staticOutputDirectory"] = "dist",
            ["portEnvironmentVariable"] = detection.GetProperty("suggestedPortEnvironmentVariable").GetString(),
            ["healthPath"] = null,
            ["internalPort"] = detection.GetProperty("suggestedPort").GetInt32(),
            ["hostname"] = "local-remote-review.abode-to-web.test",
            ["accessApplicationAudience"] = null,
            ["visibility"] = "public",
            ["autostart"] = false,
            ["protected"] = false,
            ["runTestBuild"] = false
        };
        using var validationResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/validate", request, CancellationToken.None);
        validationResponse.EnsureSuccessStatusCode();
        var validation = await validationResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.False(validation.GetProperty("isValid").GetBoolean());
        Assert.Contains(validation.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "repository.local_remote_unsupported");

        request["repositoryRemote"] = "unreviewed";
        using var unreviewedResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/validate", request, CancellationToken.None);
        unreviewedResponse.EnsureSuccessStatusCode();
        var unreviewed = await unreviewedResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.False(unreviewed.GetProperty("isValid").GetBoolean());
        Assert.Contains(unreviewed.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "repository.remote_unreviewed");
    }

    [Fact]
    public async Task Onboarding_rejects_a_retired_project_id_before_writing_the_manifest()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        const string projectId = "retired-onboarding";
        var paths = new AbodeToWebPaths
        {
            DataRoot = host.DataRoot,
            ConfigurationFile = host.ConfigurationFile,
            DatabaseFile = Path.Combine(host.DataRoot, "state", "abode-to-web.db"),
            LogsRoot = Path.Combine(host.DataRoot, "logs"),
            DeploymentsRoot = Path.Combine(host.DataRoot, "deployments"),
            SecretsRoot = Path.Combine(host.DataRoot, "secrets"),
            BackupsRoot = Path.Combine(host.DataRoot, "backups")
        };
        var database = new AbodeToWebDatabase(paths);
        await database.RetireProjectAsync(projectId, CancellationToken.None);

        var repository = Path.Combine(host.ProjectsRoot, projectId);
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "index.html"), "retired ID fixture");
        RunGit(repository, "init", "--initial-branch=main");
        RunGit(repository, "remote", "add", "origin", "https://github.com/example/retired-onboarding.git");
        using var client = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        using var detectionResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/detect", new { localPath = repository }, CancellationToken.None);
        detectionResponse.EnsureSuccessStatusCode();
        var detection = await detectionResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        var request = new Dictionary<string, object?>
        {
            ["detectionId"] = detection.GetProperty("detectionId").GetGuid(),
            ["id"] = projectId,
            ["name"] = "Retired onboarding",
            ["description"] = null,
            ["repositoryProvider"] = "other",
            ["repositoryRemote"] = "origin",
            ["repositoryBranch"] = "main",
            ["installSuggestionId"] = null,
            ["buildSuggestionId"] = null,
            ["startSuggestionId"] = null,
            ["staticOutputDirectory"] = ".",
            ["portEnvironmentVariable"] = "PORT",
            ["healthPath"] = "/",
            ["internalPort"] = detection.GetProperty("suggestedPort").GetInt32(),
            ["hostname"] = "retired-onboarding.abode-to-web.test",
            ["accessApplicationAudience"] = null,
            ["visibility"] = "public",
            ["autostart"] = false,
            ["protected"] = false,
            ["runTestBuild"] = false
        };
        using var createResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/projects", request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        var accepted = await createResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        var progressUrl = accepted.GetProperty("progressUrl").GetString()!;
        JsonElement failed = default;
        await IntegrationTestWait.UntilAsync(async () =>
        {
            using var response = await client.GetAsync(progressUrl, CancellationToken.None);
            if (response.StatusCode != HttpStatusCode.OK) return false;
            var operation = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            if (operation.GetProperty("status").GetString() != "failed") return false;
            failed = operation;
            return true;
        }, TimeSpan.FromSeconds(10), "The retired-ID onboarding operation did not fail.", CancellationToken.None);
        Assert.Equal("project.id_retired", failed.GetProperty("errorCode").GetString());
        Assert.Contains("cannot be reused", failed.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(projectId, await File.ReadAllTextAsync(host.ConfigurationFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_manifest_path_and_onboarding_requests_are_rejected_safely()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        using var client = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        var invalidManifest = JsonSerializer.Serialize(host.Manifest, AbodeToWeb.Core.Configuration.JsonManifestParser.CreateSerializerOptions())
            .TrimEnd('}') + ",\"unknownTopLevelField\":true}";
        using var manifestResponse = await client.PostAsJsonAsync(
            "/api/v1/configuration/validate", new { content = invalidManifest }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        var manifestResult = await manifestResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.False(manifestResult.GetProperty("isValid").GetBoolean());
        Assert.Contains(manifestResult.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "manifest.format");

        var outsideRoot = Path.Combine(Path.GetDirectoryName(host.ProjectsRoot)!, "outside-project");
        Directory.CreateDirectory(outsideRoot);
        using var pathResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/detect", new { localPath = outsideRoot }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, pathResponse.StatusCode);
        var pathResult = await pathResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Equal("path.outside_allowed_roots", pathResult.GetProperty("code").GetString());
        Assert.DoesNotContain(outsideRoot, pathResult.ToString(), StringComparison.OrdinalIgnoreCase);

        var invalidOnboarding = new
        {
            detectionId = Guid.NewGuid(),
            id = "invalid-project",
            name = "Invalid project",
            description = (string?)null,
            installSuggestionId = (string?)null,
            buildSuggestionId = (string?)null,
            startSuggestionId = (string?)null,
            healthPath = "/health",
            internalPort = 5199,
            hostname = "invalid.abode-to-web.test",
            visibility = "public",
            autostart = false,
            @protected = false,
            runTestBuild = false
        };
        using var onboardingResponse = await client.PostAsJsonAsync(
            "/api/v1/onboarding/validate", invalidOnboarding, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, onboardingResponse.StatusCode);
        var onboardingResult = await onboardingResponse.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.False(onboardingResult.GetProperty("isValid").GetBoolean());
        Assert.Contains(onboardingResult.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "detection.expired");
    }

    private static async Task WaitForOperationAsync(HttpClient client, string progressUrl, CancellationToken cancellationToken)
    {
        await IntegrationTestWait.UntilAsync(async () =>
        {
            using var response = await client.GetAsync(progressUrl, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK) return false;
            var operation = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var status = operation.GetProperty("status").GetString();
            Assert.False(status == "failed", operation.ToString());
            return status == "succeeded";
        }, TimeSpan.FromSeconds(10), "The AbodeToWeb operation did not complete.", cancellationToken);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git.exe")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new Xunit.Sdk.XunitException("Git could not be started for the onboarding fixture.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Git fixture command failed ({process.ExitCode}).{Environment.NewLine}{stdout}{stderr}");
    }
}
