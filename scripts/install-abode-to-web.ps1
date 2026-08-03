#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PublishedRoot,
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'AbodeToWeb'),
    [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
    [string]$ConfigurationPath,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
    [ValidateSet('NT AUTHORITY\LocalService', 'NT AUTHORITY\NetworkService')]
    [string]$ServiceAccount = 'NT AUTHORITY\LocalService',
    [pscredential]$ServiceCredential,
    [string[]]$DashboardAdminSid,
    [string]$RuntimeAccountName = 'AbodeToWebRuntime',
    [string[]]$AllowedProjectsRoot,
    [uri]$DashboardUrl = 'http://127.0.0.1:4545',
    [switch]$PublishOnly,
    [switch]$SkipFrontendBuild,
    [switch]$ReplaceConfiguration,
    [switch]$RotateRuntimeCredential,
    [switch]$DesktopShortcut,
    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.RuntimeAccount.psm1') -Force

$source = [IO.Path]::GetFullPath($SourceRoot)
$serviceAccountWasSpecified = $PSBoundParameters.ContainsKey('ServiceAccount')
if ($null -ne $ServiceCredential -and $serviceAccountWasSpecified) {
    throw 'Use either -ServiceCredential or -ServiceAccount, not both.'
}
$layout = Get-AbodeToWebLayout -InstallRoot $InstallRoot -DataRoot $DataRoot
Assert-AbodeToWebStandardInstallRoot -Path $layout.InstallRoot | Out-Null
Assert-AbodeToWebStandardDataRoot -Path $layout.DataRoot | Out-Null
foreach ($knownSubdirectory in @(
    $layout.ConfigRoot, $layout.StateRoot, $layout.LogsRoot, $layout.DeploymentsRoot,
    $layout.SecretsRoot, $layout.BackupsRoot, $layout.CloudflareRoot
)) {
    Assert-AbodeToWebNotReparsePoint -Path $knownSubdirectory
}
if (Test-Path -LiteralPath $layout.ConfigRoot -PathType Container) { Assert-AbodeToWebTreeHasNoReparsePoints -Path $layout.ConfigRoot }
$DashboardUrl = Assert-AbodeToWebDashboardUri -Uri $DashboardUrl
$serverProject = Join-Path $source 'src\AbodeToWeb.Server\AbodeToWeb.Server.csproj'
$publishRoot = Join-Path $source 'artifacts\publish\win-x64'
$frontendDist = $null
$prebuiltRoot = if ($PSBoundParameters.ContainsKey('PublishedRoot')) {
    [IO.Path]::GetFullPath($PublishedRoot)
} else {
    $source
}
$prebuiltExecutablePresent = Test-Path -LiteralPath (Join-Path $prebuiltRoot 'AbodeToWeb.Server.exe') -PathType Leaf
$usePrebuilt = $PSBoundParameters.ContainsKey('PublishedRoot') -or ($prebuiltExecutablePresent -and -not (Test-Path -LiteralPath $serverProject -PathType Leaf))
if ($usePrebuilt -and -not $prebuiltExecutablePresent) { throw "Published root does not contain AbodeToWeb.Server.exe: '$prebuiltRoot'." }

if (-not $usePrebuilt -and -not (Test-Path -LiteralPath $serverProject -PathType Leaf)) {
    throw "AbodeToWeb server project was not found at '$serverProject'. Run this script from a complete source checkout."
}

if ($usePrebuilt) {
    $publishRoot = $prebuiltRoot
    Write-Host "Inspecting prebuilt release candidate at $publishRoot"
} else {
    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $dotnet = if ($null -eq $dotnetCommand) { $null } else { $dotnetCommand.Path }
    if ([string]::IsNullOrWhiteSpace($dotnet)) {
        $candidate = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $dotnet = $candidate }
    }
    if ([string]::IsNullOrWhiteSpace($dotnet)) {
        throw '.NET SDK 10 is required to create the self-contained release. Install it from https://dotnet.microsoft.com/download/dotnet/10.0, or install from a release archive containing AbodeToWeb.Server.exe.'
    }

    if (-not $SkipFrontendBuild) {
        $frontendRoot = Join-Path $source 'src\AbodeToWeb.Web'
        $packageJson = Join-Path $frontendRoot 'package.json'
        if (Test-Path -LiteralPath $packageJson -PathType Leaf) {
            $npmCommand = Get-Command npm.cmd -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            $npm = if ($null -eq $npmCommand) { $null } else { $npmCommand.Path }
            if ([string]::IsNullOrWhiteSpace($npm)) { throw 'Node.js/npm is required to build the Abode to Web dashboard.' }
            Push-Location $frontendRoot
            try {
                if (Test-Path -LiteralPath (Join-Path $frontendRoot 'package-lock.json')) {
                    Invoke-AbodeToWebNative -FilePath $npm -ArgumentList @('ci', '--no-audit') -ErrorMessage 'Frontend dependency installation failed'
                } else {
                    Invoke-AbodeToWebNative -FilePath $npm -ArgumentList @('install', '--no-audit') -ErrorMessage 'Frontend dependency installation failed'
                }
                Invoke-AbodeToWebNative -FilePath $npm -ArgumentList @('run', 'build') -ErrorMessage 'Frontend build failed'
                $frontendDist = Join-Path $frontendRoot 'dist'
                if (-not (Test-Path -LiteralPath $frontendDist -PathType Container)) { throw "Frontend build did not create '$frontendDist'." }
            } finally {
                Pop-Location
            }
        }
    }

    $artifactsRoot = Join-Path $source 'artifacts'
    Assert-AbodeToWebSafeChildPath -Path $publishRoot -Parent $artifactsRoot | Out-Null
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

    Invoke-AbodeToWebNative -FilePath $dotnet -ArgumentList @(
        'publish', $serverProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $publishRoot,
        '-p:SkipFrontendBuild=true',
        '-p:PublishReadyToRun=true',
        '-p:DebugType=embedded'
    ) -ErrorMessage 'Self-contained AbodeToWeb publish failed'

    if ($null -ne $frontendDist) {
        $publishedWebRoot = Join-Path $publishRoot 'wwwroot'
        if (Test-Path -LiteralPath $publishedWebRoot) {
            Assert-AbodeToWebSafeChildPath -Path $publishedWebRoot -Parent $publishRoot | Out-Null
            Remove-Item -LiteralPath $publishedWebRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $publishedWebRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $frontendDist '*') -Destination $publishedWebRoot -Recurse -Force
    }
}

$publishedExe = Join-Path $publishRoot 'AbodeToWeb.Server.exe'
if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
    throw "Publish completed without the expected executable '$publishedExe'."
}
$missingRuntimeFiles = @(@('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll') | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $publishRoot $_) -PathType Leaf)
})
if ($missingRuntimeFiles.Count -gt 0) {
    throw "The staged output is not a complete self-contained Windows release; missing: $($missingRuntimeFiles -join ', '). Use dotnet publish --runtime win-x64 --self-contained true or an official Abode to Web release ZIP."
}
Assert-AbodeToWebTreeHasNoReparsePoints -Path $publishRoot

Write-Host "Self-contained win-x64 release staged at $publishRoot"
if ($PublishOnly) { return }

$installConfiguration = $ReplaceConfiguration -or -not (Test-Path -LiteralPath $layout.ConfigPath -PathType Leaf)
if ($installConfiguration) {
    if (-not $PSBoundParameters.ContainsKey('ConfigurationPath')) {
        $primaryConfig = Join-Path $source 'config\abode-to-web.yaml'
        $exampleConfig = Join-Path $source 'config\abode-to-web.example.yaml'
        $ConfigurationPath = if (Test-Path -LiteralPath $primaryConfig) { $primaryConfig } else { $exampleConfig }
    }
    if (-not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) {
        throw "No AbodeToWeb configuration was found. Expected '$ConfigurationPath'."
    }
    Assert-AbodeToWebNotReparsePoint -Path $ConfigurationPath
    $configurationToValidate = [IO.Path]::GetFullPath($ConfigurationPath)
} else {
    $configurationToValidate = $layout.ConfigPath
}
$validationText = (& $publishedExe validate-config --path $configurationToValidate --json 2>&1 | Out-String).Trim()
$validationExitCode = $LASTEXITCODE
try { $validation = $validationText | ConvertFrom-Json } catch { throw "AbodeToWeb configuration validator returned an unreadable response; no machine changes were made. $validationText" }
$validProperty = $validation.PSObject.Properties['valid']
$rootsProperty = $validation.PSObject.Properties['projectsRoots']
if ($validationExitCode -ne 0 -or $null -eq $validProperty -or -not [bool]$validProperty.Value -or $null -eq $rootsProperty) {
    $issuesProperty = $validation.PSObject.Properties['issues']
    $issueText = if ($null -eq $issuesProperty) { $validationText } else { @($issuesProperty.Value | ForEach-Object { "[$($_.code)] $($_.path): $($_.message)" }) -join [Environment]::NewLine }
    throw "The configuration that AbodeToWeb would run is invalid; no machine changes were made.$([Environment]::NewLine)$issueText"
}
$manifestProjectRoots = @($rootsProperty.Value | ForEach-Object { [string]$_ })

Assert-AbodeToWebAdministrator -Action 'Installing Abode to Web binaries and Windows service'
Assert-AbodeToWebRuntimeAccountAvailable -DataRoot $layout.DataRoot -AccountName $RuntimeAccountName | Out-Null

$reconcileProjectRoots = $true
$normalizedProjectRoots = @($manifestProjectRoots | ForEach-Object { Assert-AbodeToWebProjectRoot -Path $_ } | Sort-Object -Unique)
$managedRootsPath = Join-Path $layout.ConfigRoot 'managed-project-roots.json'
$managedRootsPreviouslyExisted = Test-Path -LiteralPath $managedRootsPath -PathType Leaf
$previousManagedRootsBytes = if ($managedRootsPreviouslyExisted) { [IO.File]::ReadAllBytes($managedRootsPath) } else { $null }
$previousManagedRoots = @()
$previousManagedRuntimeSids = @()
if ($PSBoundParameters.ContainsKey('AllowedProjectsRoot')) {
    $confirmedProjectRoots = @($AllowedProjectsRoot | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        Assert-AbodeToWebProjectRoot -Path $_
    } | Sort-Object -Unique)
    if (($confirmedProjectRoots.Count -ne $normalizedProjectRoots.Count) -or (Compare-Object -ReferenceObject $normalizedProjectRoots -DifferenceObject $confirmedProjectRoots -SyncWindow 0)) {
        throw "-AllowedProjectsRoot must exactly match the validated manifest projectsRoots. Manifest: $($normalizedProjectRoots -join ', ')."
    }
}
if (Test-Path -LiteralPath $managedRootsPath -PathType Leaf) {
    $managedRootState = Get-Content -LiteralPath $managedRootsPath -Raw | ConvertFrom-Json
    $managedRootsProperty = $managedRootState.PSObject.Properties['roots']
    if ($null -eq $managedRootsProperty) { throw "Managed project-root registry is invalid: '$managedRootsPath'." }
    $previousManagedRoots = @($managedRootsProperty.Value | ForEach-Object { [string]$_ })
    $managedRuntimeSidProperty = $managedRootState.PSObject.Properties['runtimeAccountSid']
    if ($null -ne $managedRuntimeSidProperty -and -not [string]::IsNullOrWhiteSpace([string]$managedRuntimeSidProperty.Value)) {
        $previousManagedRuntimeSids = @([string]$managedRuntimeSidProperty.Value)
    }
}

$existingService = Get-AbodeToWebService -ServiceName $ServiceName
$expectedServiceExecutable = Join-Path $layout.InstallRoot 'AbodeToWeb.Server.exe'
if ($null -ne $existingService -and -not (Test-AbodeToWebServiceExecutable -ServiceName $ServiceName -ExecutablePath $expectedServiceExecutable)) {
    throw "Service '$ServiceName' already exists but does not run Abode to Web from '$expectedServiceExecutable'. Refusing to stop or repurpose it; choose another -ServiceName."
}
if ($null -ne $existingService -and $null -eq $ServiceCredential -and -not $serviceAccountWasSpecified) {
    $existingDefinition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
    if ([string]$existingDefinition.StartName -in @('LocalSystem', '.\LocalSystem')) {
        Write-Warning "Migrating the existing Abode to Web service from LocalSystem to the safer default identity '$ServiceAccount'. Pass -ServiceCredential or -ServiceAccount explicitly to choose another identity."
        $serviceAccountWasSpecified = $true
    }
}
$existingAdminSidSetting = if ($null -eq $existingService) {
    $null
} else {
    Get-AbodeToWebServiceEnvironmentVariable -ServiceName $ServiceName -Name 'ABODE_TO_WEB_ADMIN_SIDS'
}
$dashboardAdminSids = if ($PSBoundParameters.ContainsKey('DashboardAdminSid')) {
    @(ConvertTo-AbodeToWebWindowsSidList -Sid $DashboardAdminSid -RequireUserAccount)
} elseif (-not [string]::IsNullOrWhiteSpace($existingAdminSidSetting)) {
    @(ConvertTo-AbodeToWebWindowsSidList -Sid $existingAdminSidSetting -RequireUserAccount)
} else {
    @(Get-AbodeToWebCurrentUserSid)
}
$servicePreviouslyInstalled = $null -ne $existingService
$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$previousServiceEnvironmentProperty = if ($servicePreviouslyInstalled) {
    Get-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
} else {
    $null
}
$serviceEnvironmentPreviouslyExisted = $null -ne $previousServiceEnvironmentProperty
$previousServiceEnvironment = if ($serviceEnvironmentPreviouslyExisted) { @($previousServiceEnvironmentProperty.Environment) } else { @() }
$wasActive = $null -ne $existingService -and $existingService.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
$installRootPreviouslyExisted = Test-Path -LiteralPath $layout.InstallRoot -PathType Container
$configPreviouslyExisted = Test-Path -LiteralPath $layout.ConfigPath -PathType Leaf
$oldRoot = $null
$configBackupPath = $null
$runtimeAccountInitialization = $null
$projectAclReconciliationStarted = $false

try {
    if ($null -ne $existingService) {
        Stop-AbodeToWebServiceIfRunning -ServiceName $ServiceName -TimeoutSeconds 45 | Out-Null
    }

$installParent = Split-Path -Parent $layout.InstallRoot
$stagingRoot = "$($layout.InstallRoot).new-$([guid]::NewGuid().ToString('N'))"
$oldRoot = "$($layout.InstallRoot).old-$([guid]::NewGuid().ToString('N'))"
Assert-AbodeToWebSafeChildPath -Path $stagingRoot -Parent $installParent | Out-Null
Assert-AbodeToWebSafeChildPath -Path $oldRoot -Parent $installParent | Out-Null

try {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $publishRoot '*') -Destination $stagingRoot -Recurse -Force

    if (Test-Path -LiteralPath $layout.InstallRoot) {
        Assert-AbodeToWebTreeHasNoReparsePoints -Path $layout.InstallRoot
        Assert-AbodeToWebSafeChildPath -Path $layout.InstallRoot -Parent $installParent | Out-Null
        Move-Item -LiteralPath $layout.InstallRoot -Destination $oldRoot
    }
    Move-Item -LiteralPath $stagingRoot -Destination $layout.InstallRoot
} catch {
    if ((-not (Test-Path -LiteralPath $layout.InstallRoot)) -and (Test-Path -LiteralPath $oldRoot)) {
        Move-Item -LiteralPath $oldRoot -Destination $layout.InstallRoot
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Assert-AbodeToWebSafeChildPath -Path $stagingRoot -Parent $installParent | Out-Null
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

foreach ($directory in @(
    $layout.DataRoot, $layout.ConfigRoot, $layout.StateRoot, $layout.LogsRoot,
    $layout.DeploymentsRoot, $layout.SecretsRoot, $layout.BackupsRoot, $layout.CloudflareRoot
)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

if ($installConfiguration) {
    if ($configPreviouslyExisted) {
        $configBackupPath = Join-Path $layout.ConfigRoot "abode-to-web-$([guid]::NewGuid().ToString('N')).install-backup.yaml"
        Copy-Item -LiteralPath $layout.ConfigPath -Destination $configBackupPath -Force
    }
    $configTemporaryPath = Join-Path $layout.ConfigRoot "abode-to-web-$([guid]::NewGuid().ToString('N')).tmp.yaml"
    try {
        Copy-Item -LiteralPath $ConfigurationPath -Destination $configTemporaryPath -Force
        [IO.File]::Move($configTemporaryPath, $layout.ConfigPath, $true)
    } finally {
        if (Test-Path -LiteralPath $configTemporaryPath) { Remove-Item -LiteralPath $configTemporaryPath -Force }
    }
    Write-Host "Installed configuration at $($layout.ConfigPath)."
} else {
    Write-Host "Preserved existing configuration at $($layout.ConfigPath)."
}

$serviceExe = Join-Path $layout.InstallRoot 'AbodeToWeb.Server.exe'
$binaryPath = '"' + $serviceExe + '"'
$sc = Join-Path $env:SystemRoot 'System32\sc.exe'
if ($null -ne $ServiceCredential -and $null -eq $existingService) {
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'Abode to Web' -Description 'Personal Windows hosting control plane and application gateway.' -StartupType Automatic -Credential $ServiceCredential | Out-Null
    Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('config', $ServiceName, 'start=', 'delayed-auto') -ErrorMessage 'Unable to enable delayed automatic startup'
} elseif ($null -ne $ServiceCredential) {
    Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('config', $ServiceName, 'binPath=', $binaryPath, 'start=', 'delayed-auto') -ErrorMessage "Unable to update service '$ServiceName'"
    Set-Service -Name $ServiceName -Credential $ServiceCredential
} elseif ($null -eq $existingService) {
    Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @(
        'create', $ServiceName,
        'binPath=', $binaryPath,
        'start=', 'delayed-auto',
        'obj=', $ServiceAccount,
        'DisplayName=', 'Abode to Web'
    ) -ErrorMessage "Unable to create service '$ServiceName'"
} else {
    $configArguments = @('config', $ServiceName, 'binPath=', $binaryPath, 'start=', 'delayed-auto')
    if ($serviceAccountWasSpecified) { $configArguments += @('obj=', $ServiceAccount) }
    Invoke-AbodeToWebNative -FilePath $sc -ArgumentList $configArguments -ErrorMessage "Unable to update service '$ServiceName'"
}

Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('description', $ServiceName, 'Personal Windows hosting control plane and application gateway.') -ErrorMessage 'Unable to set service description'
Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('sidtype', $ServiceName, 'unrestricted') -ErrorMessage 'Unable to enable the per-service identity'
Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') -ErrorMessage 'Unable to configure service recovery'
Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('failureflag', $ServiceName, '1') -ErrorMessage 'Unable to enable recovery for non-crash failures'

Set-AbodeToWebDataAcl -DataRoot $layout.DataRoot -ServiceName $ServiceName
foreach ($protectedDataDirectory in @($layout.ConfigRoot, $layout.LogsRoot, $layout.BackupsRoot)) {
    Set-AbodeToWebPrivateAcl -Path $protectedDataDirectory -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Modify
}
$applicationSecretAclRepairs = @(Repair-AbodeToWebApplicationSecretFileAcls `
    -SecretsRoot $layout.SecretsRoot -ServiceName $ServiceName)
foreach ($secretRepair in $applicationSecretAclRepairs) {
    Write-Host "Protected application secret permissions: $($secretRepair.FileName)"
}
$runtimeAccountInitialization = Initialize-AbodeToWebRuntimeAccount -DataRoot $layout.DataRoot -ServiceName $ServiceName `
    -AccountName $RuntimeAccountName -RotateCredential:$RotateRuntimeCredential
$installedServiceDefinition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
$serviceIdentitySid = Resolve-AbodeToWebWindowsAccountSid -AccountName ([string]$installedServiceDefinition.StartName)
if ($serviceIdentitySid.Equals([string]$runtimeAccountInitialization.AccountSid, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Abode to Web control-plane service and project runtime identity resolve to the same Windows account. Use LocalService or a separate dedicated service account; AbodeToWebRuntime must remain distinct."
}
$staleRuntimeSids = @(
    @($previousManagedRuntimeSids) + @([string]$runtimeAccountInitialization.PreviousAccountSid) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
)
Set-AbodeToWebRuntimeDataAcl -DataRoot $layout.DataRoot -ServiceName $ServiceName -RuntimeAccountSid $runtimeAccountInitialization.AccountSid `
    -PreviousRuntimeAccountSid $staleRuntimeSids
New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value @(
    "ABODE_TO_WEB_DATA_ROOT=$($layout.DataRoot)",
    "ABODE_TO_WEB_CONFIG_PATH=$($layout.ConfigPath)",
    "ABODE_TO_WEB_ADMIN_SIDS=$($dashboardAdminSids -join ';')",
    "ABODE_TO_WEB_SERVICE_NAME=$ServiceName",
    "ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH=$($runtimeAccountInitialization.CredentialPath)",
    'DOTNET_ENVIRONMENT=Production'
) -Force | Out-Null

# Git refuses to open repositories owned by another Windows identity, and the
# service's per-invocation safe.directory opt-in does not reach the pack
# processes Git spawns for local-path fetch and push. Maintain explicit
# system-level trust for exactly the configured repositories and their
# local-path remotes; never the wildcard form.
$existingSafeDirectories = @(& git config --system --get-all safe.directory 2>$null)
$configuredRepositoryPaths = @(Select-String -LiteralPath $layout.ConfigPath -Pattern '^\s*localPath:\s*(.+)$' |
    ForEach-Object { $_.Matches[0].Groups[1].Value.Trim().Trim("'").Trim('"') })
$gitTrustTargets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($configuredRepositoryPath in $configuredRepositoryPaths) {
    if (-not (Test-Path -LiteralPath $configuredRepositoryPath -PathType Container)) { continue }
    $gitTrustTargets.Add(([IO.Path]::GetFullPath($configuredRepositoryPath) -replace '\\', '/')) | Out-Null
    $configuredRemoteUrl = (& git -c "safe.directory=$(([IO.Path]::GetFullPath($configuredRepositoryPath) -replace '\\', '/'))" -C $configuredRepositoryPath remote get-url origin 2>$null)
    if ($LASTEXITCODE -eq 0 -and $configuredRemoteUrl -and (Test-Path -LiteralPath $configuredRemoteUrl -PathType Container)) {
        $gitTrustTargets.Add(([IO.Path]::GetFullPath($configuredRemoteUrl) -replace '\\', '/')) | Out-Null
    }
}
foreach ($gitTrustTarget in $gitTrustTargets) {
    if ($existingSafeDirectories -notcontains $gitTrustTarget) {
        & git config --system --add safe.directory $gitTrustTarget
        Write-Output "Registered Git repository trust: $gitTrustTarget"
    }
}
if ($reconcileProjectRoots) {
    $projectAclReconciliationStarted = $true
    foreach ($previousRoot in $previousManagedRoots) {
        $previousFullPath = [IO.Path]::GetFullPath($previousRoot)
        if ($previousFullPath -notin $normalizedProjectRoots -and (Test-Path -LiteralPath $previousFullPath -PathType Container)) {
            $previousFullPath = Assert-AbodeToWebProjectRoot -Path $previousFullPath
            Remove-AbodeToWebAclGrant -Path $previousFullPath -Identity "NT SERVICE\$ServiceName" | Out-Null
            foreach ($runtimeSidToRemove in (@(@($staleRuntimeSids) + @([string]$runtimeAccountInitialization.AccountSid)) | Sort-Object -Unique)) {
                Remove-AbodeToWebAclGrant -Path $previousFullPath -Identity "*$runtimeSidToRemove" | Out-Null
            }
        }
    }
    foreach ($projectsFullPath in $normalizedProjectRoots) {
        foreach ($runtimeSidToRemove in (@(@($staleRuntimeSids) + @([string]$runtimeAccountInitialization.AccountSid)) | Sort-Object -Unique)) {
            Remove-AbodeToWebAclGrant -Path $projectsFullPath -Identity "*$runtimeSidToRemove" | Out-Null
        }
        Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\icacls.exe" -ArgumentList @($projectsFullPath, '/grant:r', "NT SERVICE\${ServiceName}:(OI)(CI)M") -ErrorMessage "Unable to grant Abode to Web access to '$projectsFullPath'"
    }
    $managedRootsTemporary = Join-Path $layout.ConfigRoot "managed-project-roots-$([guid]::NewGuid().ToString('N')).tmp.json"
    try {
        [ordered]@{
            serviceName = $ServiceName
            runtimeAccountName = $RuntimeAccountName
            runtimeAccountSid = $runtimeAccountInitialization.AccountSid
            roots = $normalizedProjectRoots
        } |
            ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath $managedRootsTemporary -Encoding utf8NoBOM
        [IO.File]::Move($managedRootsTemporary, $managedRootsPath, $true)
    } finally {
        if (Test-Path -LiteralPath $managedRootsTemporary) { Remove-Item -LiteralPath $managedRootsTemporary -Force }
    }
}
New-AbodeToWebDashboardShortcut -DashboardUrl $DashboardUrl | Out-Null
if ($DesktopShortcut) { New-AbodeToWebDashboardShortcut -DashboardUrl $DashboardUrl -Desktop | Out-Null }

if (-not $NoStart) {
    Start-Service -Name $ServiceName
    Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds 45 | Out-Null
    Write-Host "Abode to Web is running. Open $DashboardUrl"
} elseif ($wasActive) {
    Write-Warning 'Abode to Web was running before the update and is now stopped because -NoStart was supplied.'
} else {
    Write-Host "Abode to Web is installed but stopped. Run scripts/start-abode-to-web.ps1 from an elevated PowerShell 7 session."
}

Complete-AbodeToWebRuntimeAccountInitialization -InitializationResult $runtimeAccountInitialization

if ($null -ne $oldRoot -and (Test-Path -LiteralPath $oldRoot)) {
    try {
        Assert-AbodeToWebSafeChildPath -Path $oldRoot -Parent $installParent | Out-Null
        Remove-Item -LiteralPath $oldRoot -Recurse -Force
    } catch {
        Write-Warning "The update succeeded, but the previous binary directory could not be removed: $($_.Exception.Message)"
    }
}
if ($null -ne $configBackupPath -and (Test-Path -LiteralPath $configBackupPath)) {
    try { Remove-Item -LiteralPath $configBackupPath -Force } catch { Write-Warning "The update succeeded, but its temporary configuration backup could not be removed: $($_.Exception.Message)" }
}
Write-Warning 'No firewall, router, DNS, power-plan, or Cloudflare settings were changed.'
} catch {
    $installationError = $_
    Write-Warning 'Installation failed. Attempting to restore the previous binaries, configuration, and running state.'
    try {
        $currentService = Get-AbodeToWebService -ServiceName $ServiceName
        if ($null -ne $currentService -and $currentService.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-AbodeToWebServiceIfRunning -ServiceName $ServiceName -TimeoutSeconds 45 | Out-Null
        }

        if ($null -ne $oldRoot -and (Test-Path -LiteralPath $oldRoot)) {
            if (Test-Path -LiteralPath $layout.InstallRoot) {
                Assert-AbodeToWebStandardInstallRoot -Path $layout.InstallRoot | Out-Null
                Assert-AbodeToWebTreeHasNoReparsePoints -Path $layout.InstallRoot
                Remove-Item -LiteralPath $layout.InstallRoot -Recurse -Force
            }
            Move-Item -LiteralPath $oldRoot -Destination $layout.InstallRoot
        } elseif (-not $installRootPreviouslyExisted -and (Test-Path -LiteralPath $layout.InstallRoot)) {
            Assert-AbodeToWebStandardInstallRoot -Path $layout.InstallRoot | Out-Null
            Assert-AbodeToWebTreeHasNoReparsePoints -Path $layout.InstallRoot
            Remove-Item -LiteralPath $layout.InstallRoot -Recurse -Force
        }

        if ($null -ne $configBackupPath -and (Test-Path -LiteralPath $configBackupPath)) {
            [IO.File]::Move($configBackupPath, $layout.ConfigPath, $true)
        } elseif (-not $configPreviouslyExisted -and $installConfiguration -and (Test-Path -LiteralPath $layout.ConfigPath)) {
            Remove-Item -LiteralPath $layout.ConfigPath -Force
        }

        if ($null -ne $runtimeAccountInitialization) {
            if ([bool]$runtimeAccountInitialization.CreatedThisRun) {
                foreach ($projectsFullPath in $normalizedProjectRoots) {
                    if (Test-Path -LiteralPath $projectsFullPath -PathType Container) {
                        Remove-AbodeToWebAclGrant -Path $projectsFullPath -Identity "*$($runtimeAccountInitialization.AccountSid)" | Out-Null
                    }
                }
                Remove-AbodeToWebRuntimeDataAcl -DataRoot $layout.DataRoot -RuntimeAccountSid $runtimeAccountInitialization.AccountSid
                Remove-AbodeToWebRuntimeAccount -DataRoot $layout.DataRoot -AccountName $RuntimeAccountName | Out-Null
            } elseif (-not [string]::IsNullOrWhiteSpace([string]$runtimeAccountInitialization.RotationBackupPath)) {
                Restore-AbodeToWebRuntimeAccountRotation -InitializationResult $runtimeAccountInitialization -ServiceName $ServiceName | Out-Null
            }
        }

        if ($projectAclReconciliationStarted) {
            foreach ($newRoot in $normalizedProjectRoots) {
                if ($newRoot -notin $previousManagedRoots -and (Test-Path -LiteralPath $newRoot -PathType Container)) {
                    Remove-AbodeToWebAclGrant -Path $newRoot -Identity "NT SERVICE\$ServiceName" | Out-Null
                }
            }
            foreach ($previousRoot in $previousManagedRoots) {
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

        if ($managedRootsPreviouslyExisted) {
            [IO.File]::WriteAllBytes($managedRootsPath, $previousManagedRootsBytes)
        } elseif (Test-Path -LiteralPath $managedRootsPath -PathType Leaf) {
            Remove-Item -LiteralPath $managedRootsPath -Force
        }

        if ($servicePreviouslyInstalled -and (Test-Path -LiteralPath $serviceRegistryPath)) {
            if ($serviceEnvironmentPreviouslyExisted) {
                New-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $previousServiceEnvironment -Force | Out-Null
            } else {
                Remove-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
            }
        }

        if (-not $servicePreviouslyInstalled) {
            $newService = Get-AbodeToWebService -ServiceName $ServiceName
            if ($null -ne $newService) {
                Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\sc.exe" -ArgumentList @('delete', $ServiceName) -ErrorMessage "Unable to remove partially installed service '$ServiceName'"
            }
        } elseif ($wasActive) {
            Start-Service -Name $ServiceName
            Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds 45 | Out-Null
        }
    } catch {
        Write-Warning "Automatic recovery was incomplete: $($_.Exception.Message)"
    }
    throw $installationError
}
