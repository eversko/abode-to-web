#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'cloudflared',
    [string]$ConfigPath = (Join-Path $env:ProgramData 'AbodeToWeb\cloudflared\config.yml'),
    [uri]$GatewayHealthUri = 'http://127.0.0.1:4580/__abode-to-web/gateway/health',
    [uri]$AbodeToWebTunnelUri = 'http://127.0.0.1:4545/api/v1/system/tunnel',
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Cloudflared.Common.psm1') -Force

if ($AbodeToWebTunnelUri.Scheme -ne 'http' -or -not $AbodeToWebTunnelUri.IsLoopback -or $AbodeToWebTunnelUri.Port -ne 4545 -or
    $AbodeToWebTunnelUri.AbsolutePath -ne '/api/v1/system/tunnel' -or -not [string]::IsNullOrEmpty($AbodeToWebTunnelUri.Query) -or
    -not [string]::IsNullOrEmpty($AbodeToWebTunnelUri.Fragment) -or -not [string]::IsNullOrEmpty($AbodeToWebTunnelUri.UserInfo)) {
    throw "-AbodeToWebTunnelUri must be the authenticated Abode to Web loopback endpoint 'http://127.0.0.1:4545/api/v1/system/tunnel'."
}

$cloudflared = $null
$version = $null
try {
    $cloudflared = Get-AbodeToWebCloudflaredExecutable -ExecutablePath $ExecutablePath
    $versionResult = Invoke-AbodeToWebCapturedProcess -FilePath $cloudflared -ArgumentList @('--version') -TimeoutSeconds 10 -AllowFailure
    $version = if ($versionResult.ExitCode -eq 0) { $versionResult.StandardOutput } else { $versionResult.StandardError }
} catch {
    $version = $_.Exception.Message
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$cimService = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
$configFullPath = [IO.Path]::GetFullPath($ConfigPath)
$expectedConfigArgument = '--config "' + $configFullPath + '"'
$ownedByAbodeToWeb = $null -ne $cimService -and ([string]$cimService.PathName).Contains($expectedConfigArgument, [StringComparison]::OrdinalIgnoreCase)
$gateway = Test-AbodeToWebHttpEndpoint -Uri $GatewayHealthUri -TimeoutSeconds 3
$configValid = $null
$configError = $null
if ($null -ne $cloudflared -and (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    $validation = Invoke-AbodeToWebCapturedProcess -FilePath $cloudflared -ArgumentList @('tunnel', '--config', ([IO.Path]::GetFullPath($ConfigPath)), 'ingress', 'validate') -TimeoutSeconds 20 -AllowFailure
    $configValid = $validation.ExitCode -eq 0
    if (-not $configValid) { $configError = $validation.StandardError }
}

$abodeToWebReport = $null
try { $abodeToWebReport = Invoke-RestMethod -Uri $AbodeToWebTunnelUri -Method Get -TimeoutSec 5 -UseDefaultCredentials -AllowUnencryptedAuthentication -MaximumRedirection 0 } catch { }
$result = [ordered]@{
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    installed = $null -ne $cloudflared
    executable = $cloudflared
    version = $version
    service = [ordered]@{
        installed = $null -ne $service
        name = $ServiceName
        state = if ($null -eq $service) { 'NotInstalled' } else { $service.Status.ToString() }
        processId = if ($null -eq $cimService -or $cimService.ProcessId -eq 0) { $null } else { $cimService.ProcessId }
        startMode = if ($null -eq $cimService) { $null } else { $cimService.StartMode }
        ownedByAbodeToWeb = $ownedByAbodeToWeb
    }
    configuration = [ordered]@{
        path = $configFullPath
        present = Test-Path -LiteralPath $ConfigPath -PathType Leaf
        valid = $configValid
        error = $configError
    }
    localGateway = $gateway
    abodeToWebDiagnostic = $abodeToWebReport
}

if ($Json) { $result | ConvertTo-Json -Depth 8 } else { [pscustomobject]$result }
