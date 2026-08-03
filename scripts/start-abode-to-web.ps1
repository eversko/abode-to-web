#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'AbodeToWeb'),
    [int]$TimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force

$service = Get-AbodeToWebService -ServiceName $ServiceName
if ($null -eq $service) { throw "Service '$ServiceName' is not installed." }
$expectedExecutable = Join-Path (Assert-AbodeToWebStandardInstallRoot -Path $InstallRoot) 'AbodeToWeb.Server.exe'
if (-not (Test-AbodeToWebServiceExecutable -ServiceName $ServiceName -ExecutablePath $expectedExecutable)) {
    throw "Service '$ServiceName' does not run '$expectedExecutable'. Refusing to start an unrelated service."
}
if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running) {
    Write-Host "Abode to Web is already running."
    return
}

Assert-AbodeToWebAdministrator -Action 'Starting the AbodeToWeb Windows service'
Start-Service -Name $ServiceName
Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds $TimeoutSeconds | Out-Null
Write-Host 'Abode to Web is running at http://127.0.0.1:4545.'
