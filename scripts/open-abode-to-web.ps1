#requires -Version 7.0
[CmdletBinding()]
param([uri]$DashboardUrl = 'http://127.0.0.1:4545')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force

$DashboardUrl = Assert-AbodeToWebDashboardUri -Uri $DashboardUrl
$health = Test-AbodeToWebHttpEndpoint -Uri $DashboardUrl -TimeoutSeconds 3
if (-not $health.Reachable) {
    Write-Warning 'The dashboard is not responding. Run scripts/status-abode-to-web.ps1; start the service from an elevated session if needed.'
}
Start-Process $DashboardUrl.AbsoluteUri
