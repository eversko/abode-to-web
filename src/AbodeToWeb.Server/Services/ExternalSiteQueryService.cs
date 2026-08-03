using System.Net;
using System.Net.Sockets;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Core.Health;
using AbodeToWeb.Infrastructure.Configuration;

namespace AbodeToWeb.Server.Services;

public sealed class ExternalSiteQueryService(ManifestProvider manifest, HttpClient client)
{
    public static SocketsHttpHandler CreateHttpMessageHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        ConnectCallback = ConnectToPublicAddressAsync
    };

    public async Task<IReadOnlyList<ExternalSiteSummary>> GetAsync(CancellationToken cancellationToken = default)
    {
        var probes = manifest.Current.ExternalSites.Select(site => ProbeAsync(site, cancellationToken));
        var sites = await Task.WhenAll(probes);
        return sites.OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<ExternalSiteSummary> ProbeAsync(ExternalSiteManifest site, CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, site.Url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var healthy = response.IsSuccessStatusCode
                ? HealthState.Healthy
                : HealthState.Unhealthy;
            return Map(site, healthy, statusCode, observedAt,
                response.IsSuccessStatusCode ? null : $"The site returned HTTP {statusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Map(site, HealthState.Unknown, null, observedAt, "The site health check timed out.");
        }
        catch (HttpRequestException)
        {
            return Map(site, HealthState.Unknown, null, observedAt, "The site could not be reached securely.");
        }
    }

    private static ExternalSiteSummary Map(
        ExternalSiteManifest site,
        HealthState health,
        int? statusCode,
        DateTimeOffset observedAt,
        string? safeStatusMessage) => new(
            site.Id,
            site.Name,
            site.Description,
            site.Url,
            site.Provider,
            site.ManagementUrl,
            site.RepositoryPath,
            health,
            statusCode,
            observedAt,
            safeStatusMessage);

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? lastError = null;
        foreach (var address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastError = exception;
                socket.Dispose();
            }
        }
        throw new HttpRequestException("The external site did not resolve to a reachable public address.", lastError);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xfe) != 0xfc;
        return bytes[0] switch
        {
            0 or 10 or 127 => false,
            100 when bytes[1] is >= 64 and <= 127 => false,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => false,
            192 when bytes[1] == 168 => false,
            >= 224 => false,
            _ => true
        };
    }
}
