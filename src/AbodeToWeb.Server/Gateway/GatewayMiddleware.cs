using System.Net;
using System.Runtime.InteropServices;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Infrastructure.Configuration;
using AbodeToWeb.Infrastructure.Gateway;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Win32.SafeHandles;
using Yarp.ReverseProxy.Forwarder;

namespace AbodeToWeb.Server.Gateway;

public sealed partial class GatewayMiddleware
{
    private readonly IHttpForwarder _forwarder;
    private readonly GatewayRouteRegistry _routes;
    private readonly HttpMessageInvoker _httpClient;
    private readonly ICloudflareAccessTokenValidator _accessTokenValidator;
    private readonly ManifestProvider _manifest;
    private readonly ILogger<GatewayMiddleware> _logger;
    private readonly FileExtensionContentTypeProvider _contentTypes = new();

    public GatewayMiddleware(
        IHttpForwarder forwarder,
        GatewayRouteRegistry routes,
        GatewayForwarderClient client,
        ICloudflareAccessTokenValidator accessTokenValidator,
        ManifestProvider manifest,
        ILogger<GatewayMiddleware> logger)
    {
        _forwarder = forwarder;
        _routes = routes;
        _httpClient = client.Invoker;
        _accessTokenValidator = accessTokenValidator;
        _manifest = manifest;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        SetGatewayHeaders(context.Response);
        var accessAssertions = context.Request.Headers["Cf-Access-Jwt-Assertion"].ToArray();
        foreach (var headerName in context.Request.Headers.Keys
                     .Where(name => name.StartsWith("Cf-Access-", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            context.Request.Headers.Remove(headerName);
        }

        if (context.Request.Path.Equals("/__abode-to-web/gateway/health", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { status = "healthy" }, context.RequestAborted);
            return;
        }

        var hostname = context.Request.Host.Host;
        if (string.IsNullOrWhiteSpace(hostname) || !_routes.TryGetByHostname(hostname, out var route))
        {
            await WritePageAsync(context, StatusCodes.Status404NotFound, "Unknown site", "This hostname is not configured on this AbodeToWeb gateway.");
            return;
        }

        if (route.Visibility == AppVisibility.Disabled || route.Status == GatewayRouteStatus.Disabled)
        {
            await WritePageAsync(context, StatusCodes.Status404NotFound, "Site unavailable", "This site is not publicly available.");
            return;
        }
        if (route.Visibility == AppVisibility.Protected)
        {
            var desired = _manifest.Current;
            var app = desired.Apps.FirstOrDefault(candidate =>
                candidate.Id.Equals(route.AppId, StringComparison.OrdinalIgnoreCase));
            var access = desired.AbodeToWeb.Cloudflare.Access;
            var audience = app?.Routing.AccessApplicationAudience;
            if (access is null
                || string.IsNullOrWhiteSpace(audience)
                || accessAssertions.Length != 1
                || string.IsNullOrWhiteSpace(accessAssertions[0]))
            {
                await WritePageAsync(context, StatusCodes.Status403Forbidden, "Access required", "This site is protected by Cloudflare Access.");
                return;
            }

            var validation = await _accessTokenValidator.ValidateAsync(
                accessAssertions[0]!,
                access.TeamDomain,
                audience,
                context.RequestAborted);
            if (!validation.IsValid)
            {
                LogAccessValidationFailure(_logger, route.AppId, validation.Failure);
                await WritePageAsync(context, StatusCodes.Status403Forbidden, "Access required", "This site is protected by Cloudflare Access.");
                return;
            }

            // All caller-supplied Access headers were removed before route handling.
            // Add back only identity that this gateway derived from the validated JWT.
            if (!string.IsNullOrWhiteSpace(validation.Email))
            {
                context.Request.Headers["Cf-Access-Authenticated-User-Email"] = validation.Email;
            }
        }
        if (route.Status == GatewayRouteStatus.Stopped)
        {
            if (!route.OfflinePage)
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            await WritePageAsync(context, StatusCodes.Status503ServiceUnavailable, "Site is offline", "This demo is deliberately stopped. Please try again later.");
            return;
        }
        if (route.Status is GatewayRouteStatus.Unhealthy or GatewayRouteStatus.Starting)
        {
            context.Response.Headers.RetryAfter = "10";
            await WritePageAsync(context, StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable", "The application is starting or recovering. Please try again shortly.");
            return;
        }

        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > route.MaxRequestBodyBytes)
        {
            await WritePageAsync(context, StatusCodes.Status413PayloadTooLarge, "Request too large", "This application does not accept requests of that size.");
            return;
        }
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = route.MaxRequestBodyBytes;

        if (route.StaticPath is not null)
        {
            await ServeStaticAsync(context, route.StaticPath);
            return;
        }
        if (route.InternalPort is null)
        {
            await WritePageAsync(context, StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable", "The application does not have an active endpoint.");
            return;
        }

        var destination = $"http://127.0.0.1:{route.InternalPort.Value}";
        var externalScheme = _manifest.Current.AbodeToWeb.Cloudflare.Enabled
            ? Uri.UriSchemeHttps
            : context.Request.IsHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var error = await _forwarder.SendAsync(context, destination, _httpClient,
            new ForwarderRequestConfig { ActivityTimeout = route.RequestTimeout }, new AbodeToWebTransformer(externalScheme));
        if (error != ForwarderError.None && !context.Response.HasStarted)
        {
            LogForwardingFailure(_logger, route.AppId, error);
            await WritePageAsync(context, StatusCodes.Status502BadGateway, "Application unavailable", "The application did not answer the gateway.");
        }
    }

    private async Task ServeStaticAsync(HttpContext context, string root)
    {
        var requestPath = context.Request.Path.Value ?? "/";
        string decoded;
        try { decoded = Uri.UnescapeDataString(requestPath); }
        catch (UriFormatException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (decoded.Contains('\0') || decoded.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var relative = decoded.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(relative, root);
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !Path.TrimEndingDirectorySeparator(candidate).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (Directory.Exists(candidate)) candidate = Path.Combine(candidate, "index.html");
        var staticFile = OpenVerifiedStaticFile(root, candidate);
        if (staticFile is null
            && !Path.HasExtension(relative)
            && context.Request.GetTypedHeaders().Accept?.Any(value => value.MediaType.Value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true) == true)
        {
            candidate = Path.Combine(root, "index.html");
            staticFile = OpenVerifiedStaticFile(root, candidate);
        }
        if (staticFile is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await using var openedFile = staticFile;
        if (!_contentTypes.TryGetContentType(candidate, out var contentType)) contentType = "application/octet-stream";
        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = Path.GetFileName(candidate).Equals("index.html", StringComparison.OrdinalIgnoreCase)
            ? "no-cache" : "public,max-age=3600";
        context.Response.ContentLength = openedFile.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await openedFile.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    internal static FileStream? OpenVerifiedStaticFile(string root, string candidate)
    {
        if (!OperatingSystem.IsWindows()) return null;

        SafeFileHandle? fileHandle = null;
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedCandidate = Path.GetFullPath(candidate);
            var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
            if (!normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
                || ContainsReparsePoint(normalizedRoot, normalizedCandidate))
            {
                return null;
            }

            // Keep the deployment root pinned while resolving the file. Denying delete
            // sharing prevents the root from being renamed or replaced during the open.
            using var rootHandle = CreateFileW(
                normalizedRoot,
                FileReadAttributes,
                FileShare.Read | FileShare.Write,
                0,
                FileMode.Open,
                FileFlagBackupSemantics,
                0);
            if (rootHandle.IsInvalid
                || !TryGetFinalPath(rootHandle, out var openedRoot)
                || !openedRoot.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            fileHandle = File.OpenHandle(
                normalizedCandidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!TryGetFinalPath(fileHandle, out var openedCandidate)
                || !openedCandidate.Equals(normalizedCandidate, StringComparison.OrdinalIgnoreCase)
                || !openedCandidate.StartsWith(openedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || ContainsReparsePoint(normalizedRoot, normalizedCandidate))
            {
                return null;
            }

            var stream = new FileStream(fileHandle, FileAccess.Read, bufferSize: 64 * 1024, isAsync: true);
            fileHandle = null;
            return stream;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
        finally
        {
            fileHandle?.Dispose();
        }
    }

    private static bool ContainsReparsePoint(string root, string candidate)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedCandidate = Path.GetFullPath(candidate);
            var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
            if (!normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && !normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check every existing component, including the deployment root. Checking
            // only the final file permits an ancestor junction to escape the retained
            // deployment directory after a route has been activated.
            var volumeRoot = Path.GetPathRoot(normalizedRoot);
            if (string.IsNullOrEmpty(volumeRoot)) return true;
            var current = volumeRoot;
            foreach (var segment in normalizedCandidate[volumeRoot.Length..]
                         .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static bool TryGetFinalPath(SafeFileHandle handle, out string path)
    {
        const int maximumPathLength = 32_768;
        var capacity = 512;
        while (capacity <= maximumPathLength)
        {
            var buffer = new char[capacity];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) break;
            if (length < buffer.Length)
            {
                path = NormalizeHandlePath(new string(buffer, 0, (int)length));
                return true;
            }

            capacity = checked((int)length + 1);
        }

        path = string.Empty;
        return false;
    }

    private static string NormalizeHandlePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;

    // StringBuilder output marshalling does not have a source-generated equivalent
    // without enabling unsafe code for the entire server project. Keep this tiny,
    // Windows-only boundary explicit instead of broadening project permissions.
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle fileHandle,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 2)] char[] filePath,
        uint filePathLength,
        uint flags);
#pragma warning restore SYSLIB1054

    private static async Task WritePageAsync(HttpContext context, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}} · AbodeToWeb</title><style>
            :root{color-scheme:dark;font-family:Inter,ui-sans-serif,system-ui,sans-serif;background:#0c0d0f;color:#f4f4f5}
            body{min-height:100vh;margin:0;display:grid;place-items:center}.card{max-width:34rem;padding:3rem;border:1px solid #292b31;border-radius:18px;background:#131519;box-shadow:0 20px 60px #0006}
            .mark{font-size:.72rem;letter-spacing:.18em;color:#8f96a3}h1{font-size:1.7rem;margin:1rem 0 .7rem}p{line-height:1.65;color:#afb4bd;margin:0}
            </style></head><body><main class="card"><div class="mark">ABODE_TO_WEB</div><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(detail)}}</p></main></body></html>
            """, context.RequestAborted);
    }

    private static void SetGatewayHeaders(HttpResponse response)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    [LoggerMessage(201, LogLevel.Warning, "Gateway forwarding failed for {appId}: {error}")]
    private static partial void LogForwardingFailure(ILogger logger, string appId, ForwarderError error);

    [LoggerMessage(202, LogLevel.Debug, "Cloudflare Access validation failed for {appId}: {failure}")]
    private static partial void LogAccessValidationFailure(
        ILogger logger,
        string appId,
        CloudflareAccessValidationFailure failure);
}

public sealed class GatewayForwarderClient : IDisposable
{
    public GatewayForwarderClient()
    {
        Invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });
    }

    public HttpMessageInvoker Invoker { get; }
    public void Dispose() => Invoker.Dispose();
}

internal sealed class AbodeToWebTransformer : HttpTransformer
{
    private readonly string _forwardedProto;

    internal AbodeToWebTransformer(string forwardedProto)
    {
        if (!forwardedProto.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !forwardedProto.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(forwardedProto));
        }

        _forwardedProto = forwardedProto;
    }

    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
        var forwardedFor = GetForwardedAddress(httpContext);
        proxyRequest.Headers.Host = null;
        proxyRequest.Headers.Remove("X-Forwarded-For");
        proxyRequest.Headers.Remove("X-Forwarded-Proto");
        proxyRequest.Headers.Remove("X-Forwarded-Host");
        proxyRequest.Headers.Remove("Cf-Connecting-Ip");
        proxyRequest.Headers.Remove("True-Client-Ip");
        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", _forwardedProto);
        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", httpContext.Request.Host.Value);
    }

    private static string GetForwardedAddress(HttpContext context)
    {
        // The loopback listener cannot distinguish cloudflared from another local
        // process, so an inbound client-IP header is not an authentication boundary.
        // Forward only the observed peer address instead of endorsing spoofable data.
        return context.Connection.RemoteIpAddress?.ToString() ?? IPAddress.Loopback.ToString();
    }

}
