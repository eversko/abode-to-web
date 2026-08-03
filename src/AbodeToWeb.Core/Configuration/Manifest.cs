using System.Text.Json.Serialization;
using AbodeToWeb.Core.Processes;

namespace AbodeToWeb.Core.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AbodeToWebManifest
{
    public required int ManifestVersion { get; init; }

    [JsonPropertyName("abodeToWeb")]
    public required AbodeToWebOptions AbodeToWeb { get; init; }

    public IReadOnlyList<AppManifest> Apps { get; init; } = [];

    public IReadOnlyList<ExternalSiteManifest> ExternalSites { get; init; } = [];

    public IReadOnlyList<LocalProjectManifest> LocalProjects { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalProjectManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string RepositoryPath { get; init; }

    /// <summary>When set, the project renders as a planned deployment targeting this hostname.</summary>
    public string? PlannedHostname { get; init; }

    public string? Notes { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalSiteManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Url { get; init; }

    public required string Provider { get; init; }

    public string? ManagementUrl { get; init; }

    public string? RepositoryPath { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AbodeToWebOptions
{
    public ListenerOptions Dashboard { get; init; } = new() { Bind = "127.0.0.1", Port = 4545 };

    public ListenerOptions Gateway { get; init; } = new() { Bind = "127.0.0.1", Port = 4580 };

    public required IReadOnlyList<string> ProjectsRoots { get; init; }

    /// <summary>
    /// Explicit preview opt-in for executing project-controlled install, build,
    /// or start commands. Prebuilt static deployments remain available when false.
    /// </summary>
    public bool ExperimentalProjectExecution { get; init; }

    public CloudflareOptions Cloudflare { get; init; } = new();

    public StorageOptions Storage { get; init; } = new();

    public int StartupStaggerSeconds { get; init; } = 2;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListenerOptions
{
    public required string Bind { get; init; }

    public required int Port { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloudflareOptions
{
    public bool Enabled { get; init; }

    public string? TunnelName { get; init; }

    public CloudflareAccessOptions? Access { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloudflareAccessOptions
{
    /// <summary>The absolute Cloudflare Access team URL, for example https://example.cloudflareaccess.com.</summary>
    public required string TeamDomain { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StorageOptions
{
    public int RetainedDeploymentsPerApp { get; init; } = 5;

    public int LogRetentionDays { get; init; } = 14;

    public long MaxLogBytesPerApp { get; init; } = 268_435_456;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public bool Autostart { get; init; }

    public bool Protected { get; init; }

    public IReadOnlyList<string> DemoGroups { get; init; } = [];

    public RepositoryOptions? Repository { get; init; }

    public required DeploymentOptions Deployment { get; init; }

    public RuntimeOptions Runtime { get; init; } = new();

    public HealthCheckOptions Health { get; init; } = new();

    public RoutingOptions Routing { get; init; } = new();

    public AppVisibility Visibility { get; init; } = AppVisibility.Public;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepositoryOptions
{
    public RepositoryProvider Provider { get; init; } = RepositoryProvider.GitHub;

    public required string LocalPath { get; init; }

    public string Remote { get; init; } = "origin";

    /// <summary>
    /// The reviewed remote target that the repository-local remote name must
    /// resolve to before Abode to Web fetches a deployment commit.
    /// </summary>
    public string? ExpectedRemoteUrl { get; init; }

    public string Branch { get; init; } = "main";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeploymentOptions
{
    public DeploymentAdapter Adapter { get; init; } = DeploymentAdapter.Command;

    public DeploymentMode Mode { get; init; } = DeploymentMode.Worktree;

    public CommandSpec? Install { get; init; }

    public CommandSpec? Build { get; init; }

    public CommandSpec? Start { get; init; }

    public string WorkingDirectory { get; init; } = ".";

    public string? EnvironmentFile { get; init; }

    public string? StaticOutputDirectory { get; init; }

    public string PortEnvironmentVariable { get; init; } = "PORT";

    public int CandidateStartupTimeoutSeconds { get; init; } = 120;

    public int DrainSeconds { get; init; } = 5;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeOptions
{
    public int? InternalPort { get; init; }

    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.OnFailure;

    public int MaxRestarts { get; init; } = 5;

    public int RestartWindowSeconds { get; init; } = 300;

    public int InitialBackoffSeconds { get; init; } = 1;

    public int MaxBackoffSeconds { get; init; } = 60;

    public int StableRuntimeSeconds { get; init; } = 300;

    public int GracefulShutdownSeconds { get; init; } = 15;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HealthCheckOptions
{
    public HealthCheckType Type { get; init; } = HealthCheckType.Http;

    public string Path { get; init; } = "/health";

    public int IntervalSeconds { get; init; } = 20;

    public int TimeoutSeconds { get; init; } = 5;

    public int HealthyThreshold { get; init; } = 2;

    public int UnhealthyThreshold { get; init; } = 3;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoutingOptions
{
    public string? Hostname { get; init; }

    /// <summary>The Cloudflare Access Application Audience (AUD) tag for this protected application.</summary>
    public string? AccessApplicationAudience { get; init; }

    public bool OfflinePage { get; init; } = true;

    public int RequestTimeoutSeconds { get; init; } = 100;

    public long MaxRequestBodyBytes { get; init; } = 104_857_600;
}

public enum RepositoryProvider
{
    GitHub,
    Other
}

public enum DeploymentAdapter
{
    Command,
    Node,
    Static,
    DockerCompose
}

public enum DeploymentMode
{
    Worktree,
    Local
}

public enum RestartPolicy
{
    Never,
    OnFailure,
    Always
}

public enum HealthCheckType
{
    None,
    Process,
    Http,
    Tcp
}

public enum AppVisibility
{
    Public,
    Protected,
    Disabled
}
