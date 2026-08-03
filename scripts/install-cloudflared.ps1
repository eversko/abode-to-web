#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Machine', 'User')][string]$Scope = 'Machine',
    [switch]$Upgrade
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force

$winget = (Get-Command winget.exe -ErrorAction SilentlyContinue).Source
if ([string]::IsNullOrWhiteSpace($winget)) {
    throw 'Windows Package Manager (winget) is required by this installer. Install cloudflared from https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/downloads/ and verify it with cloudflared --version.'
}
if ($Scope -eq 'Machine') { Assert-AbodeToWebAdministrator -Action 'Installing cloudflared for all users' }

$action = if ($Upgrade) { 'upgrade' } else { 'install' }
$arguments = @(
    $action, '--id', 'Cloudflare.cloudflared', '--exact', '--source', 'winget',
    '--scope', $Scope.ToLowerInvariant(), '--accept-source-agreements', '--accept-package-agreements', '--silent'
)
$arguments += @('--disable-interactivity')
Invoke-AbodeToWebNative -FilePath $winget -ArgumentList $arguments -ErrorMessage "Unable to $action cloudflared with winget"

$cloudflared = (Get-Command cloudflared.exe -ErrorAction SilentlyContinue).Source
if ([string]::IsNullOrWhiteSpace($cloudflared)) {
    foreach ($candidate in @((Join-Path $env:ProgramFiles 'cloudflared\cloudflared.exe'), (Join-Path ${env:ProgramFiles(x86)} 'cloudflared\cloudflared.exe'))) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) { $cloudflared = $candidate; break }
    }
}
if ([string]::IsNullOrWhiteSpace($cloudflared)) {
    Write-Warning 'winget completed, but this PowerShell session has not discovered cloudflared. Open a new PowerShell 7 session and run cloudflared --version.'
} else {
    & $cloudflared --version
}

Write-Host 'The binary is installed. No tunnel, service, DNS record, firewall rule, or credential was created.'
Write-Host 'Next: run cloudflared tunnel login as your normal user, then review scripts/configure-tunnel.ps1 -Help.'
