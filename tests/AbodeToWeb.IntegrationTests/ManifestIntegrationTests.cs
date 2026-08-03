using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Server.Services;

namespace AbodeToWeb.IntegrationTests;

public sealed class ManifestIntegrationTests
{
    private readonly YamlManifestParser _parser = new();
    private readonly ManifestValidator _validator = new();

    [Fact]
    public void Yaml_parser_accepts_structured_commands_and_strictly_rejects_unknown_properties()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var manifest = _parser.Parse(ValidManifest(temporary.Root));

        Assert.Equal(1, manifest.ManifestVersion);
        var app = Assert.Single(manifest.Apps);
        Assert.Equal("node", app.Deployment.Start!.Executable);
        Assert.Equal(["fixtures/node-fixture.js", "argument with spaces"], app.Deployment.Start.Arguments);
        Assert.Equal(5, manifest.AbodeToWeb.Storage.RetainedDeploymentsPerApp);
        Assert.False(manifest.AbodeToWeb.Cloudflare.Enabled);
        Assert.Equal(
            temporary.Root,
            Assert.Single(manifest.AbodeToWeb.ProjectsRoots).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var invalid = ValidManifest(temporary.Root).Replace(
            "  startupStaggerSeconds: 0",
            "  startupStaggerSeconds: 0\n  inventedSetting: true",
            StringComparison.Ordinal);
        var exception = Assert.Throws<ManifestFormatException>(() => _parser.Parse(invalid));
        Assert.Contains("inventedSetting", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_manifest_requires_explicit_opt_in_for_project_execution()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        var optedIn = _parser.Parse(ValidManifest(temporary.Root));
        var optedInValidation = _validator.Validate(optedIn);

        var defaultOff = _parser.Parse(ValidManifest(temporary.Root).Replace(
            "  experimentalProjectExecution: true",
            "  experimentalProjectExecution: false",
            StringComparison.Ordinal));
        var defaultOffValidation = _validator.Validate(defaultOff);

        Assert.True(optedInValidation.IsValid, string.Join(Environment.NewLine, optedInValidation.Errors.Select(issue => issue.Message)));
        Assert.Contains(defaultOffValidation.Errors, issue => issue.Code == "deployment.execution.experimentalDisabled");
    }

    [Fact]
    public async Task Provider_rejects_a_semantically_invalid_manifest_without_replacing_current_state()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        await File.WriteAllTextAsync(temporary.Paths.ConfigurationFile, ValidManifest(temporary.Root));
        using var provider = CreateProvider(temporary);
        var first = await provider.LoadAsync();
        Assert.True(first.Success, FormatErrors(first));

        var invalid = ValidManifest(temporary.Root).Replace("port: 4580", "port: 4545", StringComparison.Ordinal);
        await File.WriteAllTextAsync(temporary.Paths.ConfigurationFile, invalid);
        var validation = provider.ValidateText(invalid);
        var rejected = await provider.LoadAsync(allowLastKnownGood: false);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, issue => issue.Code == "listener.collision");
        Assert.False(rejected.Success);
        Assert.False(rejected.UsedLastKnownGood);
        Assert.Equal("listener.collision", Assert.Single(rejected.Errors).Code);
        Assert.Equal("sample-app", Assert.Single(provider.Current.Apps).Id);
    }

    [Fact]
    public async Task Provider_uses_last_known_good_after_primary_manifest_becomes_invalid()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        await File.WriteAllTextAsync(temporary.Paths.ConfigurationFile, ValidManifest(temporary.Root));
        using var provider = CreateProvider(temporary);

        var initial = await provider.LoadAsync();
        Assert.True(initial.Success, FormatErrors(initial));
        Assert.False(initial.UsedLastKnownGood);
        Assert.True(File.Exists(temporary.Paths.ConfigurationFile + ".last-good"));

        await File.WriteAllTextAsync(temporary.Paths.ConfigurationFile, "manifestVersion: 1\nunknownRoot: true\n");
        var fallback = await provider.LoadAsync(allowLastKnownGood: true);

        Assert.False(fallback.Success);
        Assert.True(fallback.UsedLastKnownGood);
        Assert.NotNull(fallback.Manifest);
        Assert.Equal("sample-app", Assert.Single(fallback.Manifest.Apps).Id);
        Assert.Equal("manifest.format", Assert.Single(fallback.Errors).Code);
        Assert.Same(fallback.Manifest, provider.Current);
    }

    [Fact]
    public async Task Provider_serializes_updates_atomically_and_does_not_lose_concurrent_changes()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        await File.WriteAllTextAsync(temporary.Paths.ConfigurationFile, ValidManifest(temporary.Root));
        using var provider = CreateProvider(temporary);
        Assert.True((await provider.LoadAsync()).Success);

        await Task.WhenAll(
            provider.UpdateAtomicAsync(current => current with
            {
                AbodeToWeb = current.AbodeToWeb with { StartupStaggerSeconds = 7 }
            }),
            provider.UpdateAtomicAsync(current => current with
            {
                Apps = current.Apps.Select(app => app with { Description = "Updated safely" }).ToArray()
            }));

        var reloaded = await provider.LoadAsync(allowLastKnownGood: false);
        Assert.True(reloaded.Success, FormatErrors(reloaded));
        Assert.Equal(7, reloaded.Manifest!.AbodeToWeb.StartupStaggerSeconds);
        Assert.Equal("Updated safely", Assert.Single(reloaded.Manifest.Apps).Description);
        var persisted = await File.ReadAllTextAsync(temporary.Paths.ConfigurationFile);
        Assert.Contains("abodeToWeb:", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("abode-to-web:", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("tunnelName:", persisted, StringComparison.Ordinal);
        Assert.Equal(persisted, await File.ReadAllTextAsync(temporary.Paths.ConfigurationFile + ".last-good"));
        Assert.Empty(Directory.EnumerateFiles(temporary.Paths.ConfigurationDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Provider_preserves_environment_templates_when_later_updates_are_serialized()
    {
        const string variable = "ABODE_TO_WEB_TEMPLATE_PRESERVATION_TEST";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            using var temporary = new TemporaryAbodeToWebDirectory();
            var sentinel = temporary.Root.Replace('\\', '/');
            Environment.SetEnvironmentVariable(variable, sentinel);
            var template = "${" + variable + "}";
            var manifest = ValidManifest(temporary.Root).Replace(
                $"{sentinel}/sample-app",
                $"{template}/sample-app",
                StringComparison.Ordinal);
            manifest = manifest.Replace(
                "name: Sample App",
                $"name: Sample App\n    description: \"{template}\"",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(temporary.Paths.ConfigurationFile, manifest);
            using var provider = CreateProvider(temporary);

            var loaded = await provider.LoadAsync();

            Assert.True(loaded.Success, FormatErrors(loaded));
            var app = Assert.Single(provider.Current.Apps);
            Assert.Equal($"{sentinel}/sample-app", app.Repository!.LocalPath.Replace('\\', '/'));
            Assert.Equal(template, app.Description);
            await provider.UpdateAtomicAsync(current => current with
            {
                AbodeToWeb = current.AbodeToWeb with { StartupStaggerSeconds = 7 }
            });

            var persisted = await File.ReadAllTextAsync(temporary.Paths.ConfigurationFile);
            var lastKnownGood = await File.ReadAllTextAsync(temporary.Paths.ConfigurationFile + ".last-good");
            Assert.Contains(template, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain($"{sentinel}/sample-app", persisted, StringComparison.Ordinal);
            Assert.Equal(persisted, lastKnownGood);
            Assert.Equal(template, Assert.Single(provider.Current.Apps).Description);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Metadata_updates_validate_existing_runtime_path_templates_after_expansion()
    {
        const string variable = "ABODE_TO_WEB_METADATA_TEMPLATE_TEST";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            using var temporary = new TemporaryAbodeToWebDirectory();
            var sentinel = temporary.Root.Replace('\\', '/');
            Environment.SetEnvironmentVariable(variable, sentinel);
            var template = "${" + variable + "}";
            var persisted = ValidManifest(temporary.Root)
                .Replace($"- \"{sentinel}\"", $"- \"{template}\"", StringComparison.Ordinal)
                .Replace($"{sentinel}/sample-app", $"{template}/sample-app", StringComparison.Ordinal);
            await File.WriteAllTextAsync(temporary.Paths.ConfigurationFile, persisted);
            using var provider = CreateProvider(temporary);

            var loaded = await provider.LoadAsync();

            Assert.True(loaded.Success, FormatErrors(loaded));
            Assert.Equal(sentinel, Assert.Single(provider.Current.AbodeToWeb.ProjectsRoots).Replace('\\', '/'));
            Assert.Equal($"{sentinel}/sample-app", Assert.Single(provider.Current.Apps).Repository!.LocalPath.Replace('\\', '/'));

            var externalSites = new ExternalSiteConfigurationService(provider);
            await externalSites.SaveAsync("docs", new SaveExternalSiteRequest(
                "Documentation", "Public documentation", "https://example.com/docs", "GitHub Pages", null, null));
            var localProjects = new LocalProjectConfigurationService(provider);
            await localProjects.SaveAsync("backlog", new SaveLocalProjectRequest(
                "Backlog", "Future work", "C:/Projects/backlog", "backlog.example.com", null));

            var saved = await File.ReadAllTextAsync(temporary.Paths.ConfigurationFile);
            Assert.Contains(template, saved, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, saved, StringComparison.Ordinal);
            Assert.Single(provider.Current.ExternalSites);
            Assert.Single(provider.Current.LocalProjects);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Provider_allows_retirement_removal_but_permanently_rejects_id_reuse()
    {
        using var temporary = new TemporaryAbodeToWebDirectory();
        await File.WriteAllTextAsync(temporary.Paths.ConfigurationFile, ValidManifest(temporary.Root));
        using var provider = CreateProvider(temporary);
        Assert.True((await provider.LoadAsync()).Success);
        var retiredApp = Assert.Single(provider.Current.Apps);

        provider.RetireProjectId(retiredApp.Id);
        var rejectedCurrent = provider.ValidateText(ValidManifest(temporary.Root));
        Assert.Contains(rejectedCurrent.Errors, issue => issue.Code == "project.id.retired");

        await provider.UpdateAtomicAsync(current => current with { Apps = [] });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.UpdateAtomicAsync(current => current with { Apps = [retiredApp] }));
        Assert.Contains("permanently retired", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(provider.Current.Apps);
    }

    private ManifestProvider CreateProvider(TemporaryAbodeToWebDirectory temporary) =>
        new(temporary.Paths, _parser, manifest => _validator.Validate(manifest));

    private static string FormatErrors(ManifestLoadResult result) =>
        string.Join(Environment.NewLine, result.Errors.Select(issue => $"{issue.Code} at {issue.Path}: {issue.Message}"));

    private static string ValidManifest(string projectRoot)
    {
        var yamlPath = projectRoot.Replace('\\', '/');
        return $$"""
            manifestVersion: 1
            abodeToWeb:
              dashboard:
                bind: 127.0.0.1
                port: 4545
              gateway:
                bind: 127.0.0.1
                port: 4580
              projectsRoots:
                - "{{yamlPath}}"
              experimentalProjectExecution: true
              startupStaggerSeconds: 0
            apps:
              - id: sample-app
                name: Sample App
                enabled: true
                repository:
                  provider: github
                  localPath: "{{yamlPath}}/sample-app"
                  remote: origin
                  expectedRemoteUrl: https://github.com/example/sample-app.git
                  branch: main
                deployment:
                  adapter: node
                  mode: worktree
                  start:
                    executable: node
                    arguments:
                      - fixtures/node-fixture.js
                      - "argument with spaces"
                    timeoutSeconds: 30
                runtime:
                  internalPort: 5101
                  restartPolicy: on-failure
                health:
                  type: http
                  path: /health
                  intervalSeconds: 5
                  timeoutSeconds: 1
                routing:
                  hostname: sample-app.example.com
                visibility: public
            """;
    }
}
