using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Infrastructure.Persistence;

namespace AbodeToWeb.IntegrationTests;

internal sealed class ActualHostTestHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ManifestJson = JsonManifestParser.CreateSerializerOptions();
    private readonly TemporaryAbodeToWebDirectory _temporary = new();
    private readonly StringBuilder _output = new();
    private readonly Lock _outputLock = new();
    private Process? _process;
    private bool _disposed;

    private ActualHostTestHost()
    {
        DashboardPort = IntegrationTestWait.GetAvailableLoopbackPort();
        do
        {
            GatewayPort = IntegrationTestWait.GetAvailableLoopbackPort();
        } while (GatewayPort == DashboardPort);

        ProjectsRoot = _temporary.GetPath("projects");
        StaticRoot = Path.Combine(_temporary.Paths.DeploymentsRoot, "static-site", "static-output");
        Directory.CreateDirectory(ProjectsRoot);
        Directory.CreateDirectory(StaticRoot);
        File.WriteAllText(Path.Combine(StaticRoot, "index.html"),
            "<!doctype html><html><body>STATIC ABODE_TO_WEB FIXTURE</body></html>");
        File.WriteAllText(Path.Combine(StaticRoot, "app.js"), "globalThis.abodeToWebStaticFixture = true;");
        File.WriteAllText(_temporary.GetPath("outside.txt"), "TRAVERSAL MUST NOT BE SERVED");
        Manifest = CreateManifest();
    }

    internal int DashboardPort { get; }

    internal int GatewayPort { get; }

    internal string DataRoot => _temporary.Root;

    internal string ConfigurationFile => _temporary.Paths.ConfigurationFile;

    internal string ProjectsRoot { get; }

    internal string StaticRoot { get; }

    internal AbodeToWebManifest Manifest { get; private set; }

    internal string ProcessOutput
    {
        get
        {
            lock (_outputLock)
            {
                return _output.ToString();
            }
        }
    }

    internal static Task<ActualHostTestHost> StartAsync(CancellationToken cancellationToken = default) =>
        StartAsync(experimentalProjectExecution: false, cancellationToken: cancellationToken);

    internal static async Task<ActualHostTestHost> StartAsync(
        bool experimentalProjectExecution,
        CancellationToken cancellationToken = default)
    {
        var host = new ActualHostTestHost();
        if (experimentalProjectExecution)
        {
            host.Manifest = host.Manifest with
            {
                AbodeToWeb = host.Manifest.AbodeToWeb with { ExperimentalProjectExecution = true }
            };
        }
        try
        {
            await host.PreparePersistenceAsync(cancellationToken);
            await host.WriteManifestAsync(host.Manifest, cancellationToken);
            host.StartProcess();
            await host.WaitUntilReadyAsync(cancellationToken);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    internal HttpClient CreateDashboardClient(bool useCookies = false, bool useDefaultCredentials = true)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = useCookies,
            CookieContainer = new CookieContainer(),
            Credentials = useDefaultCredentials ? CredentialCache.DefaultCredentials : null,
            PreAuthenticate = useDefaultCredentials
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{DashboardPort}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    internal HttpClient CreateGatewayClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{GatewayPort}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    internal async Task<HttpResponseMessage> SendGatewayAsync(
        HttpClient client,
        string hostname,
        string path = "/",
        CancellationToken cancellationToken = default)
    {
        if (client.BaseAddress?.Port != GatewayPort)
        {
            throw new ArgumentException("The client is not connected to this AbodeToWeb gateway listener.", nameof(client));
        }

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = hostname;
        return await client.SendAsync(request, cancellationToken);
    }

    internal async Task ReplaceHostnameAsync(
        string appId,
        string hostname,
        CancellationToken cancellationToken = default)
    {
        var replacement = Manifest.Apps.Select(app => app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)
            ? app with { Routing = app.Routing with { Hostname = hostname } }
            : app).ToArray();
        Manifest = Manifest with { Apps = replacement };
        await WriteManifestAsync(Manifest, cancellationToken);
    }

    internal static async Task<string> GetCsrfTokenAsync(
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("/api/v1/security/csrf", cancellationToken);
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            ?? throw new Xunit.Sdk.XunitException("The CSRF endpoint returned no JSON document.");
        using (document)
        {
            return document.RootElement.GetProperty("token").GetString()
                ?? throw new Xunit.Sdk.XunitException("The CSRF endpoint returned no request token.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
        }

        _process?.Dispose();
        await WaitForPortReleaseAsync(DashboardPort);
        await WaitForPortReleaseAsync(GatewayPort);
        _temporary.Dispose();
    }

    private async Task PreparePersistenceAsync(CancellationToken cancellationToken)
    {
        var database = new AbodeToWebDatabase(_temporary.Paths);
        await database.InitializeAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await database.UpsertRouteAsync(new RouteStateRecord(
            "unhealthy-site", "unhealthy.abode-to-web.test", "unhealthy-deployment", null,
            "unhealthy", Path.Combine(_temporary.Paths.DeploymentsRoot, "unhealthy-site", "missing"), now), cancellationToken);
        await database.UpsertRouteAsync(new RouteStateRecord(
            "static-site", "static.abode-to-web.test", "static-deployment", null,
            "healthy", StaticRoot, now), cancellationToken);
    }

    private async Task WriteManifestAsync(AbodeToWebManifest manifest, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.Serialize(manifest, ManifestJson);
        await File.WriteAllTextAsync(ConfigurationFile, content, cancellationToken);
    }

    private void StartProcess()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "AbodeToWeb.Server.exe");
        if (!File.Exists(executable))
        {
            throw new Xunit.Sdk.XunitException(
                $"The actual-host fixture could not find the built AbodeToWeb server at '{executable}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["ABODE_TO_WEB_DATA_ROOT"] = DataRoot;
        startInfo.Environment["ABODE_TO_WEB_CONFIG_PATH"] = ConfigurationFile;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, eventArgs) => AppendOutput("stdout", eventArgs.Data);
        _process.ErrorDataReceived += (_, eventArgs) => AppendOutput("stderr", eventArgs.Data);
        if (!_process.Start())
        {
            throw new Xunit.Sdk.XunitException("The AbodeToWeb server process did not start.");
        }
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var client = CreateDashboardClient();
        var stopwatch = Stopwatch.StartNew();
        Exception? lastException = null;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is { HasExited: true })
            {
                throw new Xunit.Sdk.XunitException(
                    $"AbodeToWeb exited during startup with code {_process.ExitCode}.{Environment.NewLine}{ProcessOutput}");
            }

            try
            {
                using var response = await client.GetAsync("/health", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastException = exception;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            $"AbodeToWeb did not become ready: {lastException?.Message ?? "no successful response"}.{Environment.NewLine}{ProcessOutput}");
    }

    private AbodeToWebManifest CreateManifest()
    {
        return new AbodeToWebManifest
        {
            ManifestVersion = 1,
            AbodeToWeb = new AbodeToWebOptions
            {
                Dashboard = new ListenerOptions { Bind = "127.0.0.1", Port = DashboardPort },
                Gateway = new ListenerOptions { Bind = "127.0.0.1", Port = GatewayPort },
                ProjectsRoots = [ProjectsRoot],
                StartupStaggerSeconds = 0,
                Cloudflare = new CloudflareOptions
                {
                    Enabled = false,
                    Access = new CloudflareAccessOptions
                    {
                        TeamDomain = "https://abode-to-web-tests.cloudflareaccess.com"
                    }
                },
                Storage = new StorageOptions
                {
                    RetainedDeploymentsPerApp = 2,
                    LogRetentionDays = 1,
                    MaxLogBytesPerApp = 1_048_576
                }
            },
            Apps =
            [
                CreateStaticApp("stopped-site", "Stopped site", "stopped.abode-to-web.test", ProjectsRoot),
                CreateStaticApp("no-offline-page", "No offline page", "no-offline.abode-to-web.test", ProjectsRoot, offlinePage: false),
                CreateStaticApp("unhealthy-site", "Unhealthy site", "unhealthy.abode-to-web.test", ProjectsRoot),
                CreateStaticApp("static-site", "Static site", "static.abode-to-web.test", ProjectsRoot)
            ]
        };
    }

    private static AppManifest CreateStaticApp(string id, string name, string hostname, string repositoryPath, bool offlinePage = true) => new()
    {
        Id = id,
        Name = name,
        Enabled = true,
        Autostart = false,
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
        Routing = new RoutingOptions { Hostname = hostname, OfflinePage = offlinePage },
        Visibility = AppVisibility.Public
    };

    private void AppendOutput(string stream, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_outputLock)
        {
            _output.Append(stream).Append(": ").AppendLine(line);
        }
    }

    private static async Task WaitForPortReleaseAsync(int port)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? lastException = null;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return;
            }
            catch (SocketException exception)
            {
                lastException = exception;
                await Task.Delay(50);
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Actual-host integration fixture left loopback port {port} occupied: {lastException?.Message}");
    }
}
