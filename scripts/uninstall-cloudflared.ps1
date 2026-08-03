#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'cloudflared',
    [string]$ConfigurationRoot = (Join-Path $env:ProgramData 'AbodeToWeb\cloudflared'),
    [switch]$KeepBinary,
    [switch]$RemoveConfiguration,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force
Assert-AbodeToWebAdministrator -Action 'Removing the machine-wide Cloudflare Tunnel service or binary'

$configurationRootFull = [IO.Path]::GetFullPath($ConfigurationRoot).TrimEnd('\')
$expectedConfigurationRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'AbodeToWeb\cloudflared')).TrimEnd('\')
if (-not $configurationRootFull.Equals($expectedConfigurationRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Tunnel removal is restricted to Abode to Web's standard configuration root '$expectedConfigurationRoot'."
}
Assert-AbodeToWebNotReparsePoint -Path $configurationRootFull
if ($RemoveConfiguration -and (Test-Path -LiteralPath $configurationRootFull -PathType Container)) {
    Assert-AbodeToWebTreeHasNoReparsePoints -Path $configurationRootFull
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $service -and -not $Force) {
    $serviceDefinition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    $expectedConfig = [IO.Path]::GetFullPath((Join-Path $configurationRootFull 'config.yml'))
    $expectedArgument = '--config "' + $expectedConfig + '"'
    if ($null -eq $serviceDefinition -or -not ([string]$serviceDefinition.PathName).Contains($expectedArgument, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Service '$ServiceName' does not reference Abode to Web's tunnel configuration. Refusing to remove a possibly unrelated connector. Pass -Force only after reviewing its ImagePath."
    }
}
if ($null -ne $service -and $PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete Cloudflare Tunnel Windows service')) {
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $ServiceName
        Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Stopped -TimeoutSeconds 45 | Out-Null
    }
    Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\sc.exe" -ArgumentList @('delete', $ServiceName) -ErrorMessage "Unable to delete service '$ServiceName'"
}

if (-not $KeepBinary) {
    $winget = (Get-Command winget.exe -ErrorAction SilentlyContinue).Source
    if ([string]::IsNullOrWhiteSpace($winget)) {
        Write-Warning 'winget is unavailable; the cloudflared binary was not removed. Remove it using the same method used to install it.'
    } elseif ($PSCmdlet.ShouldProcess('Cloudflare.cloudflared', 'Uninstall package with winget')) {
        & $winget uninstall --id Cloudflare.cloudflared --exact --source winget --silent --disable-interactivity
        if ($LASTEXITCODE -ne 0) { Write-Warning "winget could not remove cloudflared (exit $LASTEXITCODE). The service has still been removed." }
    }
}

if ($RemoveConfiguration -and (Test-Path -LiteralPath $ConfigurationRoot)) {
    $approved = $Force -or $PSCmdlet.ShouldContinue(
        "This permanently removes local tunnel configuration, logs, and copied tunnel credentials under '$ConfigurationRoot'. It does not delete Cloudflare account resources or DNS records.",
        'Remove local Cloudflare Tunnel configuration and credentials?'
    )
    if (-not $approved) { throw 'Configuration removal was cancelled.' }
    $fullRoot = $configurationRootFull
    $programDataRoot = [IO.Path]::GetFullPath($env:ProgramData)
    Assert-AbodeToWebSafeChildPath -Path $fullRoot -Parent $programDataRoot | Out-Null
    if ($PSCmdlet.ShouldProcess($fullRoot, 'Remove local Cloudflare Tunnel configuration and copied credentials')) {
        Remove-Item -LiteralPath $fullRoot -Recurse -Force
    }
} else {
    Write-Host "Local tunnel configuration and credentials were preserved at '$ConfigurationRoot'."
}

Write-Warning 'Cloudflare account tunnels, DNS routes, and Access policies were not deleted. Remove them explicitly in Cloudflare if they are no longer needed.'
