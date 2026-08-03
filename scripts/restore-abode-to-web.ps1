#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupPath,
    [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'AbodeToWeb'),
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$TunnelServiceName = 'cloudflared',
    [string]$RuntimeAccountName = 'AbodeToWebRuntime',
    [switch]$SkipSafetyBackup,
    [switch]$Force,
    [long]$MaxExpandedBytes = 20GB
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.RuntimeAccount.psm1') -Force
Assert-AbodeToWebAdministrator -Action 'Restoring protected Abode to Web state and quiescing its Windows service'

$archive = [IO.Path]::GetFullPath($BackupPath)
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Backup does not exist: '$archive'." }
Assert-AbodeToWebNotReparsePoint -Path $archive
$layout = Get-AbodeToWebLayout -DataRoot $DataRoot
Assert-AbodeToWebStandardDataRoot -Path $layout.DataRoot | Out-Null
$installedService = Get-AbodeToWebService -ServiceName $ServiceName
if ($null -eq $installedService) { throw "Service '$ServiceName' is not installed. Install the intended Abode to Web release before restoring its data." }
$expectedServiceExecutable = Join-Path (Assert-AbodeToWebStandardInstallRoot -Path $InstallRoot) 'AbodeToWeb.Server.exe'
if (-not (Test-AbodeToWebServiceExecutable -ServiceName $ServiceName -ExecutablePath $expectedServiceExecutable)) {
    throw "Service '$ServiceName' does not run '$expectedServiceExecutable'. Refusing to stop or modify an unrelated service."
}
if (-not (Test-Path -LiteralPath $expectedServiceExecutable -PathType Leaf)) {
    throw "The installed Abode to Web validator is missing: '$expectedServiceExecutable'. Reinstall the intended release before restoring data."
}
$runtimeAccountStatus = Test-AbodeToWebRuntimeAccount -DataRoot $layout.DataRoot -ServiceName $ServiceName -AccountName $RuntimeAccountName
if (-not $runtimeAccountStatus.healthy) {
    throw "The machine-bound Abode to Web runtime identity is not healthy. Run repair-abode-to-web.ps1 -Apply before restore. $($runtimeAccountStatus.issues -join ' ')"
}
$controlPlaneDefinition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
$controlPlaneSid = Resolve-AbodeToWebWindowsAccountSid -AccountName ([string]$controlPlaneDefinition.StartName)
if ($controlPlaneSid.Equals([string]$runtimeAccountStatus.accountSid, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Abode to Web control-plane service and project runtime identity are the same Windows account. Repair the service identity before restore.'
}
$approved = $Force -or $PSCmdlet.ShouldContinue(
    "This replaces the data categories present in '$archive' under '$($layout.DataRoot)'. The Abode to Web service will be stopped temporarily.",
    'Restore Abode to Web backup?'
)
if (-not $approved) { throw 'Restore was cancelled.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    [long]$expandedBytes = 0
    foreach ($entry in $zip.Entries) {
        $entryPath = $entry.FullName.Replace('/', '\')
        if ([IO.Path]::IsPathRooted($entryPath) -or $entryPath.Contains(':') -or ($entryPath.Split('\') -contains '..')) {
            throw "Unsafe archive entry rejected: '$($entry.FullName)'."
        }
        if ($entryPath.TrimEnd('\') -in @('payload\secrets\runtime-credential.json', 'payload\state\runtime-account.json')) {
            throw "Backup contains machine-bound runtime identity material that AbodeToWeb never restores: '$($entry.FullName)'."
        }
        $expandedBytes += $entry.Length
        if ($expandedBytes -gt $MaxExpandedBytes) { throw "Backup expands beyond the configured $MaxExpandedBytes byte safety limit." }
    }
} finally {
    $zip.Dispose()
}

$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage = Join-Path $tempParent "AbodeToWebRestore-$([guid]::NewGuid().ToString('N'))"
$rollback = Join-Path $tempParent "AbodeToWebRestoreRollback-$([guid]::NewGuid().ToString('N'))"
Assert-AbodeToWebSafeChildPath -Path $stage -Parent $tempParent | Out-Null
Assert-AbodeToWebSafeChildPath -Path $rollback -Parent $tempParent | Out-Null
New-Item -ItemType Directory -Path $stage, $rollback -Force | Out-Null
foreach ($temporaryRoot in @($stage, $rollback)) {
    Set-AbodeToWebPrivateAcl -Path $temporaryRoot -ServiceSid ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -ServiceAccess Modify
}
$runtimeLayout = Get-AbodeToWebRuntimeAccountLayout -DataRoot $layout.DataRoot -AccountName $RuntimeAccountName
$machineBoundRuntimeRoot = Join-Path $stage 'machine-bound-runtime'

$service = Get-AbodeToWebService -ServiceName $ServiceName
$wasActive = $null -ne $service -and $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
$tunnelService = $null
$tunnelWasActive = $false
$postRestoreAclError = $null
$restoredNames = [Collections.Generic.List[string]]::new()
$allowedNames = @('config', 'state', 'secrets', 'deployments', 'logs', 'cloudflared')
$normalizedProjectRoots = @()
$projectAclChanged = $false
$managedRootsPath = Join-Path $layout.ConfigRoot 'managed-project-roots.json'
$previousManagedRoots = @()
$previousManagedRuntimeSids = @()
$managedRootsPreviouslyExisted = Test-Path -LiteralPath $managedRootsPath -PathType Leaf
$managedRootsOriginalContent = $null
if ($managedRootsPreviouslyExisted) {
    $managedRootsOriginalContent = Get-Content -LiteralPath $managedRootsPath -Raw
    $managedRootState = $managedRootsOriginalContent | ConvertFrom-Json
    $rootsProperty = $managedRootState.PSObject.Properties['roots']
    if ($null -eq $rootsProperty) { throw "Managed project-root registry is invalid: '$managedRootsPath'." }
    $previousManagedRoots = @($rootsProperty.Value | ForEach-Object { [string]$_ })
    $runtimeSidProperty = $managedRootState.PSObject.Properties['runtimeAccountSid']
    if ($null -ne $runtimeSidProperty -and -not [string]::IsNullOrWhiteSpace([string]$runtimeSidProperty.Value)) {
        $previousManagedRuntimeSids = @([Security.Principal.SecurityIdentifier]::new([string]$runtimeSidProperty.Value).Value)
    }
}

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $stage)
    $metadataPath = Join-Path $stage 'backup.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw 'Backup metadata is missing.' }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.format -ne 'abode-to-web-backup' -or $metadata.schemaVersion -ne 1) { throw 'Unsupported or invalid Abode to Web backup format.' }

    foreach ($expected in $metadata.files) {
        $relative = ([string]$expected.path).Replace('/', '\')
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or ($relative.Split('\') -contains '..') -or
            -not $relative.StartsWith('payload\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Backup checksum entry is outside the payload: '$relative'."
        }
        $candidate = [IO.Path]::GetFullPath((Join-Path $stage $relative))
        Assert-AbodeToWebSafeChildPath -Path $candidate -Parent $stage | Out-Null
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Backup file is missing: '$relative'." }
        if ((Get-Item -LiteralPath $candidate).Length -ne [long]$expected.length) { throw "Length validation failed for '$relative'." }
        $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        if (-not $actual.Equals([string]$expected.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Checksum validation failed for '$relative'."
        }
    }

    $expectedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($expected in $metadata.files) {
        $expectedPath = ([string]$expected.path).Replace('\', '/')
        if (-not $expectedPaths.Add($expectedPath)) { throw "Backup metadata contains a duplicate payload path: '$expectedPath'." }
    }
    foreach ($actualFile in Get-ChildItem -LiteralPath (Join-Path $stage 'payload') -File -Recurse) {
        $actualRelative = [IO.Path]::GetRelativePath($stage, $actualFile.FullName).Replace('\', '/')
        if (-not $expectedPaths.Contains($actualRelative)) { throw "Backup contains an unchecksummed payload file: '$actualRelative'." }
    }

    $includedNames = @($metadata.included | ForEach-Object { [string]$_ })
    if ($includedNames.Count -eq 0) { throw 'Backup does not include any restorable data category.' }
    if (@($includedNames | Sort-Object -Unique).Count -ne $includedNames.Count) { throw 'Backup lists a data category more than once.' }
    foreach ($name in $includedNames) {
        if ($name -notin $allowedNames) { throw "Backup requests an unsupported data category: '$name'." }
        if (-not (Test-Path -LiteralPath (Join-Path $stage "payload\$name") -PathType Container)) {
            throw "Backup category '$name' is listed but missing."
        }
    }
    foreach ($payloadEntry in Get-ChildItem -LiteralPath (Join-Path $stage 'payload') -Force) {
        if (-not $payloadEntry.PSIsContainer -or $payloadEntry.Name -notin $includedNames) {
            throw "Backup payload contains an unlisted top-level entry: '$($payloadEntry.Name)'."
        }
    }

    New-Item -ItemType Directory -Path $machineBoundRuntimeRoot -Force | Out-Null
    Copy-Item -LiteralPath $runtimeLayout.CredentialPath -Destination (Join-Path $machineBoundRuntimeRoot 'runtime-credential.json') -Force
    Copy-Item -LiteralPath $runtimeLayout.MarkerPath -Destination (Join-Path $machineBoundRuntimeRoot 'runtime-account.json') -Force

    $configurationToValidate = if ('config' -in $includedNames) {
        Join-Path $stage 'payload\config\abode-to-web.yaml'
    } else {
        $layout.ConfigPath
    }
    if (-not (Test-Path -LiteralPath $configurationToValidate -PathType Leaf)) {
        throw "The restore would leave Abode to Web without a manifest at '$configurationToValidate'."
    }
    $validationText = (& $expectedServiceExecutable validate-config --path $configurationToValidate --json 2>&1 | Out-String).Trim()
    $validationExitCode = $LASTEXITCODE
    try { $validation = $validationText | ConvertFrom-Json } catch { throw "The installed Abode to Web validator returned an unreadable response. $validationText" }
    $validProperty = $validation.PSObject.Properties['valid']
    $rootsProperty = $validation.PSObject.Properties['projectsRoots']
    if ($validationExitCode -ne 0 -or $null -eq $validProperty -or -not [bool]$validProperty.Value -or $null -eq $rootsProperty) {
        $issuesProperty = $validation.PSObject.Properties['issues']
        $issueText = if ($null -eq $issuesProperty) { $validationText } else { @($issuesProperty.Value | ForEach-Object { "[$($_.code)] $($_.path): $($_.message)" }) -join [Environment]::NewLine }
        throw "The restored configuration is invalid; no ProgramData files were changed.$([Environment]::NewLine)$issueText"
    }
    $normalizedProjectRoots = @($rootsProperty.Value | ForEach-Object { Assert-AbodeToWebProjectRoot -Path ([string]$_) } | Sort-Object -Unique)

    if ('cloudflared' -in $includedNames) {
        $tunnelService = Get-AbodeToWebService -ServiceName $TunnelServiceName
        if ($null -ne $tunnelService) {
            $tunnelDefinition = Get-CimInstance Win32_Service -Filter "Name='$TunnelServiceName'" -ErrorAction SilentlyContinue
            $expectedTunnelConfig = [IO.Path]::GetFullPath((Join-Path $layout.CloudflareRoot 'config.yml'))
            $expectedTunnelArgument = '--config "' + $expectedTunnelConfig + '"'
            if ($null -eq $tunnelDefinition -or -not ([string]$tunnelDefinition.PathName).Contains($expectedTunnelArgument, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Service '$TunnelServiceName' does not use Abode to Web's tunnel configuration. Refusing to stop or modify an unrelated connector."
            }
            $tunnelWasActive = $tunnelService.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
        }
    }

    if (-not $SkipSafetyBackup -and (Test-Path -LiteralPath $layout.DataRoot)) {
        $safetyRoot = Join-Path $layout.BackupsRoot 'pre-restore'
        New-Item -ItemType Directory -Path $safetyRoot -Force | Out-Null
        $safetyParameters = @{ Destination = $safetyRoot; DataRoot = $layout.DataRoot; ServiceName = $ServiceName; TunnelServiceName = $TunnelServiceName }
        if ('secrets' -notin $includedNames) { $safetyParameters.ExcludeSecrets = $true }
        if ('deployments' -in $includedNames) { $safetyParameters.IncludeDeployments = $true }
        if ('logs' -in $includedNames) { $safetyParameters.IncludeLogs = $true }
        if ('cloudflared' -in $includedNames) { $safetyParameters.IncludeTunnelCredentials = $true }
        & (Join-Path $PSScriptRoot 'backup-abode-to-web.ps1') @safetyParameters | Out-Null
    }

    if ($tunnelWasActive) { Stop-AbodeToWebServiceIfRunning -ServiceName $TunnelServiceName -TimeoutSeconds 60 | Out-Null }
    if ($wasActive) { Stop-AbodeToWebServiceIfRunning -ServiceName $ServiceName -TimeoutSeconds 60 | Out-Null }
    New-Item -ItemType Directory -Path $layout.DataRoot -Force | Out-Null

    foreach ($name in $includedNames) {
        $target = Join-Path $layout.DataRoot $name
        $source = Join-Path $stage "payload\$name"
        Assert-AbodeToWebSafeChildPath -Path $target -Parent $layout.DataRoot | Out-Null
        Assert-AbodeToWebNotReparsePoint -Path $target
        if (Test-Path -LiteralPath $target) {
            Assert-AbodeToWebTreeHasNoReparsePoints -Path $target
            Copy-Item -LiteralPath $target -Destination $rollback -Recurse -Force
            $restoredNames.Add($name)
            Remove-Item -LiteralPath $target -Recurse -Force
        } else {
            $restoredNames.Add($name)
        }
        Copy-Item -LiteralPath $source -Destination $layout.DataRoot -Recurse -Force
    }

    # Runtime credentials are bound to this Windows machine and local account. Restore
    # the current machine's files after any state/secrets category replacement.
    New-Item -ItemType Directory -Path (Split-Path -Parent $runtimeLayout.CredentialPath), (Split-Path -Parent $runtimeLayout.MarkerPath) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $machineBoundRuntimeRoot 'runtime-credential.json') -Destination $runtimeLayout.CredentialPath -Force
    Copy-Item -LiteralPath (Join-Path $machineBoundRuntimeRoot 'runtime-account.json') -Destination $runtimeLayout.MarkerPath -Force
    Set-AbodeToWebPrivateAcl -Path (Split-Path -Parent $runtimeLayout.MarkerPath) `
        -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Modify
    Set-AbodeToWebRuntimeMarkerAcl -MarkerPath $runtimeLayout.MarkerPath

    # A backup may be restored onto a clean machine. Reconcile the external project-root
    # service-SID grants from the validated manifest instead of trusting stale ACL state.
    $projectAclChanged = $true
    $runtimeSid = [string]$runtimeAccountStatus.accountSid
    $staleRuntimeSids = @($previousManagedRuntimeSids | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    Set-AbodeToWebRuntimeDataAcl -DataRoot $layout.DataRoot -ServiceName $ServiceName -RuntimeAccountSid $runtimeSid -PreviousRuntimeAccountSid $staleRuntimeSids
    foreach ($previousRoot in $previousManagedRoots) {
        if ($previousRoot -in $normalizedProjectRoots -or -not (Test-Path -LiteralPath $previousRoot -PathType Container)) { continue }
        try {
            $previousFullPath = Assert-AbodeToWebProjectRoot -Path $previousRoot
        } catch {
            Write-Warning "A prior managed project-root grant was not modified: $($_.Exception.Message)"
            continue
        }
        Remove-AbodeToWebAclGrant -Path $previousFullPath -Identity "NT SERVICE\$ServiceName" | Out-Null
        foreach ($runtimeSidToRemove in (@(@($staleRuntimeSids) + @($runtimeSid)) | Sort-Object -Unique)) {
            Remove-AbodeToWebAclGrant -Path $previousFullPath -Identity "*$runtimeSidToRemove" | Out-Null
        }
    }
    foreach ($projectsFullPath in $normalizedProjectRoots) {
        foreach ($runtimeSidToRemove in (@(@($staleRuntimeSids) + @($runtimeSid)) | Sort-Object -Unique)) {
            Remove-AbodeToWebAclGrant -Path $projectsFullPath -Identity "*$runtimeSidToRemove" | Out-Null
        }
        Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\icacls.exe" -ArgumentList @($projectsFullPath, '/grant:r', "NT SERVICE\${ServiceName}:(OI)(CI)M") -ErrorMessage "Unable to grant Abode to Web access to '$projectsFullPath'"
    }
    New-Item -ItemType Directory -Path $layout.ConfigRoot -Force | Out-Null
    $managedRootsTemporary = Join-Path $layout.ConfigRoot "managed-project-roots-$([guid]::NewGuid().ToString('N')).tmp.json"
    try {
        [ordered]@{
            serviceName = $ServiceName
            runtimeAccountName = $RuntimeAccountName
            runtimeAccountSid = $runtimeSid
            roots = $normalizedProjectRoots
        } |
            ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath $managedRootsTemporary -Encoding utf8NoBOM
        [IO.File]::Move($managedRootsTemporary, $managedRootsPath, $true)
    } finally {
        if (Test-Path -LiteralPath $managedRootsTemporary) { Remove-Item -LiteralPath $managedRootsTemporary -Force }
    }

} catch {
    $restoreError = $_
    try {
        if ($projectAclChanged) {
            foreach ($currentRoot in $normalizedProjectRoots) {
                if ($currentRoot -notin $previousManagedRoots -and (Test-Path -LiteralPath $currentRoot -PathType Container)) {
                    Remove-AbodeToWebAclGrant -Path $currentRoot -Identity "NT SERVICE\$ServiceName" | Out-Null
                    Remove-AbodeToWebAclGrant -Path $currentRoot -Identity "*$runtimeSid" | Out-Null
                }
            }
            foreach ($previousRoot in $previousManagedRoots) {
                if (-not (Test-Path -LiteralPath $previousRoot -PathType Container)) { continue }
                try {
                    $previousFullPath = Assert-AbodeToWebProjectRoot -Path $previousRoot
                    Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\icacls.exe" -ArgumentList @($previousFullPath, '/grant:r', "NT SERVICE\${ServiceName}:(OI)(CI)M") -ErrorMessage "Unable to restore Abode to Web's prior managed grant on '$previousFullPath'"
                } catch {
                    Write-Warning "A prior project-root grant could not be restored: $($_.Exception.Message)"
                }
            }
        }
        foreach ($name in $restoredNames) {
            $target = Join-Path $layout.DataRoot $name
            Assert-AbodeToWebSafeChildPath -Path $target -Parent $layout.DataRoot | Out-Null
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
            $previous = Join-Path $rollback $name
            if (Test-Path -LiteralPath $previous) { Copy-Item -LiteralPath $previous -Destination $layout.DataRoot -Recurse -Force }
        }
        if ('config' -notin $restoredNames) {
            if ($managedRootsPreviouslyExisted) {
                $managedRootsOriginalContent | Set-Content -LiteralPath $managedRootsPath -Encoding utf8NoBOM -NoNewline
            } elseif (Test-Path -LiteralPath $managedRootsPath) {
                Remove-Item -LiteralPath $managedRootsPath -Force
            }
        }
    } catch {
        Write-Warning "Restore rollback was incomplete: $($_.Exception.Message)"
    }
    throw $restoreError
} finally {
    if ($restoredNames.Count -gt 0) {
        try {
            Set-AbodeToWebDataAcl -DataRoot $layout.DataRoot -ServiceName $ServiceName
            foreach ($protectedDataDirectory in @($layout.ConfigRoot, $layout.StateRoot, $layout.LogsRoot, $layout.BackupsRoot)) {
                if (Test-Path -LiteralPath $protectedDataDirectory -PathType Container) {
                    Set-AbodeToWebPrivateAcl -Path $protectedDataDirectory -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Modify
                }
            }
            if (Test-Path -LiteralPath $runtimeLayout.MarkerPath -PathType Leaf) {
                Set-AbodeToWebRuntimeMarkerAcl -MarkerPath $runtimeLayout.MarkerPath
            }
            if (Test-Path -LiteralPath $layout.SecretsRoot) {
                Repair-AbodeToWebApplicationSecretFileAcls -SecretsRoot $layout.SecretsRoot -ServiceName $ServiceName | Out-Null
                Set-AbodeToWebRuntimeCredentialAcl -CredentialPath $runtimeLayout.CredentialPath -ServiceName $ServiceName
            }
            Set-AbodeToWebRuntimeDataAcl -DataRoot $layout.DataRoot -ServiceName $ServiceName -RuntimeAccountSid $runtimeAccountStatus.accountSid `
                -PreviousRuntimeAccountSid $previousManagedRuntimeSids
            if ('cloudflared' -in $restoredNames -and (Test-Path -LiteralPath $layout.CloudflareRoot)) {
                if ($null -ne $tunnelService) {
                    Set-AbodeToWebPrivateAcl -Path $layout.CloudflareRoot -ServiceSid "NT SERVICE\$TunnelServiceName" -ServiceAccess Modify
                    $credentialRoot = Join-Path $layout.CloudflareRoot 'credentials'
                    if (Test-Path -LiteralPath $credentialRoot) {
                        Set-AbodeToWebPrivateAcl -Path $credentialRoot -ServiceSid "NT SERVICE\$TunnelServiceName" -ServiceAccess Read
                    }
                } else {
                    Set-AbodeToWebPrivateAcl -Path $layout.CloudflareRoot `
                        -ServiceSid ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -ServiceAccess Modify
                }
            }
        } catch {
            $postRestoreAclError = $_
            Write-Warning "Restore data was copied, but ACL repair failed: $($_.Exception.Message)"
        }
    }
    if ($wasActive -and $null -eq $postRestoreAclError) {
        try {
            Start-Service -Name $ServiceName
            Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds 60 | Out-Null
        } catch {
            Write-Warning "Restore finished, but Abode to Web could not be restarted: $($_.Exception.Message)"
        }
    } elseif ($wasActive -and $null -ne $postRestoreAclError) {
        Write-Warning "Abode to Web remains stopped because restored ACL verification failed. Run repair-abode-to-web.ps1 -Apply after resolving the reported ACL error, then start the service manually."
    }
    if ($tunnelWasActive -and $null -eq $postRestoreAclError) {
        try {
            Start-Service -Name $TunnelServiceName
            Wait-AbodeToWebServiceState -ServiceName $TunnelServiceName -State Running -TimeoutSeconds 60 | Out-Null
        } catch {
            Write-Warning "Restore finished, but the Cloudflare Tunnel service could not be restarted: $($_.Exception.Message)"
        }
    } elseif ($tunnelWasActive -and $null -ne $postRestoreAclError) {
        Write-Warning "The Cloudflare Tunnel service remains stopped because restored ACL verification failed. Repair and verify tunnel credential ACLs before restarting it."
    }
    foreach ($temporaryPath in @($stage, $rollback)) {
        if (Test-Path -LiteralPath $temporaryPath) {
            Assert-AbodeToWebSafeChildPath -Path $temporaryPath -Parent $tempParent | Out-Null
            Remove-Item -LiteralPath $temporaryPath -Recurse -Force
        }
    }
}

if ($null -ne $postRestoreAclError) { throw "Restore completed its file copy but failed to secure the restored data: $($postRestoreAclError.Exception.Message)" }
Write-Host "Restore completed from '$archive'. Restored: $($restoredNames -join ', ')."
