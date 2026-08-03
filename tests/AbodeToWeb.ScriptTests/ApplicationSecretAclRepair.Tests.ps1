#requires -Version 7.0
[CmdletBinding()]
param([ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$ServiceName = 'AbodeToWeb')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$modulePath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts\AbodeToWeb.Common.psm1'
$module = Import-Module $modulePath -Force -PassThru

function Assert-Test {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Case)
    try {
        & $Action
    } catch {
        return
    }
    throw "Expected application-secret enumeration to reject $Case."
}

function Get-TestApplicationSecretFiles {
    param([Parameter(Mandatory)][string]$Root)
    return @(& $module { param($SecretsRoot) Get-AbodeToWebApplicationSecretFiles -SecretsRoot $SecretsRoot } $Root)
}

function New-TestRoot {
    $root = Join-Path $env:TEMP "abode-to-web-secret-acl-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
    return $root
}

$roots = [Collections.Generic.List[string]]::new()
try {
    $validRoot = New-TestRoot
    $roots.Add($validRoot)
    [IO.File]::WriteAllText((Join-Path $validRoot 'sample-static.env'), 'opaque-app-test-data')
    [IO.File]::WriteAllText((Join-Path $validRoot 'runtime-credential.json'), 'opaque-runtime-test-data')
    $valid = @(Get-TestApplicationSecretFiles -Root $validRoot)
    Assert-Test ($valid.Count -eq 1 -and $valid[0].FileName -ceq 'sample-static.env') `
        'Enumeration did not return exactly the regular application secret file.'

    $directoryRoot = New-TestRoot
    $roots.Add($directoryRoot)
    New-Item -ItemType Directory -Path (Join-Path $directoryRoot 'nested') | Out-Null
    Assert-Rejected -Case 'a nested directory' -Action { Get-TestApplicationSecretFiles -Root $directoryRoot | Out-Null }

    $unexpectedRoot = New-TestRoot
    $roots.Add($unexpectedRoot)
    [IO.File]::WriteAllText((Join-Path $unexpectedRoot 'notes.txt'), 'not-a-secret')
    Assert-Rejected -Case 'an unexpected filename' -Action { Get-TestApplicationSecretFiles -Root $unexpectedRoot | Out-Null }

    $invalidAppIdRoot = New-TestRoot
    $roots.Add($invalidAppIdRoot)
    [IO.File]::WriteAllText((Join-Path $invalidAppIdRoot 'Sample-Static.env'), 'opaque-app-test-data')
    Assert-Rejected -Case 'an invalid application-id filename' -Action {
        Get-TestApplicationSecretFiles -Root $invalidAppIdRoot | Out-Null
    }

    $hardLinkRoot = New-TestRoot
    $roots.Add($hardLinkRoot)
    $outsideTarget = Join-Path $env:TEMP "abode-to-web-secret-hardlink-$([guid]::NewGuid().ToString('N')).target"
    try {
        [IO.File]::WriteAllText($outsideTarget, 'opaque-hardlink-test-data')
        New-Item -ItemType HardLink -Path (Join-Path $hardLinkRoot 'sample-static.env') -Target $outsideTarget | Out-Null
        Assert-Rejected -Case 'a hard link' -Action { Get-TestApplicationSecretFiles -Root $hardLinkRoot | Out-Null }
    } finally {
        if (Test-Path -LiteralPath $outsideTarget) { Remove-Item -LiteralPath $outsideTarget -Force }
    }

    $linkRoot = New-TestRoot
    $roots.Add($linkRoot)
    $linkTarget = New-TestRoot
    $roots.Add($linkTarget)
    try {
        New-Item -ItemType SymbolicLink -Path (Join-Path $linkRoot 'sample-static.env') -Target $linkTarget -ErrorAction Stop | Out-Null
        Assert-Rejected -Case 'a reparse point' -Action { Get-TestApplicationSecretFiles -Root $linkRoot | Out-Null }
    } catch [System.UnauthorizedAccessException] {
        Write-Warning 'Reparse-point case skipped because this test process cannot create a symbolic link.'
    }

    $servicePrincipal = "NT SERVICE\$ServiceName"
    $serviceSid = $null
    try { $serviceSid = Resolve-AbodeToWebWindowsAccountSid -AccountName $servicePrincipal }
    catch { Write-Warning "Exact ACL/idempotency case skipped because $servicePrincipal is unavailable." }
    if ($null -ne $serviceSid -and (Test-AbodeToWebAdministrator)) {
        Set-AbodeToWebExactFileAcl -Path (Join-Path $validRoot 'runtime-credential.json') `
            -ServiceSid $servicePrincipal -ServiceAccess Read
        $appHash = (Get-FileHash -LiteralPath (Join-Path $validRoot 'sample-static.env') -Algorithm SHA256).Hash
        $runtimeHash = (Get-FileHash -LiteralPath (Join-Path $validRoot 'runtime-credential.json') -Algorithm SHA256).Hash
        $runtimeAcl = (Get-Acl -LiteralPath (Join-Path $validRoot 'runtime-credential.json')).Sddl

        $first = @(Repair-AbodeToWebApplicationSecretFileAcls -SecretsRoot $validRoot -ServiceName $ServiceName)
        $firstAppAcl = (Get-Acl -LiteralPath (Join-Path $validRoot 'sample-static.env')).Sddl
        $second = @(Repair-AbodeToWebApplicationSecretFileAcls -SecretsRoot $validRoot -ServiceName $ServiceName)
        $secondAppAcl = (Get-Acl -LiteralPath (Join-Path $validRoot 'sample-static.env')).Sddl

        Assert-Test ($first.Count -eq 1 -and $second.Count -eq 1) 'Repair did not report exactly one application secret.'
        Assert-Test ($firstAppAcl -ceq $secondAppAcl) 'Repeated repair changed the exact application secret ACL.'
        Assert-Test ((Get-FileHash -LiteralPath (Join-Path $validRoot 'sample-static.env') -Algorithm SHA256).Hash -ceq $appHash) `
            'Repair changed application secret content.'
        Assert-Test ((Get-FileHash -LiteralPath (Join-Path $validRoot 'runtime-credential.json') -Algorithm SHA256).Hash -ceq $runtimeHash) `
            'Repair changed runtime credential content.'
        Assert-Test ((Get-Acl -LiteralPath (Join-Path $validRoot 'runtime-credential.json')).Sddl -ceq $runtimeAcl) `
            'Application secret repair changed the distinct runtime credential ACL.'
    } elseif ($null -ne $serviceSid) {
        Write-Warning 'Exact ACL/idempotency case skipped because setting protected owners requires elevation.'
    }

    Write-Host 'Application secret ACL repair tests passed.'
} finally {
    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}
