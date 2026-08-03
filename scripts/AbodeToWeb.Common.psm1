#requires -Version 7.0
Set-StrictMode -Version Latest

function Test-AbodeToWebAdministrator {
    [CmdletBinding()]
    param()

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-AbodeToWebAdministrator {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Action)

    if (-not (Test-AbodeToWebAdministrator)) {
        throw "$Action requires an elevated PowerShell 7 session. Reopen PowerShell with 'Run as administrator' and retry."
    }
}

function Get-AbodeToWebCurrentUserSid {
    [CmdletBinding()]
    param()

    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $sid) { throw 'The current Windows user SID could not be determined.' }
    return $sid.Value
}

function Resolve-AbodeToWebWindowsAccountSid {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$AccountName)

    $value = $AccountName.Trim()
    if ([string]::IsNullOrWhiteSpace($value) -or $value.IndexOfAny([char[]]@([char]0, "`r", "`n")) -ge 0) {
        throw 'Windows account name is empty or contains control characters.'
    }
    if ($value.StartsWith('.\', [StringComparison]::Ordinal)) {
        $value = "$env:COMPUTERNAME\$($value.Substring(2))"
    }
    try {
        return [Security.Principal.NTAccount]::new($value).Translate([Security.Principal.SecurityIdentifier]).Value
    } catch {
        throw "Windows account '$AccountName' could not be resolved to a SID on this PC."
    }
}

function ConvertTo-AbodeToWebWindowsSidList {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Sid,
        [switch]$RequireUserAccount
    )

    $normalized = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Sid) {
        foreach ($candidate in ([string]$item).Split([char[]]@(';', ','), [StringSplitOptions]::RemoveEmptyEntries)) {
            $value = $candidate.Trim()
            if ([string]::IsNullOrWhiteSpace($value)) { continue }
            try {
                $parsed = [Security.Principal.SecurityIdentifier]::new($value)
                if ($RequireUserAccount) {
                    $userAccount = Get-CimInstance Win32_UserAccount -Filter "SID='$($parsed.Value)'" -ErrorAction Stop
                    if ($null -eq $userAccount) {
                        throw "SID '$value' does not identify a Windows user account visible on this PC. Group SIDs are not valid dashboard administrators."
                    }
                }
                $normalized.Add($parsed.Value) | Out-Null
            } catch {
                if ($_.Exception.Message -like 'SID * does not identify a Windows user account*') { throw }
                throw "Invalid Windows SID '$value'. Use a user SID such as S-1-5-21-...; account names and group names are not accepted."
            }
        }
    }
    if ($normalized.Count -eq 0) { throw 'At least one dashboard administrator Windows user SID is required.' }
    return @($normalized | Sort-Object)
}

function Get-AbodeToWebServiceEnvironmentVariable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')][string]$Name
    )

    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path -LiteralPath $serviceRegistryPath)) { return $null }
    $property = Get-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
    if ($null -eq $property) { return $null }
    $prefix = "$Name="
    $values = @($property.Environment | ForEach-Object {
        $entry = [string]$_
        if ($entry.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $entry.Substring($prefix.Length)
        }
    })
    if ($values.Count -eq 0) { return $null }
    return ($values -join ';')
}

function Get-AbodeToWebLayout {
    [CmdletBinding()]
    param(
        [string]$InstallRoot = (Join-Path $env:ProgramFiles 'AbodeToWeb'),
        [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb')
    )

    [pscustomobject]@{
        InstallRoot     = [IO.Path]::GetFullPath($InstallRoot)
        DataRoot        = [IO.Path]::GetFullPath($DataRoot)
        ConfigRoot      = [IO.Path]::GetFullPath((Join-Path $DataRoot 'config'))
        ConfigPath      = [IO.Path]::GetFullPath((Join-Path $DataRoot 'config\abode-to-web.yaml'))
        StateRoot       = [IO.Path]::GetFullPath((Join-Path $DataRoot 'state'))
        LogsRoot        = [IO.Path]::GetFullPath((Join-Path $DataRoot 'logs'))
        DeploymentsRoot = [IO.Path]::GetFullPath((Join-Path $DataRoot 'deployments'))
        SecretsRoot     = [IO.Path]::GetFullPath((Join-Path $DataRoot 'secrets'))
        BackupsRoot     = [IO.Path]::GetFullPath((Join-Path $DataRoot 'backups'))
        CloudflareRoot  = [IO.Path]::GetFullPath((Join-Path $DataRoot 'cloudflared'))
    }
}

function Assert-AbodeToWebSafeChildPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent,
        [switch]$AllowParent
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    $comparison = [StringComparison]::OrdinalIgnoreCase
    $isParent = $fullPath.Equals($fullParent, $comparison)
    $isChild = $fullPath.StartsWith("$fullParent\", $comparison)

    if ((-not $isChild) -and (-not ($AllowParent -and $isParent))) {
        throw "Refusing filesystem operation outside '$fullParent': '$fullPath'."
    }

    return $fullPath
}

function Assert-AbodeToWebStandardDataRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $expected = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'AbodeToWeb')).TrimEnd('\')
    if (-not $fullPath.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Machine mutations are restricted to the standard Abode to Web data root '$expected'; received '$fullPath'."
    }
    if (Test-Path -LiteralPath $fullPath) {
        $item = Get-Item -LiteralPath $fullPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The Abode to Web data root cannot be a junction or symbolic link: '$fullPath'."
        }
    }
    return $fullPath
}

function Assert-AbodeToWebStandardInstallRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $expected = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'AbodeToWeb')).TrimEnd('\')
    if (-not $fullPath.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Machine mutations are restricted to the standard Abode to Web install root '$expected'; received '$fullPath'."
    }
    if (Test-Path -LiteralPath $fullPath) {
        $item = Get-Item -LiteralPath $fullPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The Abode to Web install root cannot be a junction or symbolic link: '$fullPath'."
        }
    }
    return $fullPath
}

function Assert-AbodeToWebDashboardUri {
    [CmdletBinding()]
    param([Parameter(Mandatory)][uri]$Uri)

    if ($Uri.Scheme -ne 'http' -or $Uri.Host -ne '127.0.0.1' -or $Uri.Port -ne 4545 -or
        $Uri.AbsolutePath -ne '/' -or -not [string]::IsNullOrEmpty($Uri.Query) -or
        -not [string]::IsNullOrEmpty($Uri.Fragment) -or -not [string]::IsNullOrEmpty($Uri.UserInfo)) {
        throw "The Abode to Web dashboard URI is restricted to 'http://127.0.0.1:4545/'."
    }
    return $Uri
}

function Assert-AbodeToWebNotReparsePoint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to modify a junction or symbolic link: '$([IO.Path]::GetFullPath($Path))'."
        }
    }
}

function Assert-AbodeToWebTreeHasNoReparsePoints {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    Assert-AbodeToWebNotReparsePoint -Path $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $reparsePoint = Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($null -ne $reparsePoint) {
        throw "Refusing the recursive filesystem operation because the tree contains a junction or symbolic link: '$($reparsePoint.FullName)'. Remove or review the link explicitly first."
    }
}

function Assert-AbodeToWebProjectRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $volumeRoot = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\')
    if ($fullPath.Equals($volumeRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "A drive root cannot be granted to Abode to Web: '$fullPath'. Choose a narrow projects directory."
    }
    foreach ($protectedRoot in @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData)) {
        if ([string]::IsNullOrWhiteSpace($protectedRoot)) { continue }
        $protected = [IO.Path]::GetFullPath($protectedRoot).TrimEnd('\')
        if ($fullPath.Equals($protected, [StringComparison]::OrdinalIgnoreCase) -or $fullPath.StartsWith("$protected\", [StringComparison]::OrdinalIgnoreCase)) {
            throw "A Windows, Program Files, or ProgramData path cannot be used as a project root: '$fullPath'."
        }
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        throw "Allowed project root does not exist: '$fullPath'."
    }
    Assert-AbodeToWebNotReparsePoint -Path $fullPath
    return $fullPath
}

function Invoke-AbodeToWebNative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter()][string[]]$ArgumentList = @(),
        [string]$ErrorMessage = "Command failed: $FilePath"
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$ErrorMessage (exit code $LASTEXITCODE)."
    }
}

function Remove-AbodeToWebAclGrant {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Identity
    )

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    Assert-AbodeToWebNotReparsePoint -Path $Path
    try {
        $targetSid = if ($Identity.StartsWith('*')) {
            [Security.Principal.SecurityIdentifier]::new($Identity.Substring(1))
        } else {
            [Security.Principal.NTAccount]::new($Identity).Translate([Security.Principal.SecurityIdentifier])
        }
    } catch {
        throw "ACL identity '$Identity' cannot be resolved safely for '$Path'."
    }

    $acl = Get-Acl -LiteralPath $Path
    $matchingRules = @($acl.Access | Where-Object {
        if ($_.IsInherited -or $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { return $false }
        try { return $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $targetSid.Value }
        catch { return $false }
    })
    if ($matchingRules.Count -eq 0) { return $false }

    & "$env:SystemRoot\System32\icacls.exe" $Path '/remove:g' ("*" + $targetSid.Value) 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { return $true }

    # Deleted local accounts can leave a raw SID that icacls refuses to resolve even
    # though the ACE is present. Remove only the explicit Allow rules we inspected;
    # preserve any Deny rule for that SID.
    foreach ($matchingRule in $matchingRules) {
        $acl.RemoveAccessRuleSpecific($matchingRule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    return $true
}

function Get-AbodeToWebService {
    [CmdletBinding()]
    param([string]$ServiceName = 'AbodeToWeb')

    return Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

function Test-AbodeToWebServiceExecutable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName,
        [Parameter(Mandatory)][string]$ExecutablePath
    )

    $definition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($null -eq $definition) { return $false }

    $expected = [IO.Path]::GetFullPath($ExecutablePath)
    $commandLine = ([string]$definition.PathName).Trim()
    return $commandLine.Equals($expected, [StringComparison]::OrdinalIgnoreCase) -or
        $commandLine.Equals(('"' + $expected + '"'), [StringComparison]::OrdinalIgnoreCase)
}

function Wait-AbodeToWebServiceState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][ValidateSet('Running', 'Stopped')][string]$State,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $ServiceName -ErrorAction Stop
        if ($service.Status.ToString() -eq $State) {
            return $service
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Service '$ServiceName' did not reach state '$State' within $TimeoutSeconds seconds."
}

function Wait-AbodeToWebServiceProcessExit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][uint32]$ProcessId,
        [datetime]$CreationTime,
        [int]$TimeoutSeconds = 30
    )

    if ($ProcessId -eq 0) { return }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction SilentlyContinue
        if ($null -eq $process) { return }
        # A rapidly reused PID must not make AbodeToWeb wait on an unrelated process.
        if ($PSBoundParameters.ContainsKey('CreationTime') -and $process.CreationDate -ne $CreationTime) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The stopped service process $ProcessId did not exit within $TimeoutSeconds seconds. Refusing to modify files it may still hold open."
}

function Stop-AbodeToWebServiceIfRunning {
    [CmdletBinding()]
    param(
        [string]$ServiceName = 'AbodeToWeb',
        [int]$TimeoutSeconds = 30
    )

    $service = Get-AbodeToWebService -ServiceName $ServiceName
    if ($null -eq $service -or $service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped) {
        return $false
    }

    $definition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
    [uint32]$serviceProcessId = $definition.ProcessId
    $serviceProcess = if ($serviceProcessId -eq 0) {
        $null
    } else {
        Get-CimInstance Win32_Process -Filter "ProcessId=$serviceProcessId" -ErrorAction SilentlyContinue
    }
    $serviceProcessCreationTime = if ($null -eq $serviceProcess) { $null } else { $serviceProcess.CreationDate }

    if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::StopPending) {
        Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Stopped -TimeoutSeconds $TimeoutSeconds | Out-Null
        if ($null -ne $serviceProcessCreationTime) {
            Wait-AbodeToWebServiceProcessExit -ProcessId $serviceProcessId -CreationTime $serviceProcessCreationTime -TimeoutSeconds $TimeoutSeconds
        }
        return $true
    }
    if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::StartPending) {
        Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Running -TimeoutSeconds $TimeoutSeconds | Out-Null
    }

    Stop-Service -Name $ServiceName -ErrorAction Stop
    Wait-AbodeToWebServiceState -ServiceName $ServiceName -State Stopped -TimeoutSeconds $TimeoutSeconds | Out-Null
    if ($null -ne $serviceProcessCreationTime) {
        Wait-AbodeToWebServiceProcessExit -ProcessId $serviceProcessId -CreationTime $serviceProcessCreationTime -TimeoutSeconds $TimeoutSeconds
    }
    return $true
}

function New-AbodeToWebExactAcl {
    param(
        [Parameter(Mandatory)][string]$ServiceSid,
        [ValidateSet('Read', 'Modify', 'Full')][string]$ServiceAccess,
        [Parameter(Mandatory)][bool]$Directory
    )

    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $resolvedServiceSid = [Security.Principal.SecurityIdentifier]::new((Resolve-AbodeToWebWindowsAccountSid -AccountName $ServiceSid))
    $acl = if ($Directory) { [Security.AccessControl.DirectorySecurity]::new() } else { [Security.AccessControl.FileSecurity]::new() }
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($administratorsSid)

    if ($Directory) {
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $serviceRights = switch ($ServiceAccess) {
            'Read' { [Security.AccessControl.FileSystemRights]::ReadAndExecute }
            'Modify' { [Security.AccessControl.FileSystemRights]::Modify }
            'Full' { [Security.AccessControl.FileSystemRights]::FullControl }
        }
        foreach ($entry in @(
            [pscustomobject]@{ Sid = $systemSid; Rights = [Security.AccessControl.FileSystemRights]::FullControl }
            [pscustomobject]@{ Sid = $administratorsSid; Rights = [Security.AccessControl.FileSystemRights]::FullControl }
            [pscustomobject]@{ Sid = $resolvedServiceSid; Rights = $serviceRights }
        )) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                $entry.Sid, $entry.Rights, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule) | Out-Null
        }
    } else {
        $serviceRights = switch ($ServiceAccess) {
            'Read' { [Security.AccessControl.FileSystemRights]::Read }
            'Modify' { [Security.AccessControl.FileSystemRights]::Modify }
            'Full' { [Security.AccessControl.FileSystemRights]::FullControl }
        }
        foreach ($entry in @(
            [pscustomobject]@{ Sid = $systemSid; Rights = [Security.AccessControl.FileSystemRights]::FullControl }
            [pscustomobject]@{ Sid = $administratorsSid; Rights = [Security.AccessControl.FileSystemRights]::FullControl }
            [pscustomobject]@{ Sid = $resolvedServiceSid; Rights = $serviceRights }
        )) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                $entry.Sid, $entry.Rights, [Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule) | Out-Null
        }
    }
    return $acl
}

function Set-AbodeToWebExactFileAcl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceSid,
        [ValidateSet('Read', 'Modify', 'Full')][string]$ServiceAccess = 'Read'
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Protected file does not exist: '$Path'." }
    Assert-AbodeToWebNotReparsePoint -Path $Path
    $acl = New-AbodeToWebExactAcl -ServiceSid $ServiceSid -ServiceAccess $ServiceAccess -Directory $false
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Set-AbodeToWebExactDirectoryAcl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceSid,
        [ValidateSet('Read', 'Modify', 'Full')][string]$ServiceAccess = 'Modify'
    )

    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Protected directory does not exist: '$Path'." }
    Assert-AbodeToWebNotReparsePoint -Path $Path
    $acl = New-AbodeToWebExactAcl -ServiceSid $ServiceSid -ServiceAccess $ServiceAccess -Directory $true
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Get-AbodeToWebValidatedSecretEntries {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SecretsRoot)

    $root = [IO.Path]::GetFullPath($SecretsRoot).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Application secrets root is not a directory: '$root'."
    }
    Assert-AbodeToWebNotReparsePoint -Path $root

    $appIdPattern = [Text.RegularExpressions.Regex]::new(
        '^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\.env$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant,
        [TimeSpan]::FromSeconds(1))
    $files = [Collections.Generic.List[object]]::new()
    foreach ($entry in @(Get-ChildItem -LiteralPath $root -Force -ErrorAction Stop | Sort-Object -Property Name)) {
        $fullPath = Assert-AbodeToWebSafeChildPath -Path $entry.FullName -Parent $root
        $relativePath = [IO.Path]::GetRelativePath($root, $fullPath)
        if ($relativePath.IndexOf([IO.Path]::DirectorySeparatorChar) -ge 0 -or
            $relativePath.IndexOf([IO.Path]::AltDirectorySeparatorChar) -ge 0) {
            throw "Application secret entry is not directly under the secrets root: '$($entry.Name)'."
        }

        $linkType = if ($null -eq $entry.PSObject.Properties['LinkType']) { $null } else { [string]$entry.LinkType }
        if ($entry.PSIsContainer -or $entry -isnot [IO.FileInfo] -or
            ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not [string]::IsNullOrWhiteSpace($linkType)) {
            throw "Unexpected application secret entry type: '$($entry.Name)'. Only regular files are allowed directly under the secrets root."
        }

        if ($entry.Name -ceq 'runtime-credential.json') {
            # This machine-bound DPAPI envelope has a distinct read-only service ACL
            # policy in AbodeToWeb.RuntimeAccount.psm1. Never treat it as an app secret.
            $files.Add([pscustomobject]@{
                Kind = 'RuntimeCredential'
                AppId = $null
                FileName = $entry.Name
                Path = $fullPath
            })
            continue
        }
        if (-not $appIdPattern.IsMatch($entry.Name)) {
            throw "Unexpected file in the application secrets root: '$($entry.Name)'. Expected a lowercase <app-id>.env file."
        }

        $files.Add([pscustomobject]@{
            Kind = 'ApplicationSecret'
            AppId = $entry.Name.Substring(0, $entry.Name.Length - 4)
            FileName = $entry.Name
            Path = $fullPath
        })
    }
    return @($files)
}

function Get-AbodeToWebApplicationSecretFiles {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SecretsRoot)

    return @(Get-AbodeToWebValidatedSecretEntries -SecretsRoot $SecretsRoot |
        Where-Object { $_.Kind -ceq 'ApplicationSecret' })
}

function Assert-AbodeToWebAclMatches {
    param(
        [Parameter(Mandatory)][Security.AccessControl.FileSystemSecurity]$Actual,
        [Parameter(Mandatory)][Security.AccessControl.FileSystemSecurity]$Expected,
        [Parameter(Mandatory)][string]$Description
    )

    $expectedOwner = $Expected.GetOwner([Security.Principal.SecurityIdentifier])
    $actualOwner = $Actual.GetOwner([Security.Principal.SecurityIdentifier])
    if (-not $Actual.AreAccessRulesProtected -or -not $actualOwner.Equals($expectedOwner)) {
        throw "$Description owner or inheritance protection is not exact."
    }

    $ruleKey = {
        param([Security.AccessControl.FileSystemAccessRule]$Rule)
        $sid = if ($Rule.IdentityReference -is [Security.Principal.SecurityIdentifier]) {
            $Rule.IdentityReference
        } else {
            $Rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier])
        }
        return '{0}|{1}|{2}|{3}|{4}|{5}' -f $sid.Value, [int64]$Rule.FileSystemRights,
            [int]$Rule.InheritanceFlags, [int]$Rule.PropagationFlags, [int]$Rule.AccessControlType, [bool]$Rule.IsInherited
    }
    $actualRules = @($Actual.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        ForEach-Object { & $ruleKey $_ } | Sort-Object)
    $expectedRules = @($Expected.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        ForEach-Object { & $ruleKey $_ } | Sort-Object)
    if ($actualRules.Count -ne $expectedRules.Count -or
        ($actualRules -join "`n") -cne ($expectedRules -join "`n")) {
        throw "$Description permissions are not exact."
    }
}

function Assert-AbodeToWebApplicationSecretsRootAcl {
    param(
        [Parameter(Mandatory)][string]$SecretsRoot,
        [Parameter(Mandatory)][string]$ServicePrincipal
    )

    $actual = Get-Acl -LiteralPath $SecretsRoot -ErrorAction Stop
    $expected = New-AbodeToWebExactAcl -ServiceSid $ServicePrincipal -ServiceAccess Modify -Directory $true
    Assert-AbodeToWebAclMatches -Actual $actual -Expected $expected -Description 'Application secrets directory'
}

function Assert-AbodeToWebApplicationSecretFileAcl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb'
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Protected application secret file does not exist: '$Path'." }
    Assert-AbodeToWebNotReparsePoint -Path $Path
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    $linkType = if ($null -eq $item.PSObject.Properties['LinkType']) { $null } else { [string]$item.LinkType }
    if ($item -isnot [IO.FileInfo] -or $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::IsNullOrWhiteSpace($linkType)) {
        throw "Protected application secret is not a regular file: '$($item.Name)'."
    }

    $actual = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $servicePrincipal = "NT SERVICE\$ServiceName"
    $serviceSid = Resolve-AbodeToWebWindowsAccountSid -AccountName $servicePrincipal
    if ($serviceSid -notmatch '^S-1-5-80-(?:[0-9]+-){4}[0-9]+$') {
        throw "Windows service '$ServiceName' did not resolve to a unique per-service SID."
    }
    $expected = New-AbodeToWebExactAcl -ServiceSid $servicePrincipal -ServiceAccess Modify -Directory $false
    $resolvedServiceSid = [Security.Principal.SecurityIdentifier]::new($serviceSid)
    $expected.SetOwner($resolvedServiceSid)
    Assert-AbodeToWebAclMatches -Actual $actual -Expected $expected -Description "Application secret '$($item.Name)'"
}

function Repair-AbodeToWebApplicationSecretFileAcls {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SecretsRoot,
        [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb'
    )

    $servicePrincipal = "NT SERVICE\$ServiceName"
    $serviceSid = Resolve-AbodeToWebWindowsAccountSid -AccountName $servicePrincipal
    if ($serviceSid -notmatch '^S-1-5-80-(?:[0-9]+-){4}[0-9]+$') {
        throw "Windows service '$ServiceName' did not resolve to a unique per-service SID."
    }

    # Validate every direct entry before changing any child ACL. Once the parent is
    # protected, enumerate again so a pre-existing writable parent cannot race a
    # link or unexpected file into the repair set.
    $before = @(Get-AbodeToWebValidatedSecretEntries -SecretsRoot $SecretsRoot)
    Set-AbodeToWebExactDirectoryAcl -Path $SecretsRoot -ServiceSid $servicePrincipal -ServiceAccess Modify
    Assert-AbodeToWebApplicationSecretsRootAcl -SecretsRoot $SecretsRoot -ServicePrincipal $servicePrincipal
    $after = @(Get-AbodeToWebValidatedSecretEntries -SecretsRoot $SecretsRoot)
    if ((@($before | ForEach-Object { $_.FileName }) -join "`n") -cne
        (@($after | ForEach-Object { $_.FileName }) -join "`n")) {
        throw 'Application secret files changed while their permissions were being repaired. No child ACL was modified.'
    }

    $repaired = [Collections.Generic.List[object]]::new()
    foreach ($secret in @($after | Where-Object { $_.Kind -ceq 'ApplicationSecret' })) {
        # Re-resolve and revalidate the direct child immediately before Set-Acl.
        $current = @(Get-AbodeToWebApplicationSecretFiles -SecretsRoot $SecretsRoot |
            Where-Object { $_.FileName -ceq $secret.FileName })
        if ($current.Count -ne 1 -or -not $current[0].Path.Equals($secret.Path, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Application secret file changed while its permissions were being repaired: '$($secret.FileName)'."
        }
        $acl = New-AbodeToWebExactAcl -ServiceSid $servicePrincipal -ServiceAccess Modify -Directory $false
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($serviceSid))
        Set-Acl -LiteralPath $secret.Path -AclObject $acl -ErrorAction Stop
        Assert-AbodeToWebApplicationSecretFileAcl -Path $secret.Path -ServiceName $ServiceName
        $repaired.Add([pscustomobject]@{ AppId = $secret.AppId; FileName = $secret.FileName })
    }
    return @($repaired)
}

function Set-AbodeToWebPrivateAcl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceSid,
        [ValidateSet('Read', 'Modify')][string]$ServiceAccess = 'Modify'
    )

    Assert-AbodeToWebNotReparsePoint -Path $Path
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Private ACL target is not a directory: '$Path'." }
    Assert-AbodeToWebTreeHasNoReparsePoints -Path $Path

    # Build each DACL from scratch. icacls /grant:r replaces only named ACEs and can
    # silently preserve an old Everyone/Users grant, which is unsafe for LocalMachine
    # DPAPI blobs and application/tunnel secrets.
    Set-AbodeToWebExactDirectoryAcl -Path $Path -ServiceSid $ServiceSid -ServiceAccess $ServiceAccess
    Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop | ForEach-Object {
        if ($_.PSIsContainer) {
            Set-AbodeToWebExactDirectoryAcl -Path $_.FullName -ServiceSid $ServiceSid -ServiceAccess $ServiceAccess
        } else {
            Set-AbodeToWebExactFileAcl -Path $_.FullName -ServiceSid $ServiceSid -ServiceAccess $ServiceAccess
        }
    }
}

function Set-AbodeToWebDataAcl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DataRoot,
        [string]$ServiceName = 'AbodeToWeb'
    )

    Assert-AbodeToWebStandardDataRoot -Path $DataRoot | Out-Null
    $serviceSid = "NT SERVICE\$ServiceName"
    Set-AbodeToWebExactDirectoryAcl -Path $DataRoot -ServiceSid $serviceSid -ServiceAccess Modify
}

function New-AbodeToWebDashboardShortcut {
    [CmdletBinding()]
    param(
        [uri]$DashboardUrl = 'http://127.0.0.1:4545',
        [switch]$Desktop
    )

    $DashboardUrl = Assert-AbodeToWebDashboardUri -Uri $DashboardUrl
    $shortcutRoot = if ($Desktop) {
        [Environment]::GetFolderPath('CommonDesktopDirectory')
    } else {
        Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'AbodeToWeb'
    }
    New-Item -ItemType Directory -Path $shortcutRoot -Force | Out-Null
    $shortcutPath = Join-Path $shortcutRoot 'AbodeToWeb.url'
    @(
        '[InternetShortcut]'
        "URL=$DashboardUrl"
        'IconIndex=0'
    ) | Set-Content -LiteralPath $shortcutPath -Encoding ascii
    return $shortcutPath
}

function Test-AbodeToWebHttpEndpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [int]$TimeoutSeconds = 3,
        [switch]$UseDefaultCredentials
    )

    if ($UseDefaultCredentials -and ($Uri.Scheme -ne 'http' -or $Uri.Host -ne '127.0.0.1' -or $Uri.Port -ne 4545 -or
        $Uri.AbsolutePath -ne '/health' -or -not [string]::IsNullOrEmpty($Uri.Query) -or
        -not [string]::IsNullOrEmpty($Uri.Fragment) -or -not [string]::IsNullOrEmpty($Uri.UserInfo))) {
        throw "Default Windows credentials may be sent only to Abode to Web's exact health endpoint 'http://127.0.0.1:4545/health'; received '$Uri'."
    }
    try {
        $request = @{
            Uri = $Uri
            Method = 'Get'
            TimeoutSec = $TimeoutSeconds
            SkipHttpErrorCheck = $true
            MaximumRedirection = 0
        }
        if ($UseDefaultCredentials) {
            $request['UseDefaultCredentials'] = $true
            # PowerShell requires this opt-in for any HTTP authentication. The guard above
            # restricts it to a credential-free loopback URI and redirects are disabled.
            $request['AllowUnencryptedAuthentication'] = $true
        }
        $response = Invoke-WebRequest @request
        $statusCode = [int]$response.StatusCode
        $successful = $statusCode -ge 200 -and $statusCode -le 299
        [pscustomobject]@{
            Reachable = $successful
            Responded = $true
            StatusCode = $statusCode
            Error = if ($successful) { $null } else { "Endpoint returned HTTP $statusCode." }
        }
    } catch {
        [pscustomobject]@{ Reachable = $false; Responded = $false; StatusCode = $null; Error = $_.Exception.Message }
    }
}

Export-ModuleMember -Function @(
    'Assert-AbodeToWebAdministrator',
    'Assert-AbodeToWebDashboardUri',
    'Assert-AbodeToWebNotReparsePoint',
    'Assert-AbodeToWebProjectRoot',
    'Assert-AbodeToWebSafeChildPath',
    'Assert-AbodeToWebStandardDataRoot',
    'Assert-AbodeToWebStandardInstallRoot',
    'Assert-AbodeToWebTreeHasNoReparsePoints',
    'ConvertTo-AbodeToWebWindowsSidList',
    'Get-AbodeToWebCurrentUserSid',
    'Get-AbodeToWebLayout',
    'Get-AbodeToWebService',
    'Get-AbodeToWebServiceEnvironmentVariable',
    'Invoke-AbodeToWebNative',
    'New-AbodeToWebDashboardShortcut',
    'Remove-AbodeToWebAclGrant',
    'Resolve-AbodeToWebWindowsAccountSid',
    'Repair-AbodeToWebApplicationSecretFileAcls',
    'Set-AbodeToWebDataAcl',
    'Set-AbodeToWebExactDirectoryAcl',
    'Set-AbodeToWebExactFileAcl',
    'Set-AbodeToWebPrivateAcl',
    'Stop-AbodeToWebServiceIfRunning',
    'Test-AbodeToWebAdministrator',
    'Test-AbodeToWebServiceExecutable',
    'Test-AbodeToWebHttpEndpoint',
    'Wait-AbodeToWebServiceState'
)
