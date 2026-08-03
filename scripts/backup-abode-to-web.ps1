#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Destination,
    [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'AbodeToWeb'),
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$TunnelServiceName = 'cloudflared',
    [switch]$IncludeDeployments,
    [switch]$IncludeLogs,
    [switch]$IncludeTunnelCredentials,
    [switch]$ExcludeSecrets,
    [switch]$NoServiceStop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force

$layout = Get-AbodeToWebLayout -DataRoot $DataRoot
if (-not (Test-Path -LiteralPath $layout.DataRoot -PathType Container)) {
    throw "Abode to Web data root does not exist: '$($layout.DataRoot)'."
}
Assert-AbodeToWebNotReparsePoint -Path $layout.DataRoot

if (-not $PSBoundParameters.ContainsKey('Destination')) {
    New-Item -ItemType Directory -Path $layout.BackupsRoot -Force | Out-Null
    $Destination = Join-Path $layout.BackupsRoot "abode-to-web-$([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')).zip"
} elseif ([IO.Path]::GetExtension($Destination) -ne '.zip') {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $Destination = Join-Path $Destination "abode-to-web-$([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')).zip"
}
$destinationPath = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
if (Test-Path -LiteralPath $destinationPath) { throw "Backup already exists: '$destinationPath'." }

$service = Get-AbodeToWebService -ServiceName $ServiceName
if ($null -ne $service) {
    $expectedServiceExecutable = Join-Path ([IO.Path]::GetFullPath($InstallRoot)) 'AbodeToWeb.Server.exe'
    if (-not (Test-AbodeToWebServiceExecutable -ServiceName $ServiceName -ExecutablePath $expectedServiceExecutable)) {
        throw "Service '$ServiceName' does not run '$expectedServiceExecutable'. Refusing to quiesce an unrelated service."
    }
}
$wasActive = $null -ne $service -and $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
$tunnelService = if ($IncludeTunnelCredentials) { Get-AbodeToWebService -ServiceName $TunnelServiceName } else { $null }
if ($null -ne $tunnelService) {
    $tunnelDefinition = Get-CimInstance Win32_Service -Filter "Name='$TunnelServiceName'" -ErrorAction SilentlyContinue
    $expectedTunnelConfig = [IO.Path]::GetFullPath((Join-Path $layout.CloudflareRoot 'config.yml'))
    $expectedTunnelArgument = '--config "' + $expectedTunnelConfig + '"'
    if ($null -eq $tunnelDefinition -or -not ([string]$tunnelDefinition.PathName).Contains($expectedTunnelArgument, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Service '$TunnelServiceName' does not use Abode to Web's tunnel configuration. Refusing to stop an unrelated connector."
    }
}
$tunnelWasActive = $null -ne $tunnelService -and $tunnelService.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
$standardDataRoot = [IO.Path]::GetFullPath($layout.DataRoot).Equals(
    [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'AbodeToWeb')),
    [StringComparison]::OrdinalIgnoreCase)
$mustElevate = $standardDataRoot -or (($wasActive -or $tunnelWasActive) -and -not $NoServiceStop) -or (-not $ExcludeSecrets)
if ($mustElevate) {
    Assert-AbodeToWebAdministrator -Action 'Quiescing Abode to Web and reading its protected secret files for backup'
}
if (($wasActive -or $tunnelWasActive) -and $NoServiceStop) {
    Write-Warning 'Creating an online file backup. SQLite WAL files are included, but a quiesced backup is safer for disaster recovery.'
}

$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage = Join-Path $tempParent "AbodeToWebBackup-$([guid]::NewGuid().ToString('N'))"
Assert-AbodeToWebSafeChildPath -Path $stage -Parent $tempParent | Out-Null
$payload = Join-Path $stage 'payload'
$included = [Collections.Generic.List[string]]::new()
$backupCompleted = $false
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Set-AbodeToWebPrivateAcl -Path $stage -ServiceSid ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -ServiceAccess Modify

try {
    if ($tunnelWasActive -and -not $NoServiceStop) {
        Stop-AbodeToWebServiceIfRunning -ServiceName $TunnelServiceName -TimeoutSeconds 60 | Out-Null
    }
    if ($wasActive -and -not $NoServiceStop) {
        Stop-AbodeToWebServiceIfRunning -ServiceName $ServiceName -TimeoutSeconds 60 | Out-Null
    }
    New-Item -ItemType Directory -Path $payload -Force | Out-Null

    $directories = [Collections.Generic.List[string]]::new()
    $directories.Add('config')
    $directories.Add('state')
    if (-not $ExcludeSecrets) { $directories.Add('secrets') }
    if ($IncludeDeployments) { $directories.Add('deployments') }
    if ($IncludeLogs) { $directories.Add('logs') }

    foreach ($name in $directories) {
        $sourcePath = Join-Path $layout.DataRoot $name
        if (Test-Path -LiteralPath $sourcePath -PathType Container) {
            Assert-AbodeToWebTreeHasNoReparsePoints -Path $sourcePath
            Copy-Item -LiteralPath $sourcePath -Destination $payload -Recurse -Force
            $included.Add($name)
        }
    }

    if ($IncludeTunnelCredentials) {
        $tunnelSource = Join-Path $layout.DataRoot 'cloudflared'
        $tunnelDestination = Join-Path $payload 'cloudflared'
        $copiedTunnelMaterial = $false
        if (Test-Path -LiteralPath (Join-Path $tunnelSource 'config.yml') -PathType Leaf) {
            Assert-AbodeToWebNotReparsePoint -Path (Join-Path $tunnelSource 'config.yml')
            New-Item -ItemType Directory -Path $tunnelDestination -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $tunnelSource 'config.yml') -Destination $tunnelDestination -Force
            $copiedTunnelMaterial = $true
        }
        if (Test-Path -LiteralPath (Join-Path $tunnelSource 'credentials') -PathType Container) {
            Assert-AbodeToWebTreeHasNoReparsePoints -Path (Join-Path $tunnelSource 'credentials')
            New-Item -ItemType Directory -Path $tunnelDestination -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $tunnelSource 'credentials') -Destination $tunnelDestination -Recurse -Force
            $copiedTunnelMaterial = $true
        }
        if ($copiedTunnelMaterial) { $included.Add('cloudflared') }
    }

    # These files bind a local account password to one Windows machine. They are
    # deliberately preserved/regenerated by install/restore tooling, never exported.
    foreach ($machineBoundRelativePath in @(
        'secrets\runtime-credential.json',
        'state\runtime-account.json'
    )) {
        $machineBoundPath = Join-Path $payload $machineBoundRelativePath
        if (Test-Path -LiteralPath $machineBoundPath -PathType Leaf) { Remove-Item -LiteralPath $machineBoundPath -Force }
    }

    $hashes = foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
        [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); length = $file.Length }
    }
    $metadata = [ordered]@{
        format = 'abode-to-web-backup'
        schemaVersion = 1
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        host = $env:COMPUTERNAME
        included = @($included)
        machineBoundRuntimeCredentialExcluded = $true
        files = @($hashes)
    }
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'backup.json') -Encoding utf8NoBOM

    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $destinationPath -CompressionLevel Optimal
    $backupCompleted = $true
} finally {
    if ($wasActive -and -not $NoServiceStop) {
        try {
            Start-Service -Name $ServiceName
            Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds 60 | Out-Null
        } catch {
            Write-Warning "Backup completed, but Abode to Web could not be restarted: $($_.Exception.Message)"
        }
    }
    if ($tunnelWasActive -and -not $NoServiceStop) {
        try {
            Start-Service -Name $TunnelServiceName
            Wait-AbodeToWebServiceState -ServiceName $TunnelServiceName -State Running -TimeoutSeconds 60 | Out-Null
        } catch {
            Write-Warning "Backup completed, but the Cloudflare Tunnel service could not be restarted: $($_.Exception.Message)"
        }
    }
    if (Test-Path -LiteralPath $stage) {
        Assert-AbodeToWebSafeChildPath -Path $stage -Parent $tempParent | Out-Null
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    if (-not $backupCompleted -and (Test-Path -LiteralPath $destinationPath)) {
        Remove-Item -LiteralPath $destinationPath -Force
    }
}

if (-not $ExcludeSecrets -or $IncludeTunnelCredentials) {
    Write-Warning 'The backup can contain application or tunnel secrets. Store it like a password vault; ZIP content is not encrypted and does not preserve NTFS ACLs.'
} else {
    Write-Warning 'The ZIP is not encrypted and does not preserve NTFS ACLs. It excludes registered secret/tunnel files but can still contain private configuration and metadata.'
}
Write-Host 'The machine-bound AbodeToWebRuntime credential and ownership marker were excluded intentionally.'
Write-Host "Backup created and checksummed: $destinationPath"
Write-Output $destinationPath
