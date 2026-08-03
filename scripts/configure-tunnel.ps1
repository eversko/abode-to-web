#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$')][string]$TunnelName,
    [string]$TunnelId,
    [string]$ExecutablePath,
    [string]$CredentialsFile,
    [string[]]$Hostname,
    [string[]]$DnsHostname,
    [uri]$RegistryUri = 'http://127.0.0.1:4545/api/v1/projects/export',
    [string]$ManifestPath = (Join-Path $env:ProgramData 'AbodeToWeb\config\abode-to-web.yaml'),
    [uri]$GatewayUri = 'http://127.0.0.1:4580',
    [string]$ConfigPath = (Join-Path $env:ProgramData 'AbodeToWeb\cloudflared\config.yml'),
    [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'cloudflared',
    [switch]$CreateTunnel,
    [switch]$CreateDnsRoutes,
    [switch]$InstallService,
    [switch]$ReplaceExistingService,
    [switch]$RestartService,
    [switch]$AllowEmptyIngress,
    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Cloudflared.Common.psm1') -Force

if ($GatewayUri.Scheme -ne 'http' -or $GatewayUri.Host -ne '127.0.0.1' -or $GatewayUri.Port -ne 4580 -or $GatewayUri.AbsolutePath -ne '/' -or -not [string]::IsNullOrEmpty($GatewayUri.Query) -or -not [string]::IsNullOrEmpty($GatewayUri.Fragment) -or -not [string]::IsNullOrEmpty($GatewayUri.UserInfo)) {
    throw '-GatewayUri is pinned to http://127.0.0.1:4580. AbodeToWeb never tunnels the dashboard listener, a LAN address, or another loopback service.'
}
if ($InstallService -and $RestartService) {
    throw 'Choose either -InstallService or -RestartService, not both.'
}
if ($ReplaceExistingService -and -not $InstallService) {
    throw '-ReplaceExistingService is valid only with -InstallService.'
}
if ($null -ne $DnsHostname -and $DnsHostname.Count -gt 0 -and -not $CreateDnsRoutes) {
    throw '-DnsHostname is valid only with -CreateDnsRoutes.'
}
$cloudflared = Get-AbodeToWebCloudflaredExecutable -ExecutablePath $ExecutablePath
$configFullPath = [IO.Path]::GetFullPath($ConfigPath)
$configRoot = Split-Path -Parent $configFullPath
$configExtension = [IO.Path]::GetExtension($configFullPath)
if ($configExtension -notin @('.yml', '.yaml')) { throw '-ConfigPath must end in .yml or .yaml.' }
$programDataRoot = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
$configIsMachineWide = $configFullPath.StartsWith("$programDataRoot\", [StringComparison]::OrdinalIgnoreCase)
if ($InstallService) {
    $expectedConfig = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'AbodeToWeb\cloudflared\config.yml'))
    if (-not $configFullPath.Equals($expectedConfig, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The tunnel service configuration is restricted to '$expectedConfig'. Generate a non-service preview elsewhere without -InstallService."
    }
}
if ($configIsMachineWide -or $InstallService -or $RestartService) {
    Assert-AbodeToWebAdministrator -Action 'Writing machine-wide tunnel configuration or controlling its Windows service'
}
Assert-AbodeToWebNotReparsePoint -Path $configRoot
if (Test-Path -LiteralPath $configRoot -PathType Container) { Assert-AbodeToWebTreeHasNoReparsePoints -Path $configRoot }

# Establish ownership before creating a tunnel, changing DNS, or replacing files. This avoids
# making account-level changes and only then discovering that the requested service name belongs
# to an unrelated cloudflared installation.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$existingServiceDefinition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
$expectedConfigArgument = '--config "' + $configFullPath + '"'
$ownedByThisConfig = $null -ne $existingServiceDefinition -and
    ([string]$existingServiceDefinition.PathName).Contains($expectedConfigArgument, [StringComparison]::OrdinalIgnoreCase)
if ($InstallService -and $null -ne $existingServiceDefinition -and -not $ownedByThisConfig -and -not $ReplaceExistingService) {
    throw "Service '$ServiceName' already exists with a different command line. Refusing to repurpose it. Choose another -ServiceName or explicitly pass -ReplaceExistingService after reviewing that service."
}
if ($RestartService) {
    if ($null -eq $service -or $null -eq $existingServiceDefinition) {
        throw "Tunnel service '$ServiceName' is not installed. Use -InstallService after reviewing the generated configuration."
    }
    if (-not $ownedByThisConfig) {
        throw "Service '$ServiceName' does not use '$configFullPath'. Refusing to restart an unrelated service."
    }
}

if ([string]::IsNullOrWhiteSpace($TunnelId)) {
    $list = Invoke-AbodeToWebCapturedProcess -FilePath $cloudflared -ArgumentList @('tunnel', 'list', '--output', 'json') -TimeoutSeconds 30
    try { $tunnels = @($list.StandardOutput | ConvertFrom-Json) } catch { throw 'Unable to parse the tunnel list. Run cloudflared tunnel login as your normal user, then retry.' }
    $matches = @($tunnels | Where-Object { $_.PSObject.Properties['name'] -and [string]$_.name -eq $TunnelName })
    if ($matches.Count -eq 0 -and $CreateTunnel) {
        Write-Host "Creating locally managed tunnel '$TunnelName'. This changes your authenticated Cloudflare account but does not create DNS routes."
        Invoke-AbodeToWebCapturedProcess -FilePath $cloudflared -ArgumentList @('tunnel', 'create', $TunnelName) -TimeoutSeconds 60 | Out-Null
        $list = Invoke-AbodeToWebCapturedProcess -FilePath $cloudflared -ArgumentList @('tunnel', 'list', '--output', 'json') -TimeoutSeconds 30
        $tunnels = @($list.StandardOutput | ConvertFrom-Json)
        $matches = @($tunnels | Where-Object { $_.PSObject.Properties['name'] -and [string]$_.name -eq $TunnelName })
    }
    if ($matches.Count -eq 0) {
        throw "Tunnel '$TunnelName' was not found. Run cloudflared tunnel login and cloudflared tunnel create '$TunnelName', or rerun with -CreateTunnel."
    }
    if ($matches.Count -gt 1) { throw "More than one active tunnel is named '$TunnelName'. Supply -TunnelId explicitly." }
    $TunnelId = [string]$matches[0].id
}

$parsedTunnelId = [guid]::Empty
if (-not [guid]::TryParse($TunnelId, [ref]$parsedTunnelId)) { throw "Tunnel ID '$TunnelId' is not a UUID." }
$TunnelId = $parsedTunnelId.ToString()

if ([string]::IsNullOrWhiteSpace($CredentialsFile)) {
    $CredentialsFile = Join-Path $HOME ".cloudflared\$TunnelId.json"
}
$credentialSource = [IO.Path]::GetFullPath($CredentialsFile)
if (-not (Test-Path -LiteralPath $credentialSource -PathType Leaf)) {
    throw "Tunnel credentials were not found at the expected path. Run cloudflared tunnel create '$TunnelName' as your normal user, or pass -CredentialsFile."
}
Assert-AbodeToWebNotReparsePoint -Path $credentialSource

$hostnames = Get-AbodeToWebRegisteredHostnames -RegistryUri $RegistryUri -ManifestPath $ManifestPath -Hostname $Hostname
if ($hostnames.Count -eq 0 -and -not $AllowEmptyIngress) {
    throw 'No public project hostnames were found. Refusing to replace tunnel ingress with a 404-only configuration. Pass -AllowEmptyIngress only when deliberately withdrawing every hostname.'
}
if ($hostnames.Count -eq 0) { Write-Warning 'No public project hostnames were found. The explicitly requested empty tunnel will return 404 for every request.' }
$dnsHostnames = if ($null -ne $DnsHostname -and $DnsHostname.Count -gt 0) {
    @( $DnsHostname | ForEach-Object { Test-AbodeToWebPublicHostname -Hostname $_ } | Sort-Object -Unique )
} else {
    @($hostnames)
}
foreach ($dnsHostnameValue in $dnsHostnames) {
    if ($dnsHostnameValue -notin $hostnames) {
        throw "DNS hostname '$dnsHostnameValue' is not present in the generated ingress. Add it to the AbodeToWeb project registry before creating its DNS route."
    }
}

New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
$credentialForConfig = $credentialSource
if ($configIsMachineWide -or $InstallService) {
    $prewritePrincipal = if ($ownedByThisConfig) { "NT SERVICE\$ServiceName" } else { [Security.Principal.WindowsIdentity]::GetCurrent().Name }
    Set-AbodeToWebPrivateAcl -Path $configRoot -ServiceSid $prewritePrincipal -ServiceAccess Modify
    $credentialRoot = Join-Path $configRoot 'credentials'
    New-Item -ItemType Directory -Path $credentialRoot -Force | Out-Null
    Set-AbodeToWebPrivateAcl -Path $credentialRoot -ServiceSid $prewritePrincipal -ServiceAccess Read
    $credentialForConfig = Join-Path $credentialRoot "$TunnelId.json"
    if (-not $credentialSource.Equals($credentialForConfig, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $credentialSource -Destination $credentialForConfig -Force
    }
}

$yamlPath = $credentialForConfig.Replace('\', '/')
$logPath = (Join-Path $configRoot 'cloudflared.log').Replace('\', '/')
$gateway = $GatewayUri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
$configuration = [Collections.Generic.List[string]]::new()
$configuration.Add("tunnel: `"$TunnelId`"")
$configuration.Add("credentials-file: `"$yamlPath`"")
$configuration.Add('no-autoupdate: true')
$configuration.Add('loglevel: info')
$configuration.Add("logfile: `"$logPath`"")
$configuration.Add('metrics: "127.0.0.1:49312"')
$configuration.Add('ingress:')
foreach ($publicHostname in $hostnames) {
    $configuration.Add(('  - hostname: "{0}"' -f $publicHostname))
    $configuration.Add(('    service: "{0}"' -f $gateway))
}
$configuration.Add('  - service: "http_status:404"')

$temporaryConfig = Join-Path $configRoot "config-$([guid]::NewGuid().ToString('N')).tmp.yml"
try {
    $configuration | Set-Content -LiteralPath $temporaryConfig -Encoding utf8NoBOM
    Invoke-AbodeToWebCapturedProcess -FilePath $cloudflared -ArgumentList @('tunnel', '--config', $temporaryConfig, 'ingress', 'validate') -TimeoutSeconds 30 | Out-Null
    [IO.File]::Move($temporaryConfig, $configFullPath, $true)
} finally {
    if (Test-Path -LiteralPath $temporaryConfig) { Remove-Item -LiteralPath $temporaryConfig -Force }
}

Write-Host "Generated and validated $($hostnames.Count) explicit hostname route(s). Final unmatched action: HTTP 404."
Write-Host "Configuration: $configFullPath"

if ($CreateDnsRoutes) {
    Write-Warning 'The following explicit operation creates proxied DNS CNAME routes in your authenticated Cloudflare account. It does not overwrite an existing A, AAAA, or CNAME record.'
    foreach ($publicHostname in $dnsHostnames) {
        Invoke-AbodeToWebCapturedProcess -FilePath $cloudflared -ArgumentList @('tunnel', 'route', 'dns', $TunnelId, $publicHostname) -TimeoutSeconds 60 | Out-Null
        Write-Host "Created the tunnel DNS route for $publicHostname."
    }
} else {
    Write-Host 'DNS was not changed. Review the hostnames, then rerun with -CreateDnsRoutes or add the routes in Cloudflare.'
}

if ($InstallService) {
    $sc = "$env:SystemRoot\System32\sc.exe"
    if ($null -ne $service -and $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $ServiceName
        Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Stopped -TimeoutSeconds 45 | Out-Null
    }
    $binaryPath = '"' + $cloudflared + '" --config "' + $configFullPath + '" tunnel run'
    if ($null -eq $service) {
        Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @(
            'create', $ServiceName, 'binPath=', $binaryPath, 'start=', 'auto',
            'obj=', 'NT AUTHORITY\LocalService', 'DisplayName=', 'Cloudflare Tunnel for Abode to Web'
        ) -ErrorMessage "Unable to create service '$ServiceName'"
    } else {
        Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('config', $ServiceName, 'binPath=', $binaryPath, 'start=', 'auto', 'obj=', 'NT AUTHORITY\LocalService') -ErrorMessage "Unable to update service '$ServiceName'"
    }
    Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('description', $ServiceName, 'Outbound Cloudflare Tunnel connector for Abode to Web public application hostnames.') -ErrorMessage 'Unable to set tunnel service description'
    Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('sidtype', $ServiceName, 'unrestricted') -ErrorMessage 'Unable to enable the tunnel per-service SID'
    Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') -ErrorMessage 'Unable to configure tunnel service recovery'
    Invoke-AbodeToWebNative -FilePath $sc -ArgumentList @('failureflag', $ServiceName, '1') -ErrorMessage 'Unable to enable tunnel recovery actions'
    Set-AbodeToWebPrivateAcl -Path $configRoot -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Modify
    Set-AbodeToWebPrivateAcl -Path (Split-Path -Parent $credentialForConfig) -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Read
    if (-not $NoStart) {
        Start-Service -Name $ServiceName
        Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds 45 | Out-Null
        Write-Host "Tunnel service '$ServiceName' is running independently of Abode to Web."
    }
} elseif ($RestartService) {
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $ServiceName
        Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Stopped -TimeoutSeconds 45 | Out-Null
    }
    Start-Service -Name $ServiceName
    Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds 45 | Out-Null
} elseif ($null -ne $service -and $service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running) {
    Write-Warning "The tunnel service is running and was not restarted. Run scripts/restart-tunnel.ps1 after reviewing the new configuration."
}

Write-Warning 'No inbound firewall port, router port-forward, power plan, dashboard exposure, or Cloudflare Access policy was changed.'
