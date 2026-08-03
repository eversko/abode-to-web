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
    throw "Service '$ServiceName' does not run '$expectedExecutable'. Refusing to stop an unrelated service."
}
if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped) {
    Write-Host 'Abode to Web is already stopped.'
    return
}

Assert-AbodeToWebAdministrator -Action 'Stopping the AbodeToWeb Windows service'
Stop-Service -Name $ServiceName
Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Stopped -TimeoutSeconds $TimeoutSeconds | Out-Null
Write-Host 'Abode to Web is stopped. Supervised application Job Objects are closed, so their owned process trees are also stopped.'
