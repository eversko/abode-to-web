#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'AbodeToWeb'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
    [string]$RuntimeAccountName = 'AbodeToWebRuntime',
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.RuntimeAccount.psm1') -Force

$layout = Get-AbodeToWebLayout -InstallRoot $InstallRoot -DataRoot $DataRoot
$service = Get-AbodeToWebService -ServiceName $ServiceName
$expectedServiceExecutable = Join-Path $layout.InstallRoot 'AbodeToWeb.Server.exe'
$serviceOwnedByAbodeToWeb = $null -ne $service -and (Test-AbodeToWebServiceExecutable -ServiceName $ServiceName -ExecutablePath $expectedServiceExecutable)
$currentUserSid = Get-AbodeToWebCurrentUserSid
$adminSidSetting = if ($null -eq $service) { $null } else { Get-AbodeToWebServiceEnvironmentVariable -ServiceName $ServiceName -Name 'ABODE_TO_WEB_ADMIN_SIDS' }
$adminSids = @()
$adminSidError = $null
if (-not [string]::IsNullOrWhiteSpace($adminSidSetting)) {
    try { $adminSids = @(ConvertTo-AbodeToWebWindowsSidList -Sid $adminSidSetting -RequireUserAccount) }
    catch { $adminSidError = $_.Exception.Message }
}
$dashboard = Test-AbodeToWebHttpEndpoint -Uri 'http://127.0.0.1:4545/health' -TimeoutSeconds 3 -UseDefaultCredentials
$gateway = Test-AbodeToWebHttpEndpoint -Uri 'http://127.0.0.1:4580/__abode-to-web/gateway/health' -TimeoutSeconds 3
$runtimeAccount = Test-AbodeToWebRuntimeAccount -DataRoot $layout.DataRoot -ServiceName $ServiceName -AccountName $RuntimeAccountName
$driveName = [IO.Path]::GetPathRoot($layout.DataRoot)
$drive = Get-PSDrive -Name $driveName.TrimEnd(':\') -PSProvider FileSystem -ErrorAction SilentlyContinue
$cimService = if ($null -eq $service) { $null } else { Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue }
$serviceAccountSid = $null
$serviceAccountError = $null
if ($null -ne $cimService) {
    try { $serviceAccountSid = Resolve-AbodeToWebWindowsAccountSid -AccountName ([string]$cimService.StartName) }
    catch { $serviceAccountError = $_.Exception.Message }
}
$process = $null
if ($null -ne $service -and $service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running) {
    if ($null -ne $cimService -and $cimService.ProcessId -gt 0) {
        $process = Get-Process -Id $cimService.ProcessId -ErrorAction SilentlyContinue
    }
}

$result = [ordered]@{
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    service = [ordered]@{
        installed = $null -ne $service
        name = $ServiceName
        status = if ($null -eq $service) { 'NotInstalled' } else { $service.Status.ToString() }
        startupType = if ($null -eq $service) { $null } else { $service.StartType.ToString() }
        accountName = if ($null -eq $cimService) { $null } else { [string]$cimService.StartName }
        accountSid = $serviceAccountSid
        accountError = $serviceAccountError
        distinctFromRuntime = if ($null -eq $serviceAccountSid -or [string]::IsNullOrWhiteSpace([string]$runtimeAccount.accountSid)) {
            $null
        } else {
            -not $serviceAccountSid.Equals([string]$runtimeAccount.accountSid, [StringComparison]::OrdinalIgnoreCase)
        }
        ownedByAbodeToWeb = $serviceOwnedByAbodeToWeb
        processId = if ($null -eq $process) { $null } else { $process.Id }
        workingSetMb = if ($null -eq $process) { $null } else { [math]::Round($process.WorkingSet64 / 1MB, 1) }
    }
    dashboard = $dashboard
    dashboardIdentity = [ordered]@{
        configured = -not [string]::IsNullOrWhiteSpace($adminSidSetting)
        valid = -not [string]::IsNullOrWhiteSpace($adminSidSetting) -and $null -eq $adminSidError
        authorizedCurrentUser = $adminSids -contains $currentUserSid
        authorizedSidCount = $adminSids.Count
        error = $adminSidError
    }
    gateway = $gateway
    runtimeIdentity = $runtimeAccount
    paths = [ordered]@{
        installRoot = $layout.InstallRoot
        installPresent = Test-Path -LiteralPath $layout.InstallRoot
        dataRoot = $layout.DataRoot
        dataPresent = Test-Path -LiteralPath $layout.DataRoot
        configurationPresent = Test-Path -LiteralPath $layout.ConfigPath -PathType Leaf
    }
    disk = if ($null -eq $drive) { $null } else { [ordered]@{
        root = $drive.Root
        freeGb = [math]::Round($drive.Free / 1GB, 2)
        usedGb = [math]::Round($drive.Used / 1GB, 2)
    }}
}

if ($Json) {
    $result | ConvertTo-Json -Depth 6
} else {
    [pscustomobject]$result
}
