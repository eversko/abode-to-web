#requires -Version 7.0
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'AbodeToWeb.Common.psm1')

$script:RuntimeAccountDescription = 'AbodeToWeb application runtime.'
if ($script:RuntimeAccountDescription.Length -gt 48) {
    throw 'The Abode to Web runtime-account description exceeds the Windows 48-character limit.'
}
$script:RuntimeCredentialEntropyText = 'AbodeToWeb.RuntimeCredential.v1'

function Assert-AbodeToWebRuntimeAccountName {
    param([Parameter(Mandatory)][string]$AccountName)

    if ($AccountName -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,19}$') {
        throw "Invalid local runtime account name '$AccountName'. Use 1-20 letters, digits, dots, underscores, or hyphens."
    }
}

function Get-AbodeToWebRuntimeAccountLayout {
    [CmdletBinding()]
    param(
        [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
        [string]$AccountName = 'AbodeToWebRuntime'
    )

    Assert-AbodeToWebRuntimeAccountName -AccountName $AccountName
    $layout = Get-AbodeToWebLayout -DataRoot $DataRoot
    [pscustomobject]@{
        AccountName = $AccountName
        CredentialPath = Join-Path $layout.SecretsRoot 'runtime-credential.json'
        MarkerPath = Join-Path $layout.StateRoot 'runtime-account.json'
        HiddenAccountsRegistryPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
    }
}

function Get-AbodeToWebRuntimeAccountMarker {
    param([Parameter(Mandatory)][string]$MarkerPath)

    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) { return $null }
    Assert-AbodeToWebRuntimeOwnershipAcl -MarkerPath $MarkerPath
    Assert-AbodeToWebNotReparsePoint -Path $MarkerPath
    try { $marker = Get-Content -LiteralPath $MarkerPath -Raw | ConvertFrom-Json }
    catch { throw "Abode to Web runtime-account ownership marker is unreadable: '$MarkerPath'. $($_.Exception.Message)" }
    foreach ($name in @('version', 'managedBy', 'accountName', 'accountSid', 'createdAtUtc')) {
        if ($null -eq $marker.PSObject.Properties[$name]) { throw "Runtime-account ownership marker is missing '$name': '$MarkerPath'." }
    }
    if ([int]$marker.version -ne 1 -or [string]$marker.managedBy -ne 'AbodeToWeb') {
        throw "Runtime-account ownership marker has an unsupported format: '$MarkerPath'."
    }
    Assert-AbodeToWebRuntimeAccountName -AccountName ([string]$marker.accountName)
    try { $null = [Security.Principal.SecurityIdentifier]::new([string]$marker.accountSid) }
    catch { throw "Runtime-account ownership marker contains an invalid SID: '$MarkerPath'." }
    try { $null = [DateTimeOffset]::Parse([string]$marker.createdAtUtc, [Globalization.CultureInfo]::InvariantCulture) }
    catch { throw "Runtime-account ownership marker contains an invalid timestamp: '$MarkerPath'." }
    return $marker
}

function Write-AbodeToWebAtomicJson {
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Path,
        [int]$Depth = 5
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Assert-AbodeToWebNotReparsePoint -Path $parent
    $temporary = Join-Path $parent "$([IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $Value | ConvertTo-Json -Depth $Depth -Compress | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
        [IO.File]::Move($temporary, $Path, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Set-AbodeToWebRuntimeMarkerAcl {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$MarkerPath)

    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) { throw "Runtime-account ownership marker is missing: '$MarkerPath'." }
    Assert-AbodeToWebNotReparsePoint -Path $MarkerPath
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($administratorsSid)
    foreach ($sid in @($systemSid, $administratorsSid)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)) | Out-Null
    }
    Set-Acl -LiteralPath $MarkerPath -AclObject $acl
}

function Assert-AbodeToWebRuntimeOwnershipAcl {
    param([Parameter(Mandatory)][string]$MarkerPath)

    $stateRoot = Split-Path -Parent $MarkerPath
    $dataRoot = Split-Path -Parent $stateRoot
    $programDataRoot = Split-Path -Parent $dataRoot
    if (-not (Test-Path -LiteralPath $stateRoot -PathType Container)) { throw "Runtime-account state directory is missing: '$stateRoot'." }
    foreach ($directory in @($programDataRoot, $dataRoot, $stateRoot)) { Assert-AbodeToWebNotReparsePoint -Path $directory }
    Assert-AbodeToWebNotReparsePoint -Path $MarkerPath
    $administratorsSid = 'S-1-5-32-544'
    $systemSid = 'S-1-5-18'

    $translateOwner = {
        param([object]$Acl)
        try { return [Security.Principal.SecurityIdentifier]::new([string]$Acl.Owner).Value }
        catch { return [Security.Principal.NTAccount]::new([string]$Acl.Owner).Translate([Security.Principal.SecurityIdentifier]).Value }
    }
    $translateRule = {
        param([object]$Rule)
        return $Rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
    }

    foreach ($managedDirectory in @($dataRoot, $stateRoot)) {
        $isDataRoot = $managedDirectory.Equals($dataRoot, [StringComparison]::OrdinalIgnoreCase)
        $directoryAcl = Get-Acl -LiteralPath $managedDirectory
        if ((& $translateOwner $directoryAcl) -ne $administratorsSid -or -not $directoryAcl.AreAccessRulesProtected) {
            throw "Runtime-account ownership directory owner or inheritance is not trusted: '$managedDirectory'."
        }
        $directoryIdentities = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $serviceSidCount = 0
        foreach ($rule in $directoryAcl.Access) {
            $sid = & $translateRule $rule
            $expectedInheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
            if ($rule.IsInherited -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
                throw "Runtime-account ownership directory contains an unexpected access rule: '$managedDirectory'."
            }
            if (-not $directoryIdentities.Add($sid)) { throw "Runtime-account ownership directory contains a duplicate access rule: '$managedDirectory'." }
            if ($sid -in @($systemSid, $administratorsSid)) {
                if ($rule.InheritanceFlags -ne $expectedInheritance -or
                    ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl) {
                    throw "Runtime-account ownership directory trusted principals lack full control: '$managedDirectory'."
                }
            } elseif ($sid.StartsWith('S-1-5-80-', [StringComparison]::Ordinal)) {
                $serviceSidCount++
                $required = [Security.AccessControl.FileSystemRights]::Modify
                $forbidden = [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
                if ($rule.InheritanceFlags -ne $expectedInheritance -or
                    ($rule.FileSystemRights -band $required) -ne $required -or ($rule.FileSystemRights -band $forbidden) -ne 0) {
                    throw "Runtime-account ownership directory service permission is not scoped to Modify: '$managedDirectory'."
                }
            } elseif ($isDataRoot) {
                $dangerous = [Security.AccessControl.FileSystemRights]::Write -bor
                    [Security.AccessControl.FileSystemRights]::Delete -bor
                    [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
                    [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                    [Security.AccessControl.FileSystemRights]::TakeOwnership
                if ($rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
                    ($rule.FileSystemRights -band $dangerous) -ne 0) {
                    throw "Runtime-account data-root grant for '$sid' is broader than non-inheriting traversal: '$managedDirectory'."
                }
            } else {
                throw "Runtime-account ownership directory grants an unexpected principal '$sid': '$managedDirectory'."
            }
        }
        if ((-not $isDataRoot -and $directoryIdentities.Count -ne 3) -or -not $directoryIdentities.Contains($systemSid) -or
            -not $directoryIdentities.Contains($administratorsSid) -or $serviceSidCount -ne 1) {
            throw "Runtime-account ownership directory DACL is incomplete or ambiguous: '$managedDirectory'."
        }
    }

    # A parent DeleteChild right can replace a protected child regardless of the
    # child's own DACL. Reject a host-wide ProgramData policy that grants it to an
    # untrusted principal; installation cannot safely prove marker ownership there.
    $deleteChild = [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    foreach ($rule in (Get-Acl -LiteralPath $programDataRoot).Access) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            ($rule.FileSystemRights -band $deleteChild) -eq 0) { continue }
        $sid = & $translateRule $rule
        if ($sid -notin @($systemSid, $administratorsSid)) {
            throw "ProgramData grants DeleteChild to untrusted principal '$sid'; runtime-account marker ownership cannot be proven safely."
        }
    }

    $markerAcl = Get-Acl -LiteralPath $MarkerPath
    if ((& $translateOwner $markerAcl) -ne $administratorsSid -or -not $markerAcl.AreAccessRulesProtected) {
        throw "Runtime-account ownership marker owner or inheritance is not trusted: '$MarkerPath'."
    }
    $markerIdentities = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($rule in $markerAcl.Access) {
        $sid = & $translateRule $rule
        if ($rule.IsInherited -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or
            $sid -notin @($systemSid, $administratorsSid) -or
            ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl -or
            -not $markerIdentities.Add($sid)) {
            throw "Runtime-account ownership marker DACL is not exactly SYSTEM and Administrators full control: '$MarkerPath'."
        }
    }
    if ($markerIdentities.Count -ne 2 -or -not $markerIdentities.Contains($systemSid) -or -not $markerIdentities.Contains($administratorsSid)) {
        throw "Runtime-account ownership marker DACL is incomplete: '$MarkerPath'."
    }
}

function New-AbodeToWebProtectedRuntimeCredential {
    param(
        [Parameter(Mandatory)][string]$PlainPassword,
        [Parameter(Mandatory)][string]$AccountName,
        [Parameter(Mandatory)][string]$Path
    )

    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($PlainPassword)
    $entropy = [Text.Encoding]::UTF8.GetBytes($script:RuntimeCredentialEntropyText)
    $protectedBytes = $null
    try {
        if ($passwordBytes.Length -lt 32) { throw 'The generated runtime password did not meet the 32-byte minimum.' }
        $protectedBytes = [Security.Cryptography.ProtectedData]::Protect(
            $passwordBytes,
            $entropy,
            [Security.Cryptography.DataProtectionScope]::LocalMachine)
        $envelope = [ordered]@{
            version = 1
            protection = 'dpapi-local-machine-v1'
            username = $AccountName
            domain = '.'
            protectedPassword = [Convert]::ToBase64String($protectedBytes)
        }
        Write-AbodeToWebAtomicJson -Value $envelope -Path $Path -Depth 2
    } finally {
        if ($null -ne $passwordBytes) { [Array]::Clear($passwordBytes, 0, $passwordBytes.Length) }
        if ($null -ne $entropy) { [Array]::Clear($entropy, 0, $entropy.Length) }
        if ($null -ne $protectedBytes) { [Array]::Clear($protectedBytes, 0, $protectedBytes.Length) }
    }
}

function Read-AbodeToWebRuntimeCredentialPassword {
    param(
        [Parameter(Mandatory)][string]$CredentialPath,
        [Parameter(Mandatory)][string]$AccountName
    )

    if (-not (Test-Path -LiteralPath $CredentialPath -PathType Leaf)) { throw "Runtime credential is missing: '$CredentialPath'." }
    Assert-AbodeToWebNotReparsePoint -Path $CredentialPath
    $document = $null
    $fileBytes = $null
    try {
        $fileBytes = [IO.File]::ReadAllBytes($CredentialPath)
        if ($fileBytes.Length -le 0 -or $fileBytes.Length -gt 32768) { throw 'Envelope size is outside the supported range.' }
        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        $json = $strictUtf8.GetString($fileBytes)
        $options = [Text.Json.JsonDocumentOptions]::new()
        $options.AllowTrailingCommas = $false
        $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
        $options.MaxDepth = 4
        $document = [Text.Json.JsonDocument]::Parse($json, $options)
        $root = $document.RootElement
        if ($root.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Envelope root is not an object.' }

        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($name in @('version', 'protection', 'username', 'domain', 'protectedPassword')) { $expected.Add($name) | Out-Null }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $root.EnumerateObject()) {
            if (-not $expected.Contains($property.Name) -or -not $seen.Add($property.Name)) {
                throw "Envelope contains an unknown, incorrectly cased, or duplicate property '$($property.Name)'."
            }
        }
        if ($seen.Count -ne $expected.Count) { throw 'Envelope is missing one or more required properties.' }

        $versionElement = $root.GetProperty('version')
        [int]$versionNumber = 0
        if ($versionElement.ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            -not $versionElement.TryGetInt32([ref]$versionNumber) -or $versionNumber -ne 1) {
            throw 'Envelope version must be the JSON number 1.'
        }
        $protectionElement = $root.GetProperty('protection')
        $usernameElement = $root.GetProperty('username')
        $domainElement = $root.GetProperty('domain')
        $passwordElement = $root.GetProperty('protectedPassword')
        if ($protectionElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $usernameElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $domainElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $passwordElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $protectionElement.GetString() -cne 'dpapi-local-machine-v1' -or
            $usernameElement.GetString() -cne $AccountName -or
            $domainElement.GetString() -cne '.') {
            throw 'Envelope identity or protection metadata does not match the supported schema.'
        }
        $protectedPasswordText = $passwordElement.GetString()
        if ([string]::IsNullOrEmpty($protectedPasswordText) -or $protectedPasswordText.Length -gt 32768 -or
            @($protectedPasswordText.ToCharArray() | Where-Object { $_ -eq [char]0 -or [char]::IsControl($_) }).Count -gt 0) {
            throw 'Envelope protectedPassword is empty, too long, or contains a control character.'
        }
    } catch {
        throw "Runtime credential envelope does not match the exact version 1 schema: '$CredentialPath'. $($_.Exception.Message)"
    } finally {
        if ($null -ne $document) { $document.Dispose() }
        if ($null -ne $fileBytes) { [Array]::Clear($fileBytes, 0, $fileBytes.Length) }
    }
    try { $protectedBytes = [Convert]::FromBase64String($protectedPasswordText) }
    catch { throw "Runtime credential envelope has invalid protectedPassword encoding: '$CredentialPath'." }
    if ($protectedBytes.Length -eq 0 -or $protectedBytes.Length -gt 16384) {
        throw "Runtime credential envelope has an invalid protectedPassword length: '$CredentialPath'."
    }
    $entropy = [Text.Encoding]::UTF8.GetBytes($script:RuntimeCredentialEntropyText)
    $passwordBytes = $null
    $passwordCharacters = $null
    $securePassword = [Security.SecureString]::new()
    try {
        $passwordBytes = [Security.Cryptography.ProtectedData]::Unprotect(
            $protectedBytes,
            $entropy,
            [Security.Cryptography.DataProtectionScope]::LocalMachine)
        if ($passwordBytes.Length -lt 32 -or $passwordBytes.Length -gt 1024) { throw 'The decrypted runtime credential length is outside the supported range.' }
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $passwordCharacters = $utf8.GetChars($passwordBytes)
        if ($passwordCharacters.Count -eq 0 -or @($passwordCharacters | Where-Object { $_ -eq [char]0 -or [char]::IsControl($_) }).Count -gt 0) {
            throw 'The decrypted runtime credential contains an invalid control character.'
        }
        foreach ($character in $passwordCharacters) { $securePassword.AppendChar($character) }
        $securePassword.MakeReadOnly()
        return $securePassword
    } catch {
        $securePassword.Dispose()
        throw "Runtime credential cannot be decrypted on this machine: '$CredentialPath'. $($_.Exception.Message)"
    } finally {
        if ($null -ne $passwordCharacters) { [Array]::Clear($passwordCharacters, 0, $passwordCharacters.Length) }
        if ($null -ne $passwordBytes) { [Array]::Clear($passwordBytes, 0, $passwordBytes.Length) }
        [Array]::Clear($protectedBytes, 0, $protectedBytes.Length)
        [Array]::Clear($entropy, 0, $entropy.Length)
    }
}

function Set-AbodeToWebRuntimeCredentialAcl {
    param(
        [Parameter(Mandatory)][string]$CredentialPath,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName
    )

    Assert-AbodeToWebNotReparsePoint -Path $CredentialPath
    Set-AbodeToWebExactFileAcl -Path $CredentialPath -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Read
}

function Remove-AbodeToWebRuntimeDeploymentAclTree {
    param(
        [Parameter(Mandatory)][string]$DeploymentsRoot,
        [Parameter(Mandatory)][string]$RuntimeSid
    )

    if (-not (Test-Path -LiteralPath $DeploymentsRoot -PathType Container)) { return }
    Assert-AbodeToWebNotReparsePoint -Path $DeploymentsRoot

    # Runtime grants are placed on individual worktree roots with inheritance. Remove
    # the root first so inherited child ACEs disappear, then inspect every ordinary
    # descendant for a stale explicit ACE. Never follow or modify a project symlink or
    # junction; PowerShell does not traverse them without -FollowSymlink and we skip
    # the reparse-point entry itself.
    Remove-AbodeToWebAclGrant -Path $DeploymentsRoot -Identity "*$RuntimeSid" | Out-Null
    Get-ChildItem -LiteralPath $DeploymentsRoot -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 } |
        ForEach-Object { Remove-AbodeToWebAclGrant -Path $_.FullName -Identity "*$RuntimeSid" | Out-Null }
}

function Set-AbodeToWebRuntimeDataAcl {
    [CmdletBinding()]
    param(
        [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
        [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
        [Parameter(Mandatory)][string]$RuntimeAccountSid,
        [string[]]$PreviousRuntimeAccountSid
    )

    Assert-AbodeToWebStandardDataRoot -Path $DataRoot | Out-Null
    try { $runtimeSid = [Security.Principal.SecurityIdentifier]::new($RuntimeAccountSid).Value }
    catch { throw "Runtime account SID '$RuntimeAccountSid' is invalid." }
    $layout = Get-AbodeToWebLayout -DataRoot $DataRoot
    foreach ($path in @($layout.DataRoot, $layout.DeploymentsRoot, $layout.LogsRoot)) {
        Assert-AbodeToWebNotReparsePoint -Path $path
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    }
    Set-AbodeToWebExactDirectoryAcl -Path $layout.DeploymentsRoot -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Full

    foreach ($oldSidValue in @($PreviousRuntimeAccountSid | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)) {
        try { $oldSid = [Security.Principal.SecurityIdentifier]::new($oldSidValue).Value }
        catch { throw "Previous runtime account SID '$oldSidValue' is invalid." }
        if ($oldSid.Equals($runtimeSid, [StringComparison]::OrdinalIgnoreCase)) { continue }
        foreach ($path in @($layout.DataRoot, $layout.DeploymentsRoot, $layout.LogsRoot)) {
            Remove-AbodeToWebAclGrant -Path $path -Identity "*$oldSid" | Out-Null
        }
        Remove-AbodeToWebRuntimeDeploymentAclTree -DeploymentsRoot $layout.DeploymentsRoot -RuntimeSid $oldSid
    }

    # Install and repair call this while Abode to Web is stopped. Clear any working-tree
    # grants left by an interrupted prior run before restoring only base traversal.
    Remove-AbodeToWebRuntimeDeploymentAclTree -DeploymentsRoot $layout.DeploymentsRoot -RuntimeSid $runtimeSid

    # The account can traverse to a launcher-approved working tree but receives no
    # inherited access to retained releases, logs, configuration, state, secrets,
    # backups, or Cloudflare material. The runtime agent grants Modify only on the
    # particular working-directory tree immediately before launch.
    Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\icacls.exe" -ArgumentList @(
        $layout.DataRoot, '/grant:r', "*${runtimeSid}:RX"
    ) -ErrorMessage 'Unable to grant the runtime identity traverse access to the Abode to Web data root'
    Invoke-AbodeToWebNative -FilePath "$env:SystemRoot\System32\icacls.exe" -ArgumentList @(
        $layout.DeploymentsRoot, '/grant:r', "*${runtimeSid}:RX"
    ) -ErrorMessage 'Unable to grant the runtime identity traverse access to the deployments root'
    Remove-AbodeToWebAclGrant -Path $layout.LogsRoot -Identity "*$runtimeSid" | Out-Null
}

function Remove-AbodeToWebRuntimeDataAcl {
    [CmdletBinding()]
    param(
        [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
        [Parameter(Mandatory)][string]$RuntimeAccountSid
    )

    Assert-AbodeToWebStandardDataRoot -Path $DataRoot | Out-Null
    try { $runtimeSid = [Security.Principal.SecurityIdentifier]::new($RuntimeAccountSid).Value }
    catch { throw "Runtime account SID '$RuntimeAccountSid' is invalid." }
    $layout = Get-AbodeToWebLayout -DataRoot $DataRoot
    foreach ($path in @($layout.DataRoot, $layout.DeploymentsRoot, $layout.LogsRoot)) {
        if (Test-Path -LiteralPath $path -PathType Container) {
            Assert-AbodeToWebNotReparsePoint -Path $path
            Remove-AbodeToWebAclGrant -Path $path -Identity "*$runtimeSid" | Out-Null
        }
    }
    Remove-AbodeToWebRuntimeDeploymentAclTree -DeploymentsRoot $layout.DeploymentsRoot -RuntimeSid $runtimeSid
}

function Get-AbodeToWebLocalUserByName {
    param([Parameter(Mandatory)][string]$AccountName)

    return Get-LocalUser -Name $AccountName -ErrorAction SilentlyContinue
}

function Test-AbodeToWebLocalGroupMembership {
    param(
        [Parameter(Mandatory)][object]$User,
        [Parameter(Mandatory)][string]$GroupSid
    )

    $group = Get-LocalGroup -SID ([Security.Principal.SecurityIdentifier]::new($GroupSid)) -ErrorAction Stop
    $members = @(Get-LocalGroupMember -Group $group -ErrorAction Stop)
    return $members | Where-Object { $null -ne $_.SID -and $_.SID.Value -eq $User.SID.Value } | Select-Object -First 1
}

function Get-AbodeToWebLocalGroupMemberships {
    param([Parameter(Mandatory)][object]$User)

    $memberships = [Collections.Generic.List[object]]::new()
    foreach ($group in Get-LocalGroup -ErrorAction Stop) {
        try { $members = @(Get-LocalGroupMember -Group $group -ErrorAction Stop) }
        catch { throw "Local group '$($group.Name)' membership could not be enumerated safely: $($_.Exception.Message)" }
        if ($members | Where-Object { $null -ne $_.SID -and $_.SID.Value -eq $User.SID.Value } | Select-Object -First 1) {
            $memberships.Add($group)
        }
    }
    return @($memberships)
}

function Set-AbodeToWebRuntimeAccountRestrictions {
    param(
        [Parameter(Mandatory)][object]$User,
        [Parameter(Mandatory)][pscustomobject]$RuntimeLayout
    )

    if (-not $User.Enabled) { Enable-LocalUser -Name $User.Name -ErrorAction Stop }
    Set-LocalUser -Name $User.Name -Description $script:RuntimeAccountDescription -AccountNeverExpires `
        -PasswordNeverExpires $true -UserMayChangePassword $false -ErrorAction Stop

    $usersSid = 'S-1-5-32-545'
    foreach ($membership in Get-AbodeToWebLocalGroupMemberships -User $User) {
        if ($membership.SID.Value -ne $usersSid) {
            Remove-LocalGroupMember -Group $membership -Member $User -Confirm:$false -ErrorAction Stop
        }
    }
    if (-not (Test-AbodeToWebLocalGroupMembership -User $User -GroupSid $usersSid)) {
        $users = Get-LocalGroup -SID ([Security.Principal.SecurityIdentifier]::new($usersSid)) -ErrorAction Stop
        Add-LocalGroupMember -Group $users -Member $User -ErrorAction Stop
    }

    New-Item -Path $RuntimeLayout.HiddenAccountsRegistryPath -Force | Out-Null
    New-ItemProperty -LiteralPath $RuntimeLayout.HiddenAccountsRegistryPath -Name $User.Name -PropertyType DWord -Value 0 -Force | Out-Null
}

function Assert-AbodeToWebRuntimeAccountAvailable {
    [CmdletBinding()]
    param(
        [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
        [string]$AccountName = 'AbodeToWebRuntime'
    )

    $runtimeLayout = Get-AbodeToWebRuntimeAccountLayout -DataRoot $DataRoot -AccountName $AccountName
    $user = Get-AbodeToWebLocalUserByName -AccountName $AccountName
    $marker = Get-AbodeToWebRuntimeAccountMarker -MarkerPath $runtimeLayout.MarkerPath
    if ($null -ne $user -and $null -eq $marker) {
        throw "Local account '$AccountName' already exists without Abode to Web's ownership marker. Refusing to change or use it. Choose a different -RuntimeAccountName or resolve the account manually."
    }
    if ($null -ne $marker -and [string]$marker.accountName -ne $AccountName) {
        throw "Runtime-account marker belongs to '$($marker.accountName)', not '$AccountName'. Refusing to change either account."
    }
    if ($null -ne $user -and -not $user.SID.Value.Equals([string]$marker.accountSid, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Local account '$AccountName' has a different SID than Abode to Web's ownership marker. Refusing to take over or remove the account."
    }
    return $runtimeLayout
}

function Initialize-AbodeToWebRuntimeAccount {
    [CmdletBinding()]
    param(
        [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
        [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
        [string]$AccountName = 'AbodeToWebRuntime',
        [switch]$RotateCredential
    )

    Assert-AbodeToWebAdministrator -Action 'Creating or repairing the dedicated Abode to Web runtime identity'
    Assert-AbodeToWebStandardDataRoot -Path $DataRoot | Out-Null
    $runtimeLayout = Assert-AbodeToWebRuntimeAccountAvailable -DataRoot $DataRoot -AccountName $AccountName
    foreach ($directory in @((Split-Path -Parent $runtimeLayout.CredentialPath), (Split-Path -Parent $runtimeLayout.MarkerPath))) {
        Assert-AbodeToWebNotReparsePoint -Path $directory
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Set-AbodeToWebDataAcl -DataRoot $DataRoot -ServiceName $ServiceName
    Set-AbodeToWebPrivateAcl -Path (Split-Path -Parent $runtimeLayout.MarkerPath) `
        -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Modify
    if (Test-Path -LiteralPath $runtimeLayout.MarkerPath -PathType Leaf) {
        Set-AbodeToWebRuntimeMarkerAcl -MarkerPath $runtimeLayout.MarkerPath
    }
    # DPAPI LocalMachine protects the password cryptographically, but any local
    # principal that can read the blob could ask Windows to decrypt it. Protect the
    # parent before creating even the temporary envelope, not only after the move.
    Set-AbodeToWebExactDirectoryAcl -Path (Split-Path -Parent $runtimeLayout.CredentialPath) `
        -ServiceSid "NT SERVICE\$ServiceName" -ServiceAccess Modify
    if (Test-Path -LiteralPath $runtimeLayout.CredentialPath -PathType Leaf) {
        # The runtime envelope is machine-bound and read-only to the service. Secure
        # it through its distinct policy before any attempt to validate or decrypt it.
        Set-AbodeToWebRuntimeCredentialAcl -CredentialPath $runtimeLayout.CredentialPath -ServiceName $ServiceName
    }

    $user = Get-AbodeToWebLocalUserByName -AccountName $AccountName
    $marker = Get-AbodeToWebRuntimeAccountMarker -MarkerPath $runtimeLayout.MarkerPath
    $previousAccountSid = if ($null -eq $marker) { $null } else { [string]$marker.accountSid }
    $createdThisRun = $false
    $credentialValid = $false
    $oldPassword = $null
    if ($null -ne $user -and (Test-Path -LiteralPath $runtimeLayout.CredentialPath -PathType Leaf)) {
        try {
            $oldPassword = Read-AbodeToWebRuntimeCredentialPassword -CredentialPath $runtimeLayout.CredentialPath -AccountName $AccountName
            $credentialValid = $true
        } catch {
            $credentialValid = $false
        }
    }

    $needsCredential = $null -eq $user -or -not $credentialValid -or $RotateCredential
    $credentialTemporary = Join-Path (Split-Path -Parent $runtimeLayout.CredentialPath) "runtime-credential-$([guid]::NewGuid().ToString('N')).tmp"
    $credentialBackup = $null
    $plainPassword = $null
    $securePassword = $null
    try {
        if ($needsCredential) {
            $randomBytes = [byte[]]::new(48)
            [Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
            try { $plainPassword = [Convert]::ToBase64String($randomBytes) }
            finally { [Array]::Clear($randomBytes, 0, $randomBytes.Length) }
            $securePassword = ConvertTo-SecureString -String $plainPassword -AsPlainText -Force
            New-AbodeToWebProtectedRuntimeCredential -PlainPassword $plainPassword -AccountName $AccountName -Path $credentialTemporary
            $plainPassword = $null

            if ($null -eq $user) {
                New-LocalUser -Name $AccountName -FullName 'AbodeToWeb Runtime' -Description $script:RuntimeAccountDescription `
                    -Password $securePassword -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword -ErrorAction Stop | Out-Null
                $createdThisRun = $true
                $user = Get-LocalUser -Name $AccountName -ErrorAction Stop
            } else {
                if ($credentialValid) {
                    $credentialBackup = Join-Path (Split-Path -Parent $runtimeLayout.CredentialPath) "runtime-credential-$([guid]::NewGuid().ToString('N')).rollback"
                    Copy-Item -LiteralPath $runtimeLayout.CredentialPath -Destination $credentialBackup -Force
                }
                Set-LocalUser -Name $AccountName -Password $securePassword -ErrorAction Stop
            }
            [IO.File]::Move($credentialTemporary, $runtimeLayout.CredentialPath, $true)
        }

        Set-AbodeToWebRuntimeAccountRestrictions -User $user -RuntimeLayout $runtimeLayout
        $createdAt = if ($null -eq $marker) { [DateTimeOffset]::UtcNow.ToString('O') } else { [string]$marker.createdAtUtc }
        Write-AbodeToWebAtomicJson -Path $runtimeLayout.MarkerPath -Value ([ordered]@{
            version = 1
            managedBy = 'AbodeToWeb'
            accountName = $AccountName
            accountSid = $user.SID.Value
            createdAtUtc = $createdAt
        }) -Depth 2
        Set-AbodeToWebRuntimeMarkerAcl -MarkerPath $runtimeLayout.MarkerPath
        Set-AbodeToWebRuntimeCredentialAcl -CredentialPath $runtimeLayout.CredentialPath -ServiceName $ServiceName

        [pscustomobject]@{
            AccountName = $AccountName
            AccountSid = $user.SID.Value
            PreviousAccountSid = $previousAccountSid
            CredentialPath = $runtimeLayout.CredentialPath
            MarkerPath = $runtimeLayout.MarkerPath
            CreatedThisRun = $createdThisRun
            CredentialRotated = $needsCredential -and -not $createdThisRun
            RotationBackupPath = $credentialBackup
        }
    } catch {
        $failure = $_
        if ($createdThisRun) {
            try { Remove-LocalUser -Name $AccountName -ErrorAction Stop } catch { }
            try { Remove-ItemProperty -LiteralPath $runtimeLayout.HiddenAccountsRegistryPath -Name $AccountName -ErrorAction SilentlyContinue } catch { }
            foreach ($path in @($runtimeLayout.CredentialPath, $runtimeLayout.MarkerPath)) {
                try { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } } catch { }
            }
        } elseif ($credentialValid -and $null -ne $oldPassword) {
            try {
                Set-LocalUser -Name $AccountName -Password $oldPassword -ErrorAction Stop
                if ($null -ne $credentialBackup -and (Test-Path -LiteralPath $credentialBackup -PathType Leaf)) {
                    [IO.File]::Move($credentialBackup, $runtimeLayout.CredentialPath, $true)
                }
            } catch { }
        }
        throw $failure
    } finally {
        $plainPassword = $null
        if ($null -ne $securePassword) { $securePassword.Dispose() }
        if ($null -ne $oldPassword) { $oldPassword.Dispose() }
        if (Test-Path -LiteralPath $credentialTemporary) { Remove-Item -LiteralPath $credentialTemporary -Force }
    }
}

function Complete-AbodeToWebRuntimeAccountInitialization {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$InitializationResult)

    $backup = [string]$InitializationResult.RotationBackupPath
    if (-not [string]::IsNullOrWhiteSpace($backup) -and (Test-Path -LiteralPath $backup -PathType Leaf)) {
        Assert-AbodeToWebNotReparsePoint -Path $backup
        Remove-Item -LiteralPath $backup -Force
    }
}

function Restore-AbodeToWebRuntimeAccountRotation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$InitializationResult,
        [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb'
    )

    $backup = [string]$InitializationResult.RotationBackupPath
    if ([string]::IsNullOrWhiteSpace($backup) -or -not (Test-Path -LiteralPath $backup -PathType Leaf)) { return $false }
    $oldPassword = Read-AbodeToWebRuntimeCredentialPassword -CredentialPath $backup -AccountName ([string]$InitializationResult.AccountName)
    try {
        Set-LocalUser -Name ([string]$InitializationResult.AccountName) -Password $oldPassword -ErrorAction Stop
        [IO.File]::Move($backup, [string]$InitializationResult.CredentialPath, $true)
        Set-AbodeToWebRuntimeCredentialAcl -CredentialPath ([string]$InitializationResult.CredentialPath) -ServiceName $ServiceName
        return $true
    } finally {
        $oldPassword.Dispose()
    }
}

function Test-AbodeToWebRuntimeAccount {
    [CmdletBinding()]
    param(
        [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
        [ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb',
        [string]$AccountName = 'AbodeToWebRuntime'
    )

    $runtimeLayout = Get-AbodeToWebRuntimeAccountLayout -DataRoot $DataRoot -AccountName $AccountName
    if (-not (Test-AbodeToWebAdministrator)) {
        return [pscustomobject]@{
            healthy = $null
            verification = 'limited'
            accountName = $AccountName
            accountSid = $null
            accountPresent = $null
            accountEnabled = $null
            ownershipMarkerPresent = $null
            credentialPath = $runtimeLayout.CredentialPath
            configuredCredentialPath = $null
            credentialPathConfigured = $null
            credentialDecryptable = $null
            credentialAclRestricted = $null
            dataAclScoped = $null
            hiddenFromSignIn = $null
            controlPlaneIdentityCompatible = $null
            processLogonSelfTest = 'not-run-server-runtime-self-test-required'
            issues = @()
            notes = @('Deep runtime-identity verification requires an elevated PowerShell 7 session because the marker and credential are intentionally unreadable to standard users.')
        }
    }
    $issues = [Collections.Generic.List[string]]::new()
    $configuredCredentialPath = $null
    try { $configuredCredentialPath = Get-AbodeToWebServiceEnvironmentVariable -ServiceName $ServiceName -Name 'ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH' }
    catch { $issues.Add("Runtime credential service environment could not be read: $($_.Exception.Message)") }
    $credentialPathConfigured = -not [string]::IsNullOrWhiteSpace($configuredCredentialPath) -and
        ([string]$configuredCredentialPath).Equals($runtimeLayout.CredentialPath, [StringComparison]::OrdinalIgnoreCase)
    if (-not $credentialPathConfigured) {
        $issues.Add("Service environment ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH is missing or does not equal '$($runtimeLayout.CredentialPath)'.")
    }
    $serviceDefinition = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    $controlPlaneIdentityCompatible = $null
    if ($null -eq $serviceDefinition) {
        $issues.Add("Windows service '$ServiceName' is missing, so the control-plane identity cannot be verified.")
    } elseif ([string]$serviceDefinition.StartName -in @('LocalSystem', '.\LocalSystem')) {
        $controlPlaneIdentityCompatible = $false
        $issues.Add('Abode to Web runs as LocalSystem; the isolated runtime launcher requires LocalService or a distinct dedicated service account.')
    }
    $user = Get-AbodeToWebLocalUserByName -AccountName $AccountName
    $marker = $null
    try { $marker = Get-AbodeToWebRuntimeAccountMarker -MarkerPath $runtimeLayout.MarkerPath }
    catch { $issues.Add($_.Exception.Message) }
    if ($null -eq $user) { $issues.Add("Dedicated local runtime account '$AccountName' is missing.") }
    if ($null -eq $marker) { $issues.Add('Runtime-account ownership marker is missing or invalid.') }
    if ($null -ne $user -and $null -ne $marker -and -not $user.SID.Value.Equals([string]$marker.accountSid, [StringComparison]::OrdinalIgnoreCase)) {
        $issues.Add('Runtime account SID does not match the AbodeToWeb ownership marker.')
    }
    if ($null -ne $user) {
        if ($null -ne $serviceDefinition -and $null -eq $controlPlaneIdentityCompatible) {
            try {
                $controlPlaneSid = Resolve-AbodeToWebWindowsAccountSid -AccountName ([string]$serviceDefinition.StartName)
                $controlPlaneIdentityCompatible = -not $controlPlaneSid.Equals($user.SID.Value, [StringComparison]::OrdinalIgnoreCase)
                if (-not $controlPlaneIdentityCompatible) { $issues.Add('Abode to Web control-plane service and project runtime identity are the same Windows account.') }
            } catch { $issues.Add("Abode to Web control-plane service account could not be verified: $($_.Exception.Message)") }
        }
        if (-not $user.Enabled) { $issues.Add('Runtime account is disabled.') }
        try {
            $memberships = @(Get-AbodeToWebLocalGroupMemberships -User $user)
            $unexpectedMemberships = @($memberships | Where-Object { $_.SID.Value -ne 'S-1-5-32-545' })
            if ($unexpectedMemberships.Count -gt 0) {
                $issues.Add("Runtime account belongs to unexpected local group(s): $($unexpectedMemberships.Name -join ', ').")
            }
            if (-not ($memberships | Where-Object { $_.SID.Value -eq 'S-1-5-32-545' } | Select-Object -First 1)) {
                $issues.Add('Runtime account is not a member of the local Users group.')
            }
        } catch { $issues.Add("Local-group membership could not be verified: $($_.Exception.Message)") }
    }

    $credentialDecryptable = $false
    try {
        $password = Read-AbodeToWebRuntimeCredentialPassword -CredentialPath $runtimeLayout.CredentialPath -AccountName $AccountName
        $password.Dispose()
        $credentialDecryptable = $true
    } catch { $issues.Add($_.Exception.Message) }

    $credentialAclRestricted = $false
    if (Test-Path -LiteralPath $runtimeLayout.CredentialPath -PathType Leaf) {
        try {
            $allowedSids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) { $allowedSids.Add($sid) | Out-Null }
            $serviceSid = [Security.Principal.NTAccount]::new("NT SERVICE\$ServiceName").Translate([Security.Principal.SecurityIdentifier]).Value
            $allowedSids.Add($serviceSid) | Out-Null
            $acl = Get-Acl -LiteralPath $runtimeLayout.CredentialPath
            try { $ownerSid = [Security.Principal.SecurityIdentifier]::new([string]$acl.Owner).Value }
            catch { $ownerSid = [Security.Principal.NTAccount]::new([string]$acl.Owner).Translate([Security.Principal.SecurityIdentifier]).Value }
            $trustedOwner = $ownerSid -eq 'S-1-5-32-544'
            $actualAllowSids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $rightsBySid = @{}
            $unexpected = $false
            foreach ($rule in $acl.Access) {
                if ($rule.IsInherited -or
                    $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                    $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
                    $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
                    $unexpected = $true
                    continue
                }
                try { $ruleSid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
                catch { $unexpected = $true; continue }
                if (-not $allowedSids.Contains($ruleSid)) { $unexpected = $true }
                $actualAllowSids.Add($ruleSid) | Out-Null
                $currentRights = if ($rightsBySid.ContainsKey($ruleSid)) { [Security.AccessControl.FileSystemRights]$rightsBySid[$ruleSid] } else { [Security.AccessControl.FileSystemRights]0 }
                $rightsBySid[$ruleSid] = $currentRights -bor $rule.FileSystemRights
            }
            $fullControl = [Security.AccessControl.FileSystemRights]::FullControl
            $serviceDangerousRights = [Security.AccessControl.FileSystemRights]::Write -bor
                [Security.AccessControl.FileSystemRights]::Delete -bor
                [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
                [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                [Security.AccessControl.FileSystemRights]::TakeOwnership
            $serviceRequiredRights = [Security.AccessControl.FileSystemRights]::ReadData -bor
                [Security.AccessControl.FileSystemRights]::ReadAttributes -bor
                [Security.AccessControl.FileSystemRights]::ReadPermissions
            $systemRights = if ($rightsBySid.ContainsKey('S-1-5-18')) { [Security.AccessControl.FileSystemRights]$rightsBySid['S-1-5-18'] } else { [Security.AccessControl.FileSystemRights]0 }
            $adminRights = if ($rightsBySid.ContainsKey('S-1-5-32-544')) { [Security.AccessControl.FileSystemRights]$rightsBySid['S-1-5-32-544'] } else { [Security.AccessControl.FileSystemRights]0 }
            $serviceRights = if ($rightsBySid.ContainsKey($serviceSid)) { [Security.AccessControl.FileSystemRights]$rightsBySid[$serviceSid] } else { [Security.AccessControl.FileSystemRights]0 }
            $rightsAreCorrect = (($systemRights -band $fullControl) -eq $fullControl) -and
                (($adminRights -band $fullControl) -eq $fullControl) -and
                (($serviceRights -band $serviceRequiredRights) -eq $serviceRequiredRights) -and
                (($serviceRights -band $serviceDangerousRights) -eq 0)
            $credentialAclRestricted = $trustedOwner -and $acl.AreAccessRulesProtected -and -not $unexpected -and
                $rightsAreCorrect -and
                ($allowedSids | Where-Object { -not $actualAllowSids.Contains($_) } | Measure-Object).Count -eq 0
            if (-not $credentialAclRestricted) { $issues.Add('Runtime credential owner or ACL is not the required Administrators owner with SYSTEM/Administrators full control and Abode to Web service read-only access.') }
        } catch { $issues.Add("Runtime credential ACL could not be verified: $($_.Exception.Message)") }
    }

    $hidden = $false
    try {
        $hiddenValue = Get-ItemPropertyValue -LiteralPath $runtimeLayout.HiddenAccountsRegistryPath -Name $AccountName -ErrorAction SilentlyContinue
        $hidden = $null -ne $hiddenValue -and [int]$hiddenValue -eq 0
    } catch { }
    if ($null -ne $user -and -not $hidden) { $issues.Add('Runtime account is not hidden from the Windows sign-in chooser.') }

    $dataAclScoped = $false
    if ($null -ne $user) {
        try {
            $abodeToWebLayout = Get-AbodeToWebLayout -DataRoot $DataRoot
            $runtimeSidValue = $user.SID.Value
            $getRuntimeRules = {
                param([string]$Path)
                if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return @() }
                return @((Get-Acl -LiteralPath $Path).Access | Where-Object {
                    try { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $runtimeSidValue }
                    catch { $false }
                })
            }
            $dataRootRules = @(& $getRuntimeRules $abodeToWebLayout.DataRoot)
            $deploymentRootRules = @(& $getRuntimeRules $abodeToWebLayout.DeploymentsRoot)
            $logsRootRules = @(& $getRuntimeRules $abodeToWebLayout.LogsRoot)
            $sensitiveRules = @(
                @(& $getRuntimeRules $abodeToWebLayout.ConfigRoot) +
                @(& $getRuntimeRules $abodeToWebLayout.StateRoot) +
                @(& $getRuntimeRules $abodeToWebLayout.SecretsRoot) +
                @(& $getRuntimeRules $abodeToWebLayout.BackupsRoot) +
                @(& $getRuntimeRules $abodeToWebLayout.CloudflareRoot)
            )
            $dangerousRights = [Security.AccessControl.FileSystemRights]::Write -bor
                [Security.AccessControl.FileSystemRights]::Delete -bor
                [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
                [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                [Security.AccessControl.FileSystemRights]::TakeOwnership
            $rootRulesAreTraverseOnly = $dataRootRules.Count -gt 0 -and -not @($dataRootRules | Where-Object {
                $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                $_.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
                ($_.FileSystemRights -band $dangerousRights) -ne 0
            }).Count
            $deploymentRulesAreTraverseOnly = $deploymentRootRules.Count -gt 0 -and -not @($deploymentRootRules | Where-Object {
                $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                $_.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
                ($_.FileSystemRights -band $dangerousRights) -ne 0
            }).Count
            $dataAclScoped = $rootRulesAreTraverseOnly -and $deploymentRulesAreTraverseOnly -and
                $logsRootRules.Count -eq 0 -and $sensitiveRules.Count -eq 0
            if (-not $dataAclScoped) {
                $issues.Add('Runtime account data ACLs are broader than traverse-only DataRoot/deployments access or reach logs/config/state/secrets/backups/tunnel material.')
            }
        } catch { $issues.Add("Runtime account data ACL scope could not be verified: $($_.Exception.Message)") }
    }

    [pscustomobject]@{
        healthy = $issues.Count -eq 0
        verification = 'full'
        accountName = $AccountName
        accountSid = if ($null -eq $user) { $null } else { $user.SID.Value }
        accountPresent = $null -ne $user
        accountEnabled = $null -ne $user -and $user.Enabled
        ownershipMarkerPresent = $null -ne $marker
        credentialPath = $runtimeLayout.CredentialPath
        configuredCredentialPath = $configuredCredentialPath
        credentialPathConfigured = $credentialPathConfigured
        credentialDecryptable = $credentialDecryptable
        credentialAclRestricted = $credentialAclRestricted
        dataAclScoped = $dataAclScoped
        hiddenFromSignIn = $hidden
        controlPlaneIdentityCompatible = $controlPlaneIdentityCompatible
        processLogonSelfTest = 'not-run-server-runtime-self-test-required'
        issues = @($issues)
        notes = @()
    }
}

function Remove-AbodeToWebRuntimeAccount {
    [CmdletBinding()]
    param(
        [string]$DataRoot = (Join-Path $env:ProgramData 'AbodeToWeb'),
        [string]$AccountName = 'AbodeToWebRuntime'
    )

    Assert-AbodeToWebAdministrator -Action 'Removing Abode to Web-owned runtime identity'
    $runtimeLayout = Get-AbodeToWebRuntimeAccountLayout -DataRoot $DataRoot -AccountName $AccountName
    $marker = Get-AbodeToWebRuntimeAccountMarker -MarkerPath $runtimeLayout.MarkerPath
    $user = Get-AbodeToWebLocalUserByName -AccountName $AccountName
    if ($null -eq $marker) {
        return [pscustomobject]@{ removed = $false; owned = $false; reason = 'Ownership marker is missing; the account was not changed.' }
    }
    if ([string]$marker.accountName -ne $AccountName) {
        throw "Runtime-account marker belongs to '$($marker.accountName)'; '$AccountName' was not changed."
    }
    if ($null -ne $user -and -not $user.SID.Value.Equals([string]$marker.accountSid, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime account '$AccountName' no longer matches Abode to Web's ownership SID. Refusing to remove it."
    }

    if ($null -ne $user) {
        Remove-LocalUser -Name $AccountName -ErrorAction Stop
        if ($null -ne (Get-AbodeToWebLocalUserByName -AccountName $AccountName)) { throw "Runtime account '$AccountName' still exists after removal." }
    }
    Remove-ItemProperty -LiteralPath $runtimeLayout.HiddenAccountsRegistryPath -Name $AccountName -ErrorAction SilentlyContinue
    foreach ($path in @($runtimeLayout.CredentialPath, $runtimeLayout.MarkerPath)) {
        if (Test-Path -LiteralPath $path) {
            Assert-AbodeToWebNotReparsePoint -Path $path
            Remove-Item -LiteralPath $path -Force
        }
    }
    return [pscustomobject]@{ removed = $null -ne $user; owned = $true; reason = $null }
}

Export-ModuleMember -Function @(
    'Assert-AbodeToWebRuntimeAccountAvailable',
    'Complete-AbodeToWebRuntimeAccountInitialization',
    'Get-AbodeToWebRuntimeAccountLayout',
    'Get-AbodeToWebRuntimeAccountMarker',
    'Initialize-AbodeToWebRuntimeAccount',
    'Remove-AbodeToWebRuntimeDataAcl',
    'Remove-AbodeToWebRuntimeAccount',
    'Restore-AbodeToWebRuntimeAccountRotation',
    'Set-AbodeToWebRuntimeCredentialAcl',
    'Set-AbodeToWebRuntimeDataAcl',
    'Set-AbodeToWebRuntimeMarkerAcl',
    'Test-AbodeToWebRuntimeAccount'
)
