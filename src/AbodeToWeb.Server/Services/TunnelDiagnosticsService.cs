using System.Diagnostics;
using System.ServiceProcess;
using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Infrastructure.Configuration;

namespace AbodeToWeb.Server.Services;

public sealed class TunnelDiagnosticsService(ManifestProvider manifest, IHttpClientFactory clients)
{
    private static readonly Uri CloudflaredReadinessUri = new("http://127.0.0.1:49312/ready");

    public async Task<TunnelStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable("cloudflared.exe");
        var installed = executable is not null;
        var version = installed ? await ReadVersionAsync(executable!, cancellationToken) : null;
        var (serviceInstalled, serviceRunning) = GetServiceState();
        var gatewayReachable = await CheckGatewayAsync(cancellationToken);
        var configured = manifest.Current.AbodeToWeb.Cloudflare.Enabled && !string.IsNullOrWhiteSpace(manifest.Current.AbodeToWeb.Cloudflare.TunnelName);
        var appearsConnected = configured && serviceRunning && gatewayReachable
            && await CheckTunnelReadinessAsync(cancellationToken);

        var state = !installed ? TunnelConnectionState.NotInstalled
            : !configured ? TunnelConnectionState.NotConfigured
            : !serviceRunning ? TunnelConnectionState.Stopped
            : appearsConnected ? TunnelConnectionState.Connected
            : gatewayReachable ? TunnelConnectionState.Degraded
            : TunnelConnectionState.Degraded;
        var diagnostic = state switch
        {
            TunnelConnectionState.NotInstalled => "cloudflared is not installed; use the guided setup script.",
            TunnelConnectionState.NotConfigured => "Cloudflare Tunnel is disabled or has no tunnel name in the manifest.",
            TunnelConnectionState.Stopped => "The separate cloudflared Windows service is stopped.",
            TunnelConnectionState.Degraded when !gatewayReachable => "The local AbodeToWeb gateway is not reachable.",
            TunnelConnectionState.Degraded => "The service is running, but a connected tunnel could not be confirmed.",
            _ => null
        };
        return new TunnelStatus(state, installed, version, serviceInstalled, serviceRunning, gatewayReachable,
            manifest.Current.AbodeToWeb.Cloudflare.TunnelName, DateTimeOffset.UtcNow, diagnostic);
    }

    private async Task<bool> CheckGatewayAsync(CancellationToken cancellationToken)
    {
        try
        {
            var listener = manifest.Current.AbodeToWeb.Gateway;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await clients.CreateClient(nameof(TunnelDiagnosticsService)).GetAsync(
                $"http://{listener.Bind}:{listener.Port}/__abode-to-web/gateway/health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { return false; }
    }

    private static async Task<string?> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var result = await RunAsync(executable, ["--version"], cancellationToken);
        if (!result.Success) return null;
        var match = System.Text.RegularExpressions.Regex.Match(result.Output, @"\d+\.\d+\.\d+");
        return match.Success ? match.Value : result.Output.Trim();
    }

    private async Task<bool> CheckTunnelReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await clients.CreateClient(nameof(TunnelDiagnosticsService))
                .GetAsync(CloudflaredReadinessUri, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { return false; }
    }

    private static async Task<(bool Success, string Output)> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return (false, string.Empty);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode == 0, output.Length > 4_096 ? output[..4_096] : output);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { return (false, string.Empty); }
    }

    private static (bool Installed, bool Running) GetServiceState()
    {
        if (!OperatingSystem.IsWindows()) return (false, false);
        try
        {
            using var service = new ServiceController("cloudflared");
            var status = service.Status;
            return (true, status == ServiceControllerStatus.Running);
        }
        catch (InvalidOperationException) { return (false, false); }
    }

    private static string? FindExecutable(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var conventional = Path.Combine(programFiles, "cloudflared", fileName);
            if (File.Exists(conventional)) return conventional;
        }
        return null;
    }
}
