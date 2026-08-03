#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'cloudflared',
    [string]$ConfigPath = (Join-Path $env:ProgramData 'AbodeToWeb\cloudflared\config.yml'),
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) { throw "Tunnel service '$ServiceName' is not installed." }
$configFullPath = [IO.Path]::GetFullPath($ConfigPath)
$expectedConfig = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'AbodeToWeb\cloudflared\config.yml'))
if (-not $configFullPath.Equals($expectedConfig, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Tunnel service control is restricted to Abode to Web's standard configuration '$expectedConfig'."
}
$definition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
$expectedArgument = '--config "' + $configFullPath + '"'
if ($null -eq $definition -or -not ([string]$definition.PathName).Contains($expectedArgument, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Service '$ServiceName' does not use '$configFullPath'. Refusing to restart an unrelated service."
}
Assert-AbodeToWebAdministrator -Action 'Restarting the Cloudflare Tunnel Windows service'

Stop-AbodeToWebServiceIfRunning -ServiceName $ServiceName -TimeoutSeconds $TimeoutSeconds | Out-Null
Start-Service -Name $ServiceName
Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds $TimeoutSeconds | Out-Null
Write-Host "Tunnel service '$ServiceName' restarted. Run scripts/status-tunnel.ps1 to confirm gateway and connector health."
