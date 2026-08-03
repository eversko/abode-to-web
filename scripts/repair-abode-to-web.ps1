#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'AbodeToWeb'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
    [string[]]$DashboardAdminSid,
    [string]$RuntimeAccountName = 'AbodeToWebRuntime',
    [switch]$RotateRuntimeCredential,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.RuntimeAccount.psm1') -Force
if ($PSBoundParameters.ContainsKey('DashboardAdminSid') -and -not $Apply) {
    throw '-DashboardAdminSid changes service configuration and therefore requires -Apply.'
}
if ($RotateRuntimeCredential -and -not $Apply) {
    throw '-RotateRuntimeCredential changes the dedicated local account and therefore requires -Apply.'
}

$layout = Get-AbodeToWebLayout -InstallRoot $InstallRoot -DataRoot $DataRoot
$service = Get-AbodeToWebService -ServiceName $ServiceName
$serviceDefinition = if ($null -eq $service) { $null } else { Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue }
$issues = [Collections.Generic.List[string]]::new()
$recommendations = [Collections.Generic.List[string]]::new()
$exe = Join-Path $layout.InstallRoot 'AbodeToWeb.Server.exe'
$existingAdminSidSetting = if ($null -eq $service) {
    $null
} else {
    Get-AbodeToWebServiceEnvironmentVariable -ServiceName $ServiceName -Name 'ABODE_TO_WEB_ADMIN_SIDS'
}
$existingAdminSids = @()
if ($null -ne $service) {
    if ([string]::IsNullOrWhiteSpace($existingAdminSidSetting)) {
        $issues.Add('The service is missing ABODE_TO_WEB_ADMIN_SIDS, so a LocalService-hosted dashboard cannot authorize the intended Windows user.')
    } else {
        try { $existingAdminSids = @(ConvertTo-AbodeToWebWindowsSidList -Sid $existingAdminSidSetting -RequireUserAccount) }
        catch { $issues.Add("The service has an invalid ABODE_TO_WEB_ADMIN_SIDS value: $($_.Exception.Message)") }
    }
}

if ($null -eq $service) { $issues.Add("Windows service '$ServiceName' is not installed.") }
if ($null -ne $serviceDefinition -and [string]$serviceDefinition.StartName -in @('LocalSystem', '.\LocalSystem')) {
    $issues.Add("Windows service '$ServiceName' still runs as LocalSystem. Repair will migrate it to LocalService; hosted commands use the separate AbodeToWebRuntime account.")
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { $issues.Add("Server executable is missing at '$exe'.") }
if (-not (Test-Path -LiteralPath $layout.ConfigPath -PathType Leaf)) { $issues.Add("Configuration is missing at '$($layout.ConfigPath)'.") }

foreach ($directory in @($layout.DataRoot, $layout.StateRoot, $layout.LogsRoot, $layout.DeploymentsRoot, $layout.SecretsRoot, $layout.BackupsRoot)) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { $issues.Add("Required directory is missing: '$directory'.") }
}
$runtimeAccountStatus = Test-AbodeToWebRuntimeAccount -DataRoot $layout.DataRoot -ServiceName $ServiceName -AccountName $RuntimeAccountName
foreach ($runtimeIssue in $runtimeAccountStatus.issues) { $issues.Add("Runtime identity: $runtimeIssue") }
if ($null -ne $serviceDefinition -and -not [string]::IsNullOrWhiteSpace([string]$runtimeAccountStatus.accountSid)) {
    try {
        $controlPlaneSid = Resolve-AbodeToWebWindowsAccountSid -AccountName ([string]$serviceDefinition.StartName)
        if ($controlPlaneSid.Equals([string]$runtimeAccountStatus.accountSid, [StringComparison]::OrdinalIgnoreCase)) {
            $issues.Add('The control-plane service and project runtime identity are the same Windows account. They must be distinct.')
        }
    } catch { $issues.Add("The control-plane service account could not be verified: $($_.Exception.Message)") }
}

$listeners = Get-NetTCPConnection -State Listen -LocalPort 4545, 4580 -ErrorAction SilentlyContinue
foreach ($listener in $listeners) {
    $owner = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    if ($null -ne $owner -and $owner.ProcessName -ne 'AbodeToWeb.Server') {
        $issues.Add("Port $($listener.LocalPort) is occupied by $($owner.ProcessName) (PID $($owner.Id)). Abode to Web will not terminate it automatically.")
    }
}

$dashboard = Test-AbodeToWebHttpEndpoint -Uri 'http://127.0.0.1:4545/health' -TimeoutSeconds 3 -UseDefaultCredentials
$gateway = Test-AbodeToWebHttpEndpoint -Uri 'http://127.0.0.1:4580/__abode-to-web/gateway/health' -TimeoutSeconds 3
if ($null -ne $service -and $service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running -and -not $dashboard.Reachable) {
    $issues.Add('The Abode to Web service is running but the dashboard health endpoint is unreachable. Review Abode to Web system logs.')
}

$powerOutput = & "$env:SystemRoot\System32\powercfg.exe" /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 2>$null
if ($LASTEXITCODE -eq 0 -and ($powerOutput -join "`n") -match 'Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)') {
    $sleepSeconds = [Convert]::ToInt64($Matches[1], 16)
    if ($sleepSeconds -gt 0) {
        $recommendations.Add("This PC is configured to sleep on AC power after $([math]::Round($sleepSeconds / 60, 0)) minutes. Public apps and the tunnel are unavailable while it sleeps. Change this manually only if continuous availability matters.")
    }
}

$drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($layout.DataRoot).TrimEnd(':\')) -PSProvider FileSystem -ErrorAction SilentlyContinue
if ($null -ne $drive -and $drive.Free -lt 10GB) {
    $recommendations.Add("The Abode to Web data drive has only $([math]::Round($drive.Free / 1GB, 1)) GB free. Remove old deployments/logs through Abode to Web or expand storage.")
}

if ($Apply) {
    Assert-AbodeToWebAdministrator -Action 'Repairing Abode to Web service settings and directory permissions'
    Assert-AbodeToWebStandardInstallRoot -Path $layout.InstallRoot | Out-Null
    Assert-AbodeToWebStandardDataRoot -Path $layout.DataRoot | Out-Null
    if ($null -ne $service -and -not (Test-AbodeToWebServiceExecutable -ServiceName $ServiceName -ExecutablePath $exe)) {
        throw "Service '$ServiceName' does not run '$exe'. Refusing to modify an unrelated service."
    }
    if ($null -eq $service -or -not (Test-Path -LiteralPath $exe)) {
        throw 'Repair cannot recreate missing binaries or a missing service. Run scripts/install-abode-to-web.ps1 from a complete source checkout.'
    }
    if (-not (Test-Path -LiteralPath $layout.ConfigPath)) {
        throw 'Repair will not invent or overwrite desired configuration. Restore a backup or reinstall with a reviewed configuration.'
    }
    $dashboardAdminSids = if ($PSBoundParameters.ContainsKey('DashboardAdminSid')) {
        @(ConvertTo-AbodeToWebWindowsSidList -Sid $DashboardAdminSid -RequireUserAccount)
    } elseif ($existingAdminSids.Count -gt 0) {
        @($existingAdminSids)
    } else {
        @(Get-AbodeToWebCurrentUserSid)
    }

    $validationText = (& $exe validate-config --path $layout.ConfigPath --json 2>&1 | Out-String).Trim()
    $validationExitCode = $LASTEXITCODE
    try { $validation = $validationText | ConvertFrom-Json }
    catch { throw "Installed AbodeToWeb returned an unreadable configuration-validation response. No repair changes were made. $validationText" }
    $validProperty = $validation.PSObject.Properties['valid']
    $rootsProperty = $validation.PSObject.Properties['projectsRoots']
    if ($validationExitCode -ne 0 -or $null -eq $validProperty -or -not [bool]$validProperty.Value -or $null -eq $rootsProperty) {
        throw 'Repair will not grant project-root access for an invalid manifest. Correct or restore the configuration, then retry.'
    }
    $projectRoots = @($rootsProperty.Value | ForEach-Object { Assert-AbodeToWebProjectRoot -Path ([string]$_) } | Sort-Object -Unique)
    $managedRootsPath = Join-Path $layout.ConfigRoot 'managed-project-roots.json'
    $managedRootsPreviouslyExisted = Test-Path -LiteralPath $managedRootsPath -PathType Leaf
    $managedRootsBytes = if ($managedRootsPreviouslyExisted) { [IO.File]::ReadAllBytes($managedRootsPath) } else { $null }
    $previousRoots = @()
    $previousRuntimeSids = @()
    if ($managedRootsPreviouslyExisted) {
        $managedState = Get-Content -LiteralPath $managedRootsPath -Raw | ConvertFrom-Json
        if ($null -eq $managedState.PSObject.Properties['roots']) { throw "Managed project-root registry is invalid: '$managedRootsPath'." }
        $previousRoots = @($managedState.roots | ForEach-Object { [string]$_ })
        if ($null -ne $managedState.PSObject.Properties['runtimeAccountSid'] -and -not [string]::IsNullOrWhiteSpace([string]$managedState.runtimeAccountSid)) {
            $previousRuntimeSids = @([Security.Principal.SecurityIdentifier]::new([string]$managedState.runtimeAccountSid).Value)
        }
    }
    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $previousEnvironmentProperty = Get-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
    $environmentPreviouslyExisted = $null -ne $previousEnvironmentProperty
    $previousEnvironment = if ($environmentPreviouslyExisted) { @($previousEnvironmentProperty.Environment) } else { @() }
    $wasRunning = $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
    $runtimeInitialization = $null
    $projectAclReconciliationStarted = $false

    if ($PSCmdlet.ShouldProcess($ServiceName, 'Restore service settings, dedicated runtime identity, ACLs, dashboard administrator SIDs, and shortcut')) {
        try {
            if ($wasRunning) { Stop-AbodeToWebServiceIfRunning -ServiceName $ServiceName -TimeoutSeconds 60 | Out-Null }
            foreach ($directory in @($layout.DataRoot, $layout.ConfigRoot, $layout.StateRoot, $layout.LogsRoot, $layout.DeploymentsRoot, $layout.SecretsRoot, $layout.BackupsRoot, $layout.CloudflareRoot)) {
                Assert-AbodeToWebNotReparsePoint -Path $directory
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
            }
            $sc = "$env:SystemRoot\System32\sc.exe"
            Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('config', $ServiceName, 'start=', 'delayed-auto') -ErrorMessage 'Unable to restore automatic startup'
            if ($null -ne $serviceDefinition -and [string]$serviceDefinition.StartName -in @('LocalSystem', '.\LocalSystem')) {
                Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('config', $ServiceName, 'obj=', 'NT AUTHORITY\LocalService') -ErrorMessage 'Unable to migrate Abode to Web from LocalSystem to LocalService'
            }
            Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('sidtype', $ServiceName, 'unrestricted') -ErrorMessage 'Unable to restore the per-service SID'
            Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') -ErrorMessage 'Unable to restore service recovery'
            Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('failureflag', $ServiceName, '1') -ErrorMessage 'Unable to restore failure actions'
            Set-AbodeToWebDataAcl -DataRoot $layout.DataRoot -ServiceName $ServiceName
            foreach ($protectedDataDirectory in @($layout.ConfigRoot, $layout.LogsRoot, $layout.BackupsRoot)) {
                Set-AbodeToWebPrivateAcl -Path $protectedDataDirectory -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Modify
            }
            $applicationSecretAclRepairs = @(Repair-AbodeToWebApplicationSecretFileAcls `
                -SecretsRoot $layout.SecretsRoot -ServiceName $ServiceName)
            foreach ($secretRepair in $applicationSecretAclRepairs) {
                Write-Host "Repaired application secret permissions: $($secretRepair.FileName)"
            }
            $runtimeInitialization = Initialize-AbodeToWebRuntimeAccount -DataRoot $layout.DataRoot -ServiceName $ServiceName `
                -AccountName $RuntimeAccountName -RotateCredential:$RotateRuntimeCredential
            $repairedServiceDefinition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
            $controlPlaneSid = Resolve-AbodeToWebWindowsAccountSid -AccountName ([string]$repairedServiceDefinition.StartName)
            if ($controlPlaneSid.Equals([string]$runtimeInitialization.AccountSid, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The Abode to Web control-plane service and project runtime identity resolve to the same Windows account. Configure a distinct service identity before repair.'
            }
            $staleRuntimeSids = @(
                @($previousRuntimeSids) + @([string]$runtimeInitialization.PreviousAccountSid) |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    Sort-Object -Unique
            )
            Set-AbodeToWebRuntimeDataAcl -DataRoot $layout.DataRoot -ServiceName $ServiceName -RuntimeAccountSid $runtimeInitialization.AccountSid `
                -PreviousRuntimeAccountSid $staleRuntimeSids

            $projectAclReconciliationStarted = $true
            foreach ($previousRoot in $previousRoots) {
                if ($previousRoot -in $projectRoots -or -not (Test-Path -LiteralPath $previousRoot -PathType Container)) { continue }
                $previousFullPath = Assert-AbodeToWebProjectRoot -Path $previousRoot
                Remove-AbodeToWebAclGrant -Path $previousFullPath -Identity "NT SERVICE\$ServiceName" | Out-Null
                foreach ($runtimeSid in (@(@($staleRuntimeSids) + @([string]$runtimeInitialization.AccountSid)) | Sort-Object -Unique)) {
                    Remove-AbodeToWebAclGrant -Path $previousFullPath -Identity "*$runtimeSid" | Out-Null
                }
            }
            foreach ($projectRoot in $projectRoots) {
                foreach ($runtimeSid in (@(@($staleRuntimeSids) + @([string]$runtimeInitialization.AccountSid)) | Sort-Object -Unique)) {
                    Remove-AbodeToWebAclGrant -Path $projectRoot -Identity "*$runtimeSid" | Out-Null
                }
                Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\icacls.exe" -ArgumentList @($projectRoot, '/grant:r', "NT SERVICE\${ServiceName}:(OI)(CI)M") -ErrorMessage "Unable to restore Abode to Web access to '$projectRoot'"
            }
            $managedTemporary = Join-Path $layout.ConfigRoot "managed-project-roots-$([guid]::NewGuid().ToString('N')).tmp.json"
            try {
                [ordered]@{
                    serviceName = $ServiceName
                    runtimeAccountName = $RuntimeAccountName
                    runtimeAccountSid = $runtimeInitialization.AccountSid
                    roots = $projectRoots
                } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $managedTemporary -Encoding utf8NoBOM
                [IO.File]::Move($managedTemporary, $managedRootsPath, $true)
            } finally {
                if (Test-Path -LiteralPath $managedTemporary) { Remove-Item -LiteralPath $managedTemporary -Force }
            }
            New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value @(
                "ABODE_TO_WEB_DATA_ROOT=$($layout.DataRoot)",
                "ABODE_TO_WEB_CONFIG_PATH=$($layout.ConfigPath)",
                "ABODE_TO_WEB_ADMIN_SIDS=$($dashboardAdminSids -join ';')",
                "ABODE_TO_WEB_SERVICE_NAME=$ServiceName",
                "ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH=$($runtimeInitialization.CredentialPath)",
                'DOTNET_ENVIRONMENT=Production'
            ) -Force | Out-Null
            New-AbodeToWebDashboardShortcut | Out-Null
            if ($wasRunning) {
                Start-Service -Name $ServiceName
                Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds 60 | Out-Null
            }
            Complete-AbodeToWebRuntimeAccountInitialization -InitializationResult $runtimeInitialization
            Write-Host 'Repairable settings and the dedicated runtime identity were restored.'
        } catch {
            $repairFailure = $_
            try {
                if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
                    Stop-AbodeToWebServiceIfRunning -ServiceName $ServiceName -TimeoutSeconds 60 | Out-Null
                }
                if ($null -ne $runtimeInitialization) {
                    if ([bool]$runtimeInitialization.CreatedThisRun) {
                        foreach ($projectRoot in $projectRoots) {
                            if (Test-Path -LiteralPath $projectRoot -PathType Container) {
                                Remove-AbodeToWebAclGrant -Path $projectRoot -Identity "*$($runtimeInitialization.AccountSid)" | Out-Null
                            }
                        }
                        Remove-AbodeToWebRuntimeDataAcl -DataRoot $layout.DataRoot -RuntimeAccountSid $runtimeInitialization.AccountSid
                        Remove-AbodeToWebRuntimeAccount -DataRoot $layout.DataRoot -AccountName $RuntimeAccountName | Out-Null
                    } elseif (-not [string]::IsNullOrWhiteSpace([string]$runtimeInitialization.RotationBackupPath)) {
                        Restore-AbodeToWebRuntimeAccountRotation -InitializationResult $runtimeInitialization -ServiceName $ServiceName | Out-Null
                    }
                }
                if ($projectAclReconciliationStarted) {
                    foreach ($newRoot in $projectRoots) {
                        if ($newRoot -notin $previousRoots -and (Test-Path -LiteralPath $newRoot -PathType Container)) {
                            Remove-AbodeToWebAclGrant -Path $newRoot -Identity "NT SERVICE\$ServiceName" | Out-Null
                        }
                    }
                    foreach ($previousRoot in $previousRoots) {
                        if (-not (Test-Path -LiteralPath $previousRoot -PathType Container)) { continue }
                        try {
                            $previousFullPath = Assert-AbodeToWebProjectRoot -Path $previousRoot
                            Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\icacls.exe" -ArgumentList @(
                                $previousFullPath, '/grant:r', "NT SERVICE\${ServiceName}:(OI)(CI)M"
                            ) -ErrorMessage "Unable to restore Abode to Web's prior managed grant on '$previousFullPath'"
                        } catch {
                            Write-Warning "A prior project-root grant could not be restored: $($_.Exception.Message)"
                        }
                    }
                }
                if ($managedRootsPreviouslyExisted) { [IO.File]::WriteAllBytes($managedRootsPath, $managedRootsBytes) }
                elseif (Test-Path -LiteralPath $managedRootsPath) { Remove-Item -LiteralPath $managedRootsPath -Force }
                if ($environmentPreviouslyExisted) {
                    New-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $previousEnvironment -Force | Out-Null
                } else {
                    Remove-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
                }
                if ($wasRunning -and (Get-Service -Name $ServiceName).Status -ne [ServiceProcess.ServiceControllerStatus]::Running) {
                    Start-Service -Name $ServiceName
                    Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds 60 | Out-Null
                }
            } catch { Write-Warning "Repair rollback was incomplete: $($_.Exception.Message)" }
            throw $repairFailure
        }
    }
}

Write-Host "Service: $(if ($null -eq $service) { 'Not installed' } else { $service.Status })"
Write-Host "Dashboard health: $(if ($dashboard.Reachable) { "HTTP $($dashboard.StatusCode)" } else { 'Unreachable' })"
Write-Host "Gateway health: $(if ($gateway.Reachable) { "HTTP $($gateway.StatusCode)" } else { 'Unreachable' })"
Write-Host "Runtime identity: $(if ($runtimeAccountStatus.verification -eq 'limited') { 'Limited check; rerun elevated for marker, credential, and ACL verification' } elseif ($runtimeAccountStatus.healthy) { 'Healthy' } else { 'Needs repair' }) (process-logon self-test: $($runtimeAccountStatus.processLogonSelfTest))"
if ($issues.Count -eq 0) { Write-Host 'No structural problems detected.' } else { $issues | ForEach-Object { Write-Warning $_ } }
if ($recommendations.Count -gt 0) {
    Write-Host 'Availability recommendations (no changes were made):'
    $recommendations | ForEach-Object { Write-Host " - $_" }
}
