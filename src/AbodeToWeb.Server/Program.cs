using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Networking;
using AbodeToWeb.Core.Security;
using AbodeToWeb.Infrastructure;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Deployments;
using AbodeToWeb.Infrastructure.Gateway;
using AbodeToWeb.Infrastructure.Health;
using AbodeToWeb.Infrastructure.Logging;
using AbodeToWeb.Infrastructure.Operations;
using AbodeToWeb.Infrastructure.Persistence;
using AbodeToWeb.Infrastructure.Processes;
using AbodeToWeb.Infrastructure.Runtime;
using AbodeToWeb.Infrastructure.Security;
using AbodeToWeb.Server.Api;
using AbodeToWeb.Server.Gateway;
using AbodeToWeb.Server.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.HttpOverrides;
using Yarp.ReverseProxy.Forwarder;

namespace AbodeToWeb.Server;

public partial class Program
{
    private static readonly JsonSerializerOptions CliJsonOptions = new(JsonSerializerDefaults.Web);

    [SupportedOSPlatform("windows")]
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault()?.Equals("doctor-project", StringComparison.OrdinalIgnoreCase) == true)
            return await DoctorProjectCliAsync(args, Console.Out, Console.Error);

        if (args.FirstOrDefault()?.Equals("validate-config", StringComparison.OrdinalIgnoreCase) == true)
            return await ValidateConfigurationCliAsync(args);

        var paths = AbodeToWebPaths.Resolve();
        EnsureInitialConfiguration(paths);
        var parser = new YamlManifestParser();
        var validator = new ManifestValidator();
        var provider = new ManifestProvider(paths, parser, manifest => validator.Validate(manifest,
            new ManifestValidationContext(GetEnvironment(), RequireRepositoryPathsToExist: false)));
        var load = await provider.LoadAsync();
        if (load.Manifest is null)
        {
            Console.Error.WriteLine("AbodeToWeb configuration is invalid:");
            foreach (var error in load.Errors) Console.Error.WriteLine($"  {error.Path}: {error.Message}");
            return 2;
        }
        if (load.UsedLastKnownGood)
            Console.Error.WriteLine("Warning: invalid current configuration was rejected; AbodeToWeb is using the last-known-good manifest.");
        var desired = load.Manifest;

        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(options => options.ServiceName = "AbodeToWeb");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
            options.Listen(ParseAddress(desired.AbodeToWeb.Dashboard.Bind), desired.AbodeToWeb.Dashboard.Port);
            options.Listen(ParseAddress(desired.AbodeToWeb.Gateway.Bind), desired.AbodeToWeb.Gateway.Port);
        });

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(parser);
        builder.Services.AddSingleton<IManifestValidator>(validator);
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton<SecretFileStore>();
        builder.Services.AddSingleton<DynamicLogRedactor>();
        builder.Services.AddSingleton<ILogRedactor>(services => services.GetRequiredService<DynamicLogRedactor>());
        builder.Services.AddSingleton<LogStreamHub>();
        builder.Services.AddSingleton(services => new BoundedLogStore(paths, services.GetRequiredService<LogStreamHub>(),
            services.GetRequiredService<DynamicLogRedactor>().Redact));
        builder.Services.AddSingleton<AbodeToWebDatabase>();
        builder.Services.AddSingleton<OperationStreamHub>();
        builder.Services.AddSingleton<OperationCoordinator>();
        builder.Services.AddHostedService(services => services.GetRequiredService<OperationCoordinator>());
        builder.Services.AddSingleton<ApplicationSupervisor>();
        builder.Services.AddSingleton<IManagedProcessTreeSnapshotProvider>(services =>
            services.GetRequiredService<ApplicationSupervisor>());
        builder.Services.AddSingleton<ITcpListenerSnapshotProvider, WindowsTcpListenerSnapshotProvider>();
        builder.Services.AddSingleton<HostedEndpointVerifier>();
        builder.Services.AddSingleton<StructuredCommandRunner>();
        builder.Services.AddSingleton<GitClient>();
        builder.Services.AddSingleton<GatewayRouteRegistry>();
        builder.Services.AddSingleton<IPortAllocator>(_ => new DeterministicPortAllocator());
        builder.Services.AddHttpClient(nameof(ApplicationHealthProbe))
            .ConfigurePrimaryHttpMessageHandler(ApplicationHealthProbe.CreateHttpMessageHandler);
        builder.Services.AddHttpClient(nameof(TunnelDiagnosticsService)).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
        builder.Services.AddHttpClient(nameof(ExternalSiteQueryService), client =>
        {
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AbodeToWeb/1.0");
        }).ConfigurePrimaryHttpMessageHandler(ExternalSiteQueryService.CreateHttpMessageHandler);
        builder.Services.AddHttpClient(nameof(CloudflareAccessTokenValidator), client =>
        {
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AbodeToWeb/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        });
        builder.Services.AddSingleton(services => new ApplicationHealthProbe(services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ApplicationHealthProbe))));
        builder.Services.AddSingleton<ICloudflareAccessTokenValidator>(services =>
            new CloudflareAccessTokenValidator(
                services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CloudflareAccessTokenValidator))));
        builder.Services.AddSingleton<DeploymentEngine>();
        builder.Services.AddSingleton<ProjectQueryService>();
        builder.Services.AddSingleton(services => new ExternalSiteQueryService(
            services.GetRequiredService<ManifestProvider>(),
            services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ExternalSiteQueryService))));
        builder.Services.AddSingleton<ExternalSiteConfigurationService>();
        builder.Services.AddSingleton<LocalProjectQueryService>();
        builder.Services.AddSingleton<LocalProjectConfigurationService>();
        builder.Services.AddSingleton<SystemStatusService>();
        builder.Services.AddSingleton<TunnelDiagnosticsService>();
        builder.Services.AddSingleton<OnboardingService>();
        builder.Services.AddSingleton<ProjectConfigurationService>();
        builder.Services.AddHttpForwarder();
        builder.Services.AddSingleton<GatewayForwarderClient>();
        builder.Services.AddSingleton<GatewayMiddleware>();

        builder.Services.AddHostedService<AbodeToWebInitializer>();
        builder.Services.AddHostedService<RuntimeReconciliationService>();
        builder.Services.AddHostedService<ApplicationHealthMonitoringService>();
        builder.Services.AddHostedService<ManifestWatchService>();
        builder.Services.AddHostedService<LogQuotaService>();

        builder.Services.AddOpenApi();
        builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
        builder.Services.AddAuthorization();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false)));
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<SafeExceptionHandler>();
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__AbodeToWeb-Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 180,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
        builder.Services.AddSingleton<ILoggerProvider, AbodeToWebFileLoggerProvider>();

        var app = builder.Build();
        var dashboardAdminSids = ResolveDashboardAdminSids();
        LogDashboardOperators(app.Logger, string.Join(", ", dashboardAdminSids.Select(DescribeWindowsSid)));
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort == desired.AbodeToWeb.Gateway.Port)
            {
                await context.RequestServices.GetRequiredService<GatewayMiddleware>().HandleAsync(context);
                return;
            }
            if (context.Connection.LocalPort != desired.AbodeToWeb.Dashboard.Port)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!IsAllowedDashboardHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Invalid dashboard host",
                    status = StatusCodes.Status421MisdirectedRequest,
                    code = "dashboard_host_invalid",
                    detail = "Open AbodeToWeb using http://127.0.0.1:4545 or http://localhost:4545."
                });
                return;
            }
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                await context.ChallengeAsync(NegotiateDefaults.AuthenticationScheme);
                return;
            }

            var requestSid = (context.User.Identity as WindowsIdentity)?.User?.Value
                ?? context.User.FindFirstValue(ClaimTypes.PrimarySid);
            if (requestSid is null || !dashboardAdminSids.Contains(requestSid))
            {
                // Tell the (already Windows-authenticated, loopback-only) caller who they
                // signed in as; an unexplained deny is undiagnosable. The allowlist itself
                // is never disclosed. Blank-password accounts authenticate as ANONYMOUS
                // LOGON, which reads as "denied" while the operator's own SID is authorized.
                var accountName = context.User.Identity?.Name;
                var displayName = string.IsNullOrWhiteSpace(accountName) ? "<unknown>" : accountName;
                var signedInAsAnonymous = string.Equals(requestSid, AnonymousLogonSid, StringComparison.OrdinalIgnoreCase);
                LogDashboardAccessDenied(app.Logger, displayName, requestSid ?? "<no SID>");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Dashboard access denied",
                    status = StatusCodes.Status403Forbidden,
                    code = signedInAsAnonymous ? "dashboard_identity_anonymous" : "dashboard_identity_denied",
                    detail = signedInAsAnonymous
                        ? "Windows signed this session in as ANONYMOUS LOGON, which usually means the account has a blank password. Set a password on the Windows account, close every browser window, then sign in again with that account name and its password."
                        : $"Windows account '{displayName}' is not an authorized AbodeToWeb operator. See docs/TROUBLESHOOTING.md, \"Dashboard sign-in is denied\", to review and repair ABODE_TO_WEB_ADMIN_SIDS.",
                    account = string.IsNullOrWhiteSpace(accountName) ? null : accountName,
                    sid = requestSid
                });
                return;
            }
            await next(context);
        });
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1")
                && (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
                    || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)))
            {
                if (!HasValidDashboardOrigin(context))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { title = "Invalid request origin", status = 403, code = "origin_invalid", detail = "Refresh AbodeToWeb and try again." });
                    return;
                }
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { title = "Invalid request", status = 400, code = "csrf_invalid", detail = "Refresh AbodeToWeb and try again." });
                    return;
                }
            }
            await next(context);
        });
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapAbodeToWebApi();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl = context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
                    ? "no-cache" : "public,max-age=31536000,immutable";
            }
        });
        app.MapFallback(async context =>
        {
            var index = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
            if (File.Exists(index)) { context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.SendFileAsync(index, context.RequestAborted); }
            else
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("AbodeToWeb dashboard assets are not installed. Re-run install-abode-to-web.ps1.", context.RequestAborted);
            }
        }).ExcludeFromDescription();

        await app.RunAsync();
        return 0;
    }

    private static bool IsAllowedDashboardHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private const string AnonymousLogonSid = "S-1-5-7";

    [SupportedOSPlatform("windows")]
    private static HashSet<string> ResolveDashboardAdminSids()
    {
        var configured = Environment.GetEnvironmentVariable("ABODE_TO_WEB_ADMIN_SIDS");
        string[] values;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var current = WindowsIdentity.GetCurrent();
            var currentSid = current.User?.Value
                ?? throw new InvalidOperationException("AbodeToWeb could not read the current Windows SID.");
            // Interactive development fallback only. Under a service account the same
            // fallback would authorize the service itself and no human could ever sign
            // in — fail closed with the repair path instead of a silent lockout.
            if (currentSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
                throw new InvalidOperationException(
                    $"ABODE_TO_WEB_ADMIN_SIDS is not configured and AbodeToWeb is running as the service account '{current.Name}', so no Windows operator could sign in to the dashboard. Repair the service environment (repair-abode-to-web.ps1 -Apply -DashboardAdminSid <operator user SID>) and restart the service.");
            values = [currentSid];
        }
        else values = configured.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            try { result.Add(new SecurityIdentifier(value!).Value); }
            catch (ArgumentException exception) { throw new InvalidOperationException("ABODE_TO_WEB_ADMIN_SIDS contains an invalid Windows SID.", exception); }
        }
        if (result.Count == 0) throw new InvalidOperationException("AbodeToWeb could not determine an authorized dashboard Windows SID.");
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static string DescribeWindowsSid(string sid)
    {
        try { return $"{new SecurityIdentifier(sid).Translate(typeof(NTAccount))} ({sid})"; }
        catch (SystemException) { return $"<unresolved account> ({sid})"; }
    }

    [LoggerMessage(401, LogLevel.Information, "Dashboard operators authorized by Windows user SID: {operators}")]
    private static partial void LogDashboardOperators(ILogger logger, string operators);

    [LoggerMessage(402, LogLevel.Warning, "Dashboard access denied for Windows account {account} ({sid})")]
    private static partial void LogDashboardAccessDenied(ILogger logger, string account, string sid);

    private static bool HasValidDashboardOrigin(HttpContext context)
    {
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (!string.IsNullOrEmpty(fetchSite)
            && fetchSite is not "same-origin" and not "none") return false;

        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !IsAllowedDashboardHost(uri.Host)) return false;
        return uri.Port == context.Connection.LocalPort;
    }

    private static async Task<int> ValidateConfigurationCliAsync(string[] args)
    {
        var index = Array.FindIndex(args, argument => argument.Equals("--path", StringComparison.OrdinalIgnoreCase));
        var path = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : AbodeToWebPaths.Resolve().ConfigurationFile;
        var json = args.Any(argument => argument.Equals("--json", StringComparison.OrdinalIgnoreCase));
        try
        {
            var parser = new YamlManifestParser();
            var manifest = parser.Parse(await File.ReadAllTextAsync(path));
            var validation = new ManifestValidator().Validate(manifest, new ManifestValidationContext(GetEnvironment(), false));
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    valid = validation.IsValid,
                    projectsRoots = manifest.AbodeToWeb.ProjectsRoots,
                    issues = validation.Issues.Select(issue => new { severity = issue.Severity.ToString(), issue.Code, issue.Path, issue.Message })
                }, CliJsonOptions));
            }
            else foreach (var issue in validation.Issues) Console.WriteLine($"{issue.Severity}: {issue.Path}: {issue.Message}");
            if (!validation.IsValid) return 1;
            if (!json) Console.WriteLine("Configuration is valid.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or ManifestFormatException)
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { valid = false, projectsRoots = Array.Empty<string>(), issues = new[] { new { severity = "Error", code = "manifest.invalid", path = "$", message = exception.Message } } }, CliJsonOptions));
            else Console.Error.WriteLine("Configuration is invalid: " + exception.Message);
            return 1;
        }
    }

    internal static async Task<int> DoctorProjectCliAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string? path = null;
        var json = false;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                if (json) return await WriteDoctorCliErrorAsync(output, error, true, "argument.duplicate", "--json may be specified only once.");
                json = true;
                continue;
            }

            if (argument.Equals("--path", StringComparison.OrdinalIgnoreCase))
            {
                if (path is not null) return await WriteDoctorCliErrorAsync(output, error, json, "argument.duplicate", "--path may be specified only once.");
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    return await WriteDoctorCliErrorAsync(output, error, json, "path.required", "Provide an existing project directory with --path.");
                path = args[++index];
                continue;
            }

            return await WriteDoctorCliErrorAsync(output, error, json, "argument.unknown", "Only --path <existing project> and --json are supported.");
        }

        if (path is null)
            return await WriteDoctorCliErrorAsync(output, error, json, "path.required", "Provide an existing project directory with --path.");

        try
        {
            var report = await ProjectInspector.CreateDoctorReportAsync(path, cancellationToken);
            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(report, CliJsonOptions));
            }
            else
            {
                await output.WriteLineAsync($"Status: {report.Status}");
                await output.WriteLineAsync("Changes made: no");
                await output.WriteLineAsync($"Detected: {report.Detected.Framework} ({report.Detected.Adapter})");
                await output.WriteLineAsync($"Git repository: {(report.Git.IsRepository ? "yes" : "no")}");
                await output.WriteLineAsync("No command suggestion was selected or executed.");
                foreach (var warning in report.Warnings) await output.WriteLineAsync($"Warning: {warning}");
                foreach (var action in report.ApprovalActions)
                    await output.WriteLineAsync($"Approval required: {action.Phrase} — {action.Authorizes}");
            }
            return 0;
        }
        catch (ProjectInspectionException exception)
        {
            return await WriteDoctorCliErrorAsync(output, error, json, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return await WriteDoctorCliErrorAsync(output, error, json, "inspection.unavailable", "The project metadata could not be inspected safely.");
        }
    }

    private static async Task<int> WriteDoctorCliErrorAsync(
        TextWriter output,
        TextWriter error,
        bool json,
        string code,
        string message)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                changesMade = false,
                status = "unsupported",
                error = new { code, message }
            }, CliJsonOptions));
        }
        else
        {
            await error.WriteLineAsync($"Project inspection failed: {message}");
        }
        return 2;
    }

    private static void EnsureInitialConfiguration(AbodeToWebPaths paths)
    {
        if (File.Exists(paths.ConfigurationFile)) return;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config", "abode-to-web.example.yaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "config", "abode-to-web.example.yaml"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config", "abode-to-web.example.yaml"))
        };
        var example = candidates.FirstOrDefault(File.Exists);
        if (example is null) throw new FileNotFoundException("AbodeToWeb configuration and bundled example are missing.", paths.ConfigurationFile);
        paths.EnsureDirectories();
        File.Copy(example, paths.ConfigurationFile, overwrite: false);
    }

    private static IPAddress ParseAddress(string value) => IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address)
        ? address : throw new InvalidOperationException("AbodeToWeb listeners must bind to a loopback IP address.");

    private static Dictionary<string, string> GetEnvironment() => Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>().Where(entry => entry.Key is string && entry.Value is string)
        .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
