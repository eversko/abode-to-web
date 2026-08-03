#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'AbodeToWeb'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
    [string]$RuntimeAccountName = 'AbodeToWebRuntime',
    [switch]$RemoveData,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.RuntimeAccount.psm1') -Force
Assert-AbodeToWebAdministrator -Action 'Uninstalling the AbodeToWeb Windows service and Program Files installation'

$layout = Get-AbodeToWebLayout -InstallRoot $InstallRoot -DataRoot $DataRoot
Assert-AbodeToWebStandardInstallRoot -Path $layout.InstallRoot | Out-Null
Assert-AbodeToWebStandardDataRoot -Path $layout.DataRoot | Out-Null
if (Test-Path -LiteralPath $layout.InstallRoot -PathType Container) { Assert-AbodeToWebTreeHasNoReparsePoints -Path $layout.InstallRoot }
if ($RemoveData -and (Test-Path -LiteralPath $layout.DataRoot -PathType Container)) { Assert-AbodeToWebTreeHasNoReparsePoints -Path $layout.DataRoot }
$service = Get-AbodeToWebService -ServiceName $ServiceName
if ($null -ne $service) {
    $expectedServiceExecutable = Join-Path $layout.InstallRoot 'AbodeToWeb.Server.exe'
    if (-not (Test-AbodeToWebServiceExecutable -ServiceName $ServiceName -ExecutablePath $expectedServiceExecutable)) {
        throw "Service '$ServiceName' does not run Abode to Web from '$expectedServiceExecutable'. Refusing to stop or delete an unrelated service."
    }
}
$managedRootsPath = Join-Path $layout.ConfigRoot 'managed-project-roots.json'
$managedRootPaths = [Collections.Generic.List[string]]::new()
$managedRuntimeSids = [Collections.Generic.List[string]]::new()
$runtimeLayout = Get-AbodeToWebRuntimeAccountLayout -DataRoot $layout.DataRoot -AccountName $RuntimeAccountName
$validatedRuntimeMarker = $null
if (Test-Path -LiteralPath $runtimeLayout.MarkerPath -PathType Leaf) {
    try {
        Assert-AbodeToWebRuntimeAccountAvailable -DataRoot $layout.DataRoot -AccountName $RuntimeAccountName | Out-Null
        $validatedRuntimeMarker = Get-AbodeToWebRuntimeAccountMarker -MarkerPath $runtimeLayout.MarkerPath
        $managedRuntimeSids.Add([Security.Principal.SecurityIdentifier]::new([string]$validatedRuntimeMarker.accountSid).Value)
    } catch {
        throw "Runtime-account ownership cannot be validated; no service, account, ACL, binary, or data changes were made. $($_.Exception.Message)"
    }
}
if (Test-Path -LiteralPath $managedRootsPath -PathType Leaf) {
    $managedRootState = Get-Content -LiteralPath $managedRootsPath -Raw | ConvertFrom-Json
    $rootsProperty = $managedRootState.PSObject.Properties['roots']
    if ($null -eq $rootsProperty) { throw "Managed project-root registry is invalid: '$managedRootsPath'." }
    foreach ($managedRoot in @($rootsProperty.Value)) {
        $managedFullPath = [IO.Path]::GetFullPath([string]$managedRoot)
        if (Test-Path -LiteralPath $managedFullPath -PathType Container) {
            $managedRootPaths.Add((Assert-AbodeToWebProjectRoot -Path $managedFullPath))
        }
    }
}
$removeService = $null -ne $service -and $PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete Windows service')
if ($removeService) {
    Stop-AbodeToWebServiceIfRunning -ServiceName $ServiceName -TimeoutSeconds 45 | Out-Null
}
if ($null -ne $service -and -not $removeService) {
    Write-Warning "Service removal was not approved. Abode to Web binaries, runtime identity, ACL grants, shortcuts, and data were left unchanged."
    return
}
$cleanupManagedRoots = $removeService -or $null -eq $service
if ($cleanupManagedRoots) {
    foreach ($managedFullPath in $managedRootPaths) {
        if ($PSCmdlet.ShouldProcess($managedFullPath, 'Remove the Abode to Web service-SID project-root grant')) {
            Remove-AbodeToWebAclGrant -Path $managedFullPath -Identity "NT SERVICE\$ServiceName" | Out-Null
            foreach ($runtimeSid in $managedRuntimeSids) {
                Remove-AbodeToWebAclGrant -Path $managedFullPath -Identity "*$runtimeSid" | Out-Null
            }
        }
    }
    if (Test-Path -LiteralPath $managedRootsPath -PathType Leaf) {
        if ($PSCmdlet.ShouldProcess($managedRootsPath, 'Remove the managed project-root grant registry')) {
            Remove-Item -LiteralPath $managedRootsPath -Force
        }
    }
}
$runtimeAccountPresentBeforeCleanup = $null -ne (Get-LocalUser -Name $RuntimeAccountName -ErrorAction SilentlyContinue)
$runtimeMarkerPresentBeforeCleanup = Test-Path -LiteralPath $runtimeLayout.MarkerPath -PathType Leaf
$runtimeCleanupNeeded = $runtimeAccountPresentBeforeCleanup -or $runtimeMarkerPresentBeforeCleanup
$runtimeAccountCleanupComplete = -not $runtimeCleanupNeeded
if (($removeService -or $null -eq $service) -and $runtimeCleanupNeeded -and
    $PSCmdlet.ShouldProcess($RuntimeAccountName, 'Remove AbodeToWeb-owned dedicated local runtime account')) {
    foreach ($runtimeSid in $managedRuntimeSids) {
        Remove-AbodeToWebRuntimeDataAcl -DataRoot $layout.DataRoot -RuntimeAccountSid $runtimeSid
    }
    $runtimeRemoval = Remove-AbodeToWebRuntimeAccount -DataRoot $layout.DataRoot -AccountName $RuntimeAccountName
    $runtimeAccountCleanupComplete = [bool]$runtimeRemoval.owned
    if (-not $runtimeAccountCleanupComplete) { Write-Warning $runtimeRemoval.reason }
}
if ($runtimeCleanupNeeded -and -not $runtimeAccountCleanupComplete) {
    Write-Warning "The dedicated runtime identity or its ownership files were preserved because safe AbodeToWeb ownership could not be confirmed or removal was not approved."
}
if ($removeService) {
    Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\sc.exe" -ArgumentList @('delete', $ServiceName) -ErrorMessage "Unable to delete service '$ServiceName'"
}

if (Test-Path -LiteralPath $layout.InstallRoot) {
    $installParent = Split-Path -Parent $layout.InstallRoot
    Assert-AbodeToWebSafeChildPath -Path $layout.InstallRoot -Parent $installParent | Out-Null
    if ($PSCmdlet.ShouldProcess($layout.InstallRoot, 'Remove installed binaries')) {
        Remove-Item -LiteralPath $layout.InstallRoot -Recurse -Force
    }
}

$startMenuDirectory = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AbodeToWeb'
$shortcutPaths = @(
    (Join-Path $startMenuDirectory 'AbodeToWeb.url'),
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'AbodeToWeb.url')
)
foreach ($shortcutPath in $shortcutPaths) {
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
        if ($PSCmdlet.ShouldProcess($shortcutPath, 'Remove Abode to Web dashboard shortcut')) {
            Remove-Item -LiteralPath $shortcutPath -Force
        }
    }
}
if ((Test-Path -LiteralPath $startMenuDirectory -PathType Container) -and
    -not (Get-ChildItem -LiteralPath $startMenuDirectory -Force | Select-Object -First 1) -and
    $PSCmdlet.ShouldProcess($startMenuDirectory, 'Remove empty AbodeToWeb Start-menu folder')) {
    Remove-Item -LiteralPath $startMenuDirectory -Force
}

if ($RemoveData -and (Test-Path -LiteralPath $layout.DataRoot)) {
    if (-not $runtimeAccountCleanupComplete) {
        throw "Refusing -RemoveData because the Abode to Web runtime identity cleanup is incomplete. Deleting its ownership marker or credential now would prevent safe recovery. Resolve the warning first."
    }
    $approved = $Force -or $PSCmdlet.ShouldContinue(
        "This permanently removes runtime state, deployment history, logs, backups, and application secret files from '$($layout.DataRoot)'. Hosted project repositories are not removed.",
        'Remove all Abode to Web mutable data?'
    )
    if (-not $approved) { throw 'Data removal was cancelled. Binaries and the service may already have been removed.' }
    $dataParent = Split-Path -Parent $layout.DataRoot
    Assert-AbodeToWebSafeChildPath -Path $layout.DataRoot -Parent $dataParent | Out-Null
    if ($PSCmdlet.ShouldProcess($layout.DataRoot, 'Permanently remove mutable Abode to Web data')) {
        Remove-Item -LiteralPath $layout.DataRoot -Recurse -Force
    }
} else {
    Write-Host "Mutable data was preserved at $($layout.DataRoot). Use -RemoveData only after taking a verified backup."
}

Write-Host 'Abode to Web was uninstalled. No hosted project repositories, firewall rules, router settings, DNS records, power settings, or Cloudflare resources were changed.'
