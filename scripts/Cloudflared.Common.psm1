#requires -Version 7.0
Set-StrictMode -Version Latest

function Get-AbodeToWebCloudflaredExecutable {
    [CmdletBinding()]
    param([string]$ExecutablePath)

    if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) {
        $resolved = [IO.Path]::GetFullPath($ExecutablePath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "cloudflared was not found at '$resolved'." }
        return $resolved
    }

    $command = Get-Command cloudflared.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles 'cloudflared\cloudflared.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'cloudflared\cloudflared.exe')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    throw 'cloudflared is not installed or is not on PATH. Run scripts/install-cloudflared.ps1 first.'
}

function Invoke-AbodeToWebCapturedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter()][string[]]$ArgumentList = @(),
        [int]$TimeoutSeconds = 30,
        [switch]$AllowFailure
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $ArgumentList) { $startInfo.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Unable to start '$FilePath'." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw "'$FilePath' exceeded the $TimeoutSeconds second timeout."
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult().Trim()
    $stderr = $stderrTask.GetAwaiter().GetResult().Trim()
    $result = [pscustomobject]@{ ExitCode = $process.ExitCode; StandardOutput = $stdout; StandardError = $stderr }
    $process.Dispose()

    if ($result.ExitCode -ne 0 -and -not $AllowFailure) {
        $safeError = if ([string]::IsNullOrWhiteSpace($stderr)) { 'No diagnostic text was returned.' } else { $stderr }
        throw "cloudflared command failed (exit $($result.ExitCode)): $safeError"
    }
    return $result
}

function Test-AbodeToWebPublicHostname {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Hostname)

    $value = $Hostname.Trim().TrimEnd('.').ToLowerInvariant()
    if ($value.Length -gt 253 -or $value -notmatch '^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$') {
        throw "Invalid public hostname '$Hostname'. Use a fully-qualified DNS hostname; wildcards and URLs are not accepted."
    }
    return $value
}

function Get-AbodeToWebObjectProperty {
    param(
        [Parameter(Mandatory)][object]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-AbodeToWebManifestHostnames {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Manifest was not found: '$ManifestPath'." }
    if ([IO.Path]::GetExtension($ManifestPath) -eq '.json') {
        $json = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        $jsonProjects = Get-AbodeToWebObjectProperty -InputObject $json -Name 'projects'
        $jsonApps = Get-AbodeToWebObjectProperty -InputObject $json -Name 'apps'
        $projects = if ($null -ne $jsonProjects) { @($jsonProjects) } elseif ($null -ne $jsonApps) { @($jsonApps) } else { @($json) }
        return @($projects | ForEach-Object {
            $visibility = Get-AbodeToWebObjectProperty -InputObject $_ -Name 'visibility'
            $routing = Get-AbodeToWebObjectProperty -InputObject $_ -Name 'routing'
            $directHostname = Get-AbodeToWebObjectProperty -InputObject $_ -Name 'hostname'
            if ($visibility -ne 'disabled') {
                $routedHostname = if ($null -eq $routing) { $null } else { Get-AbodeToWebObjectProperty -InputObject $routing -Name 'hostname' }
                if ($null -ne $routedHostname) { [string]$routedHostname }
                elseif ($null -ne $directHostname) { [string]$directHostname }
            }
        } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    $hostnames = [Collections.Generic.List[string]]::new()
    $insideApps = $false
    $appsIndent = -1
    $entryIndent = -1
    $routingIndent = -1
    $currentHostname = $null
    $currentVisibility = 'public'
    $finishEntry = {
        if (-not [string]::IsNullOrWhiteSpace($currentHostname) -and $currentVisibility -ne 'disabled') {
            $hostnames.Add($currentHostname)
        }
        $currentHostname = $null
        $currentVisibility = 'public'
        $routingIndent = -1
    }
    foreach ($line in Get-Content -LiteralPath $ManifestPath) {
        if ($line -match '^\s*(?:#.*)?$') { continue }
        $indent = $line.Length - $line.TrimStart().Length
        if (-not $insideApps -and $line -match '^\s*apps\s*:\s*(?:#.*)?$') {
            $insideApps = $true
            $appsIndent = $indent
            continue
        }
        if (-not $insideApps) { continue }
        if ($indent -le $appsIndent) {
            . $finishEntry
            $insideApps = $false
            break
        }
        if ($line -match '^\s*-\s+[A-Za-z][A-Za-z0-9_-]*\s*:') {
            if ($entryIndent -lt 0) { $entryIndent = $indent }
            if ($indent -eq $entryIndent) {
                . $finishEntry
                continue
            }
        }
        if ($entryIndent -lt 0) { continue }
        if ($line -match '^\s*visibility\s*:\s*["'']?([^"''\s#]+)["'']?\s*(?:#.*)?$') {
            $currentVisibility = $Matches[1].ToLowerInvariant()
            continue
        }
        if ($line -match '^\s*routing\s*:\s*(?:#.*)?$') {
            $routingIndent = $indent
            continue
        }
        if ($routingIndent -ge 0 -and $indent -le $routingIndent) { $routingIndent = -1 }
        if ($routingIndent -ge 0 -and $line -match '^\s*hostname\s*:\s*["'']?([^"''\s#]+)["'']?\s*(?:#.*)?$') {
            $currentHostname = $Matches[1]
        }
    }
    if ($insideApps) { . $finishEntry }
    return @($hostnames)
}

function Get-AbodeToWebRegisteredHostnames {
    [CmdletBinding()]
    param(
        [uri]$RegistryUri = 'http://127.0.0.1:4545/api/v1/projects/export',
        [string]$ManifestPath,
        [string[]]$Hostname
    )

    $values = [Collections.Generic.List[string]]::new()
    if ($null -ne $Hostname -and $Hostname.Count -gt 0) {
        foreach ($item in $Hostname) { $values.Add($item) }
    } else {
        if ($RegistryUri.Scheme -ne 'http' -or -not $RegistryUri.IsLoopback -or $RegistryUri.Port -ne 4545 -or
            $RegistryUri.AbsolutePath -ne '/api/v1/projects/export' -or -not [string]::IsNullOrEmpty($RegistryUri.Query) -or
            -not [string]::IsNullOrEmpty($RegistryUri.Fragment) -or -not [string]::IsNullOrEmpty($RegistryUri.UserInfo)) {
            throw "The authenticated project registry URI must be the Abode to Web loopback endpoint 'http://127.0.0.1:4545/api/v1/projects/export'."
        }
        try {
            $export = Invoke-RestMethod -Uri $RegistryUri -Method Get -TimeoutSec 5 -UseDefaultCredentials -AllowUnencryptedAuthentication -MaximumRedirection 0
            $exportProjects = Get-AbodeToWebObjectProperty -InputObject $export -Name 'projects'
            $exportApps = Get-AbodeToWebObjectProperty -InputObject $export -Name 'apps'
            $projects = if ($null -ne $exportProjects) { @($exportProjects) } elseif ($null -ne $exportApps) { @($exportApps) } else { @($export) }
            foreach ($project in $projects) {
                $visibility = Get-AbodeToWebObjectProperty -InputObject $project -Name 'visibility'
                $directHostname = Get-AbodeToWebObjectProperty -InputObject $project -Name 'hostname'
                $publicHostname = Get-AbodeToWebObjectProperty -InputObject $project -Name 'publicHostname'
                $url = Get-AbodeToWebObjectProperty -InputObject $project -Name 'url'
                if ($null -ne $visibility -and [string]$visibility -eq 'disabled') { continue }
                if ($null -ne $directHostname) { $values.Add([string]$directHostname); continue }
                if ($null -ne $publicHostname) { $values.Add([string]$publicHostname); continue }
                if ($null -ne $url) {
                    $uri = [uri][string]$url
                    if (-not $uri.IsLoopback) { $values.Add($uri.DnsSafeHost) }
                }
            }
        } catch {
            if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
                throw "Abode to Web registry export is unavailable and no -ManifestPath was supplied: $($_.Exception.Message)"
            }
            foreach ($item in Get-AbodeToWebManifestHostnames -ManifestPath $ManifestPath) { $values.Add($item) }
        }
    }

    return @($values | ForEach-Object { Test-AbodeToWebPublicHostname -Hostname $_ } | Sort-Object -Unique)
}

Export-ModuleMember -Function @(
    'Get-AbodeToWebCloudflaredExecutable',
    'Get-AbodeToWebManifestHostnames',
    'Get-AbodeToWebRegisteredHostnames',
    'Invoke-AbodeToWebCapturedProcess',
    'Test-AbodeToWebPublicHostname'
)
