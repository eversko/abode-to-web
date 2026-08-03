using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Processes;

namespace AbodeToWeb.Core.Tests;

internal static class TestManifestFactory
{
    internal static AbodeToWebManifest Create(string? root = null, params AppManifest[] apps) => new()
    {
        ManifestVersion = 1,
        AbodeToWeb = new AbodeToWebOptions
        {
            ProjectsRoots = [root ?? Path.Combine(Path.GetTempPath(), "abode-to-web-projects")]
        },
        Apps = apps
    };

    internal static AppManifest App(
        string id = "sample-app",
        string hostname = "sample-app.example.com",
        int? port = 5101,
        string? repositoryPath = null) => new()
        {
            Id = id,
            Name = id.ToUpperInvariant(),
            Repository = new RepositoryOptions
            {
                LocalPath = repositoryPath ?? Path.Combine(Path.GetTempPath(), "abode-to-web-projects", id),
                ExpectedRemoteUrl = "https://github.com/example/sample-app.git"
            },
            Deployment = new DeploymentOptions
            {
                Adapter = DeploymentAdapter.Node,
                Start = new CommandSpec { Executable = "node.exe", Arguments = ["server.js"] }
            },
            Runtime = new RuntimeOptions { InternalPort = port },
            Routing = new RoutingOptions { Hostname = hostname }
        };
}
