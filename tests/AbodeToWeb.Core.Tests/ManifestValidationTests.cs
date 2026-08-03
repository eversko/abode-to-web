using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Security;

namespace AbodeToWeb.Core.Tests;

public sealed class ManifestValidationTests
{
    private readonly ManifestValidator _validator = new();

    [Fact]
    public void Valid_complete_manifest_passes()
    {
        var root = Path.Combine(Path.GetTempPath(), "abode-to-web-projects");
        var manifest = WithExperimentalProjectExecution(
            TestManifestFactory.Create(root, TestManifestFactory.App(repositoryPath: Path.Combine(root, "sample-app"))));

        var result = _validator.Validate(manifest);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(issue => issue.Message)));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validation_reports_multiple_actionable_errors()
    {
        var app = TestManifestFactory.App() with
        {
            Id = "Bad ID",
            Name = "",
            Autostart = true,
            Enabled = false,
            Runtime = new RuntimeOptions
            {
                InternalPort = 80,
                MaxRestarts = -1,
                RestartWindowSeconds = 1,
                GracefulShutdownSeconds = 0
            },
            Health = new HealthCheckOptions { Path = "http://outside/health", TimeoutSeconds = 20, IntervalSeconds = 10 },
            Routing = new RoutingOptions { Hostname = "*example.com", RequestTimeoutSeconds = 0 }
        };
        var manifest = TestManifestFactory.Create(apps: app) with { ManifestVersion = 99 };

        var result = _validator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "manifest.version.unsupported");
        Assert.Contains(result.Errors, issue => issue.Code == "app.id.invalid");
        Assert.Contains(result.Errors, issue => issue.Code == "runtime.port.range");
        Assert.Contains(result.Errors, issue => issue.Code == "health.path.invalid");
        Assert.Contains(result.Errors, issue => issue.Code == "routing.hostname.invalid");
    }

    [Fact]
    public void Validation_rejects_duplicate_ids_ports_and_hostnames_case_insensitively()
    {
        var root = Path.Combine(Path.GetTempPath(), "abode-to-web-projects");
        var first = TestManifestFactory.App("sample-app", "sample-app.example.com", 5101, Path.Combine(root, "one"));
        var second = TestManifestFactory.App("SAMPLE-APP", "SAMPLE-APP.EXAMPLE.COM", 5101, Path.Combine(root, "two"));

        var result = _validator.Validate(TestManifestFactory.Create(root, first, second));

        Assert.Contains(result.Errors, issue => issue.Code == "app.id.duplicate");
        Assert.Contains(result.Errors, issue => issue.Code == "runtime.port.duplicate");
        Assert.Contains(result.Errors, issue => issue.Code == "routing.hostname.duplicate");
    }

    [Fact]
    public void Validation_rejects_non_loopback_listeners_and_endpoint_collision()
    {
        var manifest = TestManifestFactory.Create() with
        {
            AbodeToWeb = new AbodeToWebOptions
            {
                ProjectsRoots = [Path.GetTempPath()],
                Dashboard = new ListenerOptions { Bind = "0.0.0.0", Port = 4545 },
                Gateway = new ListenerOptions { Bind = "0.0.0.0", Port = 4545 }
            }
        };

        var result = _validator.Validate(manifest);

        Assert.Equal(2, result.Errors.Count(issue => issue.Code == "listener.bind.loopback"));
        Assert.Contains(result.Errors, issue => issue.Code == "listener.collision");
    }

    [Fact]
    public void Validation_expands_project_root_and_restricts_repository_to_it()
    {
        var manifest = WithExperimentalProjectExecution(
            TestManifestFactory.Create("${PROJECTS_ROOT}", TestManifestFactory.App(repositoryPath: "${PROJECTS_ROOT}/sample-app")));
        var context = new ManifestValidationContext(new Dictionary<string, string>
        {
            ["PROJECTS_ROOT"] = Path.Combine(Path.GetTempPath(), "allowed")
        });

        var result = _validator.Validate(manifest, context);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(issue => issue.Message)));
    }

    [Fact]
    public void Validation_rejects_missing_environment_variable()
    {
        var manifest = TestManifestFactory.Create("${PROJECTS_ROOT}");

        var result = _validator.Validate(manifest);

        var issue = Assert.Single(result.Errors);
        Assert.Equal("environment.missing", issue.Code);
        Assert.Contains("PROJECTS_ROOT", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_requires_structured_start_for_node_and_static_output_for_static()
    {
        var root = Path.Combine(Path.GetTempPath(), "abode-to-web-projects");
        var node = TestManifestFactory.App("node", "node.example.com", 5101, Path.Combine(root, "node")) with
        {
            Deployment = new DeploymentOptions { Adapter = DeploymentAdapter.Node }
        };
        var staticApp = TestManifestFactory.App("static", "static.example.com", 5102, Path.Combine(root, "static")) with
        {
            Deployment = new DeploymentOptions { Adapter = DeploymentAdapter.Static }
        };

        var result = _validator.Validate(TestManifestFactory.Create(root, node, staticApp));

        Assert.Contains(result.Errors, issue => issue.Code == "deployment.start.required");
        Assert.Contains(result.Errors, issue => issue.Code == "deployment.staticOutput.required");
    }

    [Theory]
    [InlineData(DeploymentAdapter.Node)]
    [InlineData(DeploymentAdapter.Command)]
    [InlineData(DeploymentAdapter.DockerCompose)]
    public void Validation_rejects_all_non_static_adapters_without_explicit_execution_opt_in(DeploymentAdapter adapter)
    {
        var app = TestManifestFactory.App() with
        {
            Deployment = TestManifestFactory.App().Deployment with { Adapter = adapter }
        };

        var result = _validator.Validate(TestManifestFactory.Create(apps: app));

        Assert.Contains(result.Errors, issue => issue.Code == "deployment.execution.experimentalDisabled");
    }

    [Fact]
    public void Validation_accepts_dynamic_adapter_after_explicit_execution_opt_in()
    {
        var manifest = WithExperimentalProjectExecution(TestManifestFactory.Create(apps: TestManifestFactory.App()));

        var result = _validator.Validate(manifest);

        Assert.DoesNotContain(result.Errors, issue => issue.Code == "deployment.execution.experimentalDisabled");
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(issue => issue.Message)));
    }

    [Fact]
    public void Validation_accepts_prebuilt_static_adapter_without_execution_opt_in()
    {
        var app = TestManifestFactory.App() with
        {
            Deployment = new DeploymentOptions
            {
                Adapter = DeploymentAdapter.Static,
                StaticOutputDirectory = "dist"
            },
            Health = new HealthCheckOptions { Type = HealthCheckType.None }
        };

        var result = _validator.Validate(TestManifestFactory.Create(apps: app));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(issue => issue.Message)));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("build")]
    [InlineData("start")]
    public void Validation_rejects_each_static_project_command_without_execution_opt_in(string purpose)
    {
        var command = new AbodeToWeb.Core.Processes.CommandSpec
        {
            Executable = "sample-tool.exe",
            Arguments = [purpose]
        };
        var app = TestManifestFactory.App() with
        {
            Deployment = new DeploymentOptions
            {
                Adapter = DeploymentAdapter.Static,
                StaticOutputDirectory = "dist",
                Install = purpose == "install" ? command : null,
                Build = purpose == "build" ? command : null,
                Start = purpose == "start" ? command : null
            },
            Health = new HealthCheckOptions { Type = HealthCheckType.None }
        };

        var result = _validator.Validate(TestManifestFactory.Create(apps: app));

        Assert.Contains(result.Errors, issue => issue.Code == "deployment.execution.experimentalDisabled");
    }

    [Fact]
    public void Validation_accepts_static_install_and_build_after_explicit_execution_opt_in()
    {
        var app = TestManifestFactory.App() with
        {
            Deployment = new DeploymentOptions
            {
                Adapter = DeploymentAdapter.Static,
                StaticOutputDirectory = "dist",
                Install = new AbodeToWeb.Core.Processes.CommandSpec
                {
                    Executable = "npm.cmd",
                    Arguments = ["ci"]
                },
                Build = new AbodeToWeb.Core.Processes.CommandSpec
                {
                    Executable = "npm.cmd",
                    Arguments = ["run", "build"]
                }
            },
            Health = new HealthCheckOptions { Type = HealthCheckType.None }
        };
        var manifest = WithExperimentalProjectExecution(TestManifestFactory.Create(apps: app));

        var result = _validator.Validate(manifest);

        Assert.DoesNotContain(result.Errors, issue => issue.Code == "deployment.execution.experimentalDisabled");
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(issue => issue.Message)));
    }

    [Fact]
    public void Validation_rejects_unimplemented_local_and_repository_environment_modes()
    {
        var app = TestManifestFactory.App() with
        {
            Deployment = TestManifestFactory.App().Deployment with
            {
                Mode = DeploymentMode.Local,
                EnvironmentFile = ".env.abode-to-web"
            }
        };

        var result = _validator.Validate(TestManifestFactory.Create(apps: app));

        Assert.Contains(result.Errors, issue => issue.Code == "deployment.mode.unsupported");
        Assert.Contains(result.Errors, issue => issue.Code == "deployment.environmentFile.unsupported");
    }

    [Fact]
    public void Validation_requires_foreground_structured_Docker_Compose_start()
    {
        var app = TestManifestFactory.App() with
        {
            Deployment = TestManifestFactory.App().Deployment with
            {
                Adapter = DeploymentAdapter.DockerCompose,
                Start = new AbodeToWeb.Core.Processes.CommandSpec
                {
                    Executable = "docker.exe",
                    Arguments = ["compose", "up", "--detach"]
                }
            }
        };

        var result = _validator.Validate(TestManifestFactory.Create(apps: app));

        Assert.Contains(result.Errors, issue => issue.Code == "deployment.composeStart.detached");
    }

    [Fact]
    public void Validation_reports_null_nested_objects_instead_of_throwing()
    {
        var app = TestManifestFactory.App() with
        {
            Deployment = null!,
            Runtime = null!,
            Health = null!,
            Routing = null!,
            DemoGroups = null!
        };
        var manifest = TestManifestFactory.Create(apps: [app]) with
        {
            AbodeToWeb = TestManifestFactory.Create().AbodeToWeb with
            {
                Storage = null!,
                Cloudflare = null!
            }
        };

        var result = _validator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "storage.required");
        Assert.Contains(result.Errors, issue => issue.Code == "cloudflare.required");
        Assert.Contains(result.Errors, issue => issue.Code == "deployment.required");
        Assert.Contains(result.Errors, issue => issue.Code == "runtime.required");
        Assert.Contains(result.Errors, issue => issue.Code == "health.required");
        Assert.Contains(result.Errors, issue => issue.Code == "routing.required");
    }

    [Fact]
    public void Validation_rejects_project_port_reserved_by_gateway()
    {
        var app = TestManifestFactory.App(port: 4580);

        var result = _validator.Validate(TestManifestFactory.Create(apps: [app]));

        Assert.Contains(result.Errors, issue => issue.Code == "runtime.port.reserved");
    }

    [Fact]
    public void Protected_apps_require_valid_Cloudflare_Access_configuration()
    {
        var app = TestManifestFactory.App() with
        {
            Visibility = AppVisibility.Protected,
            Routing = TestManifestFactory.App().Routing with { AccessApplicationAudience = null }
        };

        var result = _validator.Validate(TestManifestFactory.Create(apps: app));

        Assert.Contains(result.Errors, issue => issue.Code == "cloudflare.access.required");
        Assert.Contains(result.Errors, issue => issue.Code == "routing.accessApplicationAudience.invalid");
    }

    [Fact]
    public void Protected_app_with_team_domain_and_application_audience_passes()
    {
        var app = TestManifestFactory.App() with
        {
            Visibility = AppVisibility.Protected,
            Routing = TestManifestFactory.App().Routing with
            {
                AccessApplicationAudience = new string('a', 64)
            }
        };
        var manifest = WithExperimentalProjectExecution(TestManifestFactory.Create(apps: app));
        manifest = manifest with
        {
            AbodeToWeb = manifest.AbodeToWeb with
            {
                Cloudflare = new CloudflareOptions
                {
                    Access = new CloudflareAccessOptions
                    {
                        TeamDomain = "https://abode-to-web.cloudflareaccess.com"
                    }
                }
            }
        };

        var result = _validator.Validate(manifest);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(issue => issue.Message)));
    }

    [Theory]
    [InlineData("http://abode-to-web.cloudflareaccess.com")]
    [InlineData("https://cloudflareaccess.com")]
    [InlineData("https://abode-to-web.cloudflareaccess.com.evil.invalid")]
    [InlineData("https://abode-to-web.cloudflareaccess.com/path")]
    [InlineData("https://abode-to-web.cloudflareaccess.com:444")]
    public void Validation_rejects_unsafe_Cloudflare_Access_team_domains(string teamDomain)
    {
        var manifest = TestManifestFactory.Create();
        manifest = manifest with
        {
            AbodeToWeb = manifest.AbodeToWeb with
            {
                Cloudflare = new CloudflareOptions
                {
                    Access = new CloudflareAccessOptions { TeamDomain = teamDomain }
                }
            }
        };

        var result = _validator.Validate(manifest);

        Assert.Contains(result.Errors, issue => issue.Code == "cloudflare.access.teamDomain.invalid");
    }

    [Theory]
    [InlineData("feature/ok", true)]
    [InlineData("../main", false)]
    [InlineData("refs bad", false)]
    [InlineData("main.lock", false)]
    public void Validation_checks_git_branch_names(string branch, bool expectedValid)
    {
        var root = Path.Combine(Path.GetTempPath(), "abode-to-web-projects");
        var app = TestManifestFactory.App(repositoryPath: Path.Combine(root, "sample-app")) with
        {
            Repository = new RepositoryOptions { LocalPath = Path.Combine(root, "sample-app"), Branch = branch }
        };

        var result = _validator.Validate(TestManifestFactory.Create(root, app));

        Assert.Equal(expectedValid, !result.Errors.Any(issue => issue.Code == "repository.branch.invalid"));
    }

    [Fact]
    public void Validation_rejects_an_expected_remote_url_with_embedded_credentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "abode-to-web-projects");
        var app = TestManifestFactory.App(repositoryPath: Path.Combine(root, "sample-app")) with
        {
            Repository = new RepositoryOptions
            {
                Provider = RepositoryProvider.Other,
                LocalPath = Path.Combine(root, "sample-app"),
                ExpectedRemoteUrl = "https://user:secret@github.com/example/sample-app.git"
            }
        };

        var result = _validator.Validate(TestManifestFactory.Create(root, app));

        Assert.Contains(result.Errors, issue => issue.Code == "repository.expectedRemoteUrl.invalid");
    }

    [Theory]
    [InlineData(@"\\server\share\repo.git")]
    [InlineData(@"\\?\C:\repo.git")]
    public void Validation_rejects_network_and_device_local_remote_paths(string expectedRemoteUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "abode-to-web-projects");
        var app = TestManifestFactory.App(repositoryPath: Path.Combine(root, "sample-app")) with
        {
            Repository = new RepositoryOptions
            {
                LocalPath = Path.Combine(root, "sample-app"),
                ExpectedRemoteUrl = expectedRemoteUrl
            }
        };

        var result = _validator.Validate(TestManifestFactory.Create(root, app));

        Assert.Contains(result.Errors, issue => issue.Code == "repository.expectedRemoteUrl.invalid");
    }

    [Fact]
    public void Validation_rejects_existing_local_remote_outside_configured_project_roots()
    {
        var root = Path.Combine(Path.GetTempPath(), "AbodeToWebRemoteTests", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var outside = Path.Combine(Path.GetTempPath(), "AbodeToWebRemoteOutside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(outside);
        var app = TestManifestFactory.App(repositoryPath: repository) with
        {
            Repository = new RepositoryOptions { Provider = RepositoryProvider.Other, LocalPath = repository, ExpectedRemoteUrl = outside }
        };

        var result = _validator.Validate(WithExperimentalProjectExecution(TestManifestFactory.Create(root, app)),
            new ManifestValidationContext(new Dictionary<string, string>(), true,
                AllowLocalRepositoryRemotesForTests: true));

        Assert.Contains(result.Errors, issue => issue.Code == "repository.expectedRemoteUrl.invalid");
    }

    [Fact]
    public void Validation_rejects_local_remote_in_production_even_when_it_is_inside_a_project_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "AbodeToWebRemoteTests", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(remote);
        var app = TestManifestFactory.App(repositoryPath: repository) with
        {
            Repository = new RepositoryOptions { Provider = RepositoryProvider.Other, LocalPath = repository, ExpectedRemoteUrl = remote }
        };

        var result = _validator.Validate(WithExperimentalProjectExecution(TestManifestFactory.Create(root, app)),
            new ManifestValidationContext(new Dictionary<string, string>(), true));

        Assert.Contains(result.Errors, issue => issue.Code == "repository.expectedRemoteUrl.invalid");
    }

    [Fact]
    public void Validation_rejects_reparse_point_local_remote_even_when_lexically_within_a_project_root()
    {
        var root = Path.GetFullPath(@"C:\allowed");
        var repository = Path.Combine(root, "repository");
        var remoteLink = Path.Combine(root, "remote-link");
        var inspector = new RemotePathInspector();
        inspector.AddDirectory(root);
        inspector.AddDirectory(repository);
        inspector.AddLink(remoteLink, Path.GetFullPath(@"C:\outside\remote.git"));
        var app = TestManifestFactory.App(repositoryPath: repository) with
        {
            Repository = new RepositoryOptions { Provider = RepositoryProvider.Other, LocalPath = repository, ExpectedRemoteUrl = remoteLink }
        };

        var result = _validator.Validate(WithExperimentalProjectExecution(TestManifestFactory.Create(root, app)),
            new ManifestValidationContext(new Dictionary<string, string>(), true, inspector,
                AllowLocalRepositoryRemotesForTests: true));

        Assert.Contains(result.Errors, issue => issue.Code == "repository.expectedRemoteUrl.invalid");
    }

    [Fact]
    public void External_sites_require_unique_ids_and_safe_https_urls()
    {
        var manifest = WithExperimentalProjectExecution(TestManifestFactory.Create(apps: TestManifestFactory.App())) with
        {
            ExternalSites =
            [
                new ExternalSiteManifest
                {
                    Id = "sample-app",
                    Name = "Duplicate",
                    Provider = "Other",
                    Url = "https://example.com"
                },
                new ExternalSiteManifest
                {
                    Id = "unsafe-site",
                    Name = "Unsafe",
                    Provider = "Other",
                    Url = "http://localhost:8080/private",
                    ManagementUrl = "https://user:secret@example.com/admin"
                }
            ]
        };

        Assert.Equal("sample-app", Assert.Single(manifest.Apps).Id);
        var result = _validator.Validate(manifest);

        Assert.Contains(result.Errors, issue => issue.Code == "externalSite.id.duplicate");
        Assert.Contains(result.Errors, issue => issue.Code == "externalSite.url.invalid");
        Assert.Contains(result.Errors, issue => issue.Code == "externalSite.managementUrl.invalid");
    }

    [Fact]
    public void Valid_external_site_inventory_passes()
    {
        var manifest = TestManifestFactory.Create() with
        {
            ExternalSites =
            [
                new ExternalSiteManifest
                {
                    Id = "sample-site",
                    Name = "Sample Site",
                    Description = "Externally hosted news site",
                    Provider = "Example Hosting",
                    Url = "https://sample-site.example.com/",
                    ManagementUrl = "https://hosting.example.com/",
                    RepositoryPath = "C:/Projects/sample-site"
                }
            ]
        };

        var result = _validator.Validate(manifest);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(issue => issue.Message)));
    }

    [Fact]
    public void Local_projects_require_unique_ids_valid_paths_and_hostnames()
    {
        var manifest = WithExperimentalProjectExecution(TestManifestFactory.Create(apps: TestManifestFactory.App())) with
        {
            LocalProjects =
            [
                new LocalProjectManifest
                {
                    Id = "sample-app",
                    Name = "Duplicate of the app id",
                    RepositoryPath = "C:/Projects/sample-app"
                },
                new LocalProjectManifest
                {
                    Id = "broken-project",
                    Name = "",
                    RepositoryPath = "",
                    PlannedHostname = "not a hostname"
                }
            ]
        };

        var result = _validator.Validate(manifest);

        Assert.Contains(result.Errors, issue => issue.Code == "localProject.id.duplicate");
        Assert.Contains(result.Errors, issue => issue.Code == "localProject.name.invalid");
        Assert.Contains(result.Errors, issue => issue.Code == "localProject.repositoryPath.invalid");
        Assert.Contains(result.Errors, issue => issue.Code == "localProject.plannedHostname.invalid");
    }

    [Fact]
    public void Local_project_ids_share_the_external_site_namespace()
    {
        var manifest = TestManifestFactory.Create() with
        {
            ExternalSites =
            [
                new ExternalSiteManifest { Id = "shared-id", Name = "Site", Provider = "Other", Url = "https://example.com" }
            ],
            LocalProjects =
            [
                new LocalProjectManifest { Id = "shared-id", Name = "Project", RepositoryPath = "C:/tmp/project" }
            ]
        };

        var result = _validator.Validate(manifest);

        Assert.Contains(result.Errors, issue => issue.Code == "localProject.id.duplicate");
    }

    [Fact]
    public void Valid_local_project_inventory_passes()
    {
        var manifest = TestManifestFactory.Create() with
        {
            LocalProjects =
            [
                new LocalProjectManifest
                {
                    Id = "sample-site",
                    Name = "Sample Site",
                    Description = "Interactive historical-mysteries explorer",
                    RepositoryPath = "C:/Projects/sample-site",
                    PlannedHostname = "sample-site.example.com",
                    Notes = "Needs first deploy"
                },
                new LocalProjectManifest
                {
                    Id = "sample-cli",
                    Name = "Sample CLI",
                    RepositoryPath = "C:/Projects/sample-cli"
                }
            ]
        };

        var result = _validator.Validate(manifest);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(issue => issue.Message)));
    }

    private static AbodeToWebManifest WithExperimentalProjectExecution(AbodeToWebManifest manifest) => manifest with
    {
        AbodeToWeb = manifest.AbodeToWeb with { ExperimentalProjectExecution = true }
    };

    private sealed class RemotePathInspector : IPathInspector
    {
        private readonly Dictionary<string, FileAttributes> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _targets = new(StringComparer.OrdinalIgnoreCase);

        internal void AddDirectory(string path) => _entries[Normalize(path)] = FileAttributes.Directory;

        internal void AddLink(string path, string target)
        {
            _entries[Normalize(path)] = FileAttributes.Directory | FileAttributes.ReparsePoint;
            _targets[Normalize(path)] = Normalize(target);
        }

        public bool Exists(string path) => _entries.ContainsKey(Normalize(path));

        public FileAttributes GetAttributes(string path) => _entries[Normalize(path)];

        public string? ResolveLinkTarget(string path) => _targets.GetValueOrDefault(Normalize(path));

        private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
