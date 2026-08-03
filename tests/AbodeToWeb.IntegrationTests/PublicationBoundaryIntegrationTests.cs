using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AbodeToWeb.Infrastructure.Deployments;

namespace AbodeToWeb.IntegrationTests;

[Collection(ActualHostIntegrationSuite.CollectionName)]
public sealed class PublicationBoundaryIntegrationTests
{
    [Fact]
    public async Task Malicious_repository_clean_filter_cannot_be_reached_through_a_service_side_publish_path()
    {
        await using var host = await ActualHostTestHost.StartAsync(CancellationToken.None);
        var markerPath = Path.Combine(host.DataRoot, "malicious-clean-filter-ran.txt");
        PrepareRepositoryWithMaliciousCleanFilter(host.ProjectsRoot, markerPath);

        using var client = host.CreateDashboardClient(useCookies: true);
        var token = await ActualHostTestHost.GetCsrfTokenAsync(client, CancellationToken.None);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        using var rejected = await client.PostAsJsonAsync(
            "/api/v1/projects/stopped-site/publish", new { }, CancellationToken.None);

        Assert.False(rejected.IsSuccessStatusCode);
        Assert.False(File.Exists(markerPath));
        Assert.Contains("changed after review", File.ReadAllText(Path.Combine(host.ProjectsRoot, "app.txt")),
            StringComparison.Ordinal);

        // Prove the repository-controlled filter is live: the same working-tree
        // mutation that the removed service path used would execute it. This
        // control runs only under the test user's identity in the disposable repo.
        RunGit(host.ProjectsRoot, "add", "--", "app.txt");
        Assert.True(File.Exists(markerPath));

        using var openApi = await client.GetAsync("/openapi/v1.json", CancellationToken.None);
        var document = await openApi.Content.ReadFromJsonAsync<JsonDocument>(CancellationToken.None);
        Assert.NotNull(document);
        using (document)
        {
            Assert.False(document.RootElement.GetProperty("paths")
                .TryGetProperty("/api/v1/projects/{appId}/publish", out _));
            var operationKinds = document.RootElement.GetProperty("components").GetProperty("schemas")
                .GetProperty("OperationKind").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString());
            Assert.DoesNotContain("publish", operationKinds);
        }
    }

    [Fact]
    public void Git_client_exposes_no_working_tree_publication_api()
    {
        var publicMethods = typeof(GitClient).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(publicMethods, method =>
            method.Name.Contains("Publish", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Push", StringComparison.OrdinalIgnoreCase));
    }

    private static void PrepareRepositoryWithMaliciousCleanFilter(string repositoryPath, string markerPath)
    {
        RunGit(repositoryPath, "init", "--initial-branch=main");
        File.WriteAllText(Path.Combine(repositoryPath, "app.txt"), "reviewed commit");
        File.WriteAllText(Path.Combine(repositoryPath, ".gitattributes"), "app.txt filter=abode-to-web-attack\n");
        RunGit(repositoryPath, "add", "--", ".gitattributes", "app.txt");
        RunGit(repositoryPath, "-c", "user.name=AbodeToWeb Tests", "-c", "user.email=tests@localhost",
            "commit", "-m", "reviewed commit");

        var filterPath = Path.Combine(repositoryPath, "malicious-clean-filter");
        var shellMarkerPath = markerPath.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);
        File.WriteAllText(filterPath, $"#!/bin/sh\nprintf 'executed' > '{shellMarkerPath}'\ncat\n");
        RunGit(repositoryPath, "config", "filter.abode-to-web-attack.clean", filterPath.Replace('\\', '/'));
        File.WriteAllText(Path.Combine(repositoryPath, "app.txt"), "changed after review");
    }

    private static void RunGit(string repositoryPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new Xunit.Sdk.XunitException("Git could not be started for the publication-boundary fixture.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"Git fixture command failed ({process.ExitCode}): {standardOutput}{Environment.NewLine}{standardError}");
    }
}
