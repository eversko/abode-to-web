#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'AbodeToWeb'),
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force

$service = Get-AbodeToWebService -ServiceName $ServiceName
if ($null -eq $service) { throw "Service '$ServiceName' is not installed." }
$expectedExecutable = Join-Path (Assert-AbodeToWebStandardInstallRoot -Path $InstallRoot) 'AbodeToWeb.Server.exe'
if (-not (Test-AbodeToWebServiceExecutable -ServiceName $ServiceName -ExecutablePath $expectedExecutable)) {
    throw "Service '$ServiceName' does not run '$expectedExecutable'. Refusing to restart an unrelated service."
}
Assert-AbodeToWebAdministrator -Action 'Restarting the AbodeToWeb Windows service'

if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $ServiceName
    Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Stopped -TimeoutSeconds $TimeoutSeconds | Out-Null
}
Start-Service -Name $ServiceName
Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds $TimeoutSeconds | Out-Null

$health = Test-AbodeToWebHttpEndpoint -Uri 'http://127.0.0.1:4545/health' -TimeoutSeconds 5 -UseDefaultCredentials
if ($health.Reachable) {
    Write-Host "Abode to Web restarted; dashboard listener returned HTTP $($health.StatusCode)."
} else {
    Write-Warning "The service restarted, but its health endpoint is not ready: $($health.Error)"
}
