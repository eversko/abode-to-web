using AbodeToWeb.Core.Configuration;

namespace AbodeToWeb.Core.Tests;

public sealed class ManifestParsingTests
{
    private readonly JsonManifestParser _parser = new();

    [Fact]
    public void Parse_valid_manifest_preserves_structured_command()
    {
        var manifest = _parser.Parse(ValidJson);

        var app = Assert.Single(manifest.Apps);
        Assert.Equal("sample-app", app.Id);
        Assert.Equal(DeploymentAdapter.Node, app.Deployment.Adapter);
        Assert.Equal("npm.cmd", app.Deployment.Start!.Executable);
        Assert.Equal(["run", "start"], app.Deployment.Start.Arguments);
        Assert.Equal(RestartPolicy.OnFailure, app.Runtime.RestartPolicy);
        Assert.NotNull(manifest.AbodeToWeb.Storage);
        Assert.NotNull(manifest.AbodeToWeb.Cloudflare);
        Assert.Equal(5, manifest.AbodeToWeb.Storage.RetainedDeploymentsPerApp);
        Assert.NotNull(app.Health);
        Assert.NotNull(app.Routing);
        Assert.Equal("/health", app.Health.Path);
    }

    [Fact]
    public void Parse_rejects_unknown_fields_with_path()
    {
        var json = ValidJson.Replace("\"manifestVersion\": 1", "\"manifestVersion\": 1, \"mystery\": true", StringComparison.Ordinal);

        var exception = Assert.Throws<ManifestFormatException>(() => _parser.Parse(json));

        Assert.Contains("mystery", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mystery", exception.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_duplicate_fields()
    {
        var json = ValidJson.Replace("\"manifestVersion\": 1", "\"manifestVersion\": 1, \"manifestVersion\": 1", StringComparison.Ordinal);

        var exception = Assert.Throws<ManifestFormatException>(() => _parser.Parse(json));

        Assert.Contains("Duplicate", exception.Message, StringComparison.Ordinal);
        Assert.Equal("$.manifestVersion", exception.Path);
    }

    [Fact]
    public void Parse_is_case_sensitive()
    {
        var json = ValidJson.Replace("\"manifestVersion\"", "\"ManifestVersion\"", StringComparison.Ordinal);

        Assert.Throws<ManifestFormatException>(() => _parser.Parse(json));
    }

    [Fact]
    public void Parse_accepts_canonical_abodeToWeb_name()
    {
        Assert.NotNull(_parser.Parse(ValidJson).AbodeToWeb);
    }

    [Fact]
    public void Parse_rejects_noncanonical_product_root()
    {
        var noncanonical = ValidJson.Replace("\"abodeToWeb\"", "\"legacyControlPlane\"", StringComparison.Ordinal);

        var exception = Assert.Throws<ManifestFormatException>(() => _parser.Parse(noncanonical));

        Assert.Contains("legacyControlPlane", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_integer_enum_values()
    {
        var json = ValidJson.Replace("\"on-failure\"", "1", StringComparison.Ordinal);

        Assert.Throws<ManifestFormatException>(() => _parser.Parse(json));
    }

    [Fact]
    public void Parse_rejects_comments_and_trailing_commas()
    {
        Assert.Throws<ManifestFormatException>(() => _parser.Parse("{/* comment */ \"manifestVersion\":1}"));
        Assert.Throws<ManifestFormatException>(() => _parser.Parse("{\"manifestVersion\":1,}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Parse_rejects_missing_object(string json)
    {
        Assert.Throws<ManifestFormatException>(() => _parser.Parse(json));
    }

    [Fact]
    public void Environment_expansion_supports_braced_and_percent_syntax()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PROJECTS_ROOT"] = @"C:\Projects",
            ["APP"] = "SampleApp"
        };

        var result = EnvironmentVariableExpander.Expand("${PROJECTS_ROOT}/%APP%", values);

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\Projects/SampleApp", result.Value);
    }

    [Fact]
    public void Environment_expansion_reports_all_missing_names_without_partial_result()
    {
        var result = EnvironmentVariableExpander.Expand("${ONE}/%TWO%/${ONE}", new Dictionary<string, string>());

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(["ONE", "TWO"], result.MissingVariables);
    }

    private const string ValidJson = """
        {
          "manifestVersion": 1,
          "abodeToWeb": {
            "projectsRoots": ["C:/Projects"]
          },
          "apps": [
            {
              "id": "sample-app",
              "name": "Sample App",
              "repository": {
                "localPath": "C:/Projects/sample-app",
                "branch": "main"
              },
              "deployment": {
                "adapter": "node",
                "start": {
                  "executable": "npm.cmd",
                  "arguments": ["run", "start"],
                  "timeoutSeconds": 120
                }
              },
              "runtime": {
                "internalPort": 5101,
                "restartPolicy": "on-failure"
              },
              "routing": {
                "hostname": "sample-app.example.com"
              }
            }
          ]
        }
        """;
}
