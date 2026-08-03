#requires -Version 7.0
<#
Read-only preflight for a commit that may be deployed by AbodeToWeb.

This legacy-named helper never stages, commits, pushes, deploys, contacts the
dashboard, or forwards Windows credentials. It verifies that the working tree
is clean, identifies the exact reviewed commit, and prints the explicit push
command for the operator (or their LLM) to review and run separately.

    pwsh -File scripts/publish-project.ps1 -RepositoryPath .
    pwsh -File scripts/publish-project.ps1 -RepositoryPath C:\src\my-app -ExpectedBranch main
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RepositoryPath,
    [ValidatePattern('^[A-Za-z0-9._-]+$')][string]$Remote = 'origin',
    [string]$ExpectedBranch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedPath = (Resolve-Path -LiteralPath $RepositoryPath -ErrorAction Stop).Path
$gitExecutable = @(Get-Command git.exe -CommandType Application -All -ErrorAction Stop)[0].Source

function Invoke-ReadOnlyGit {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $previousOptionalLocks = [Environment]::GetEnvironmentVariable('GIT_OPTIONAL_LOCKS', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('GIT_OPTIONAL_LOCKS', '0', 'Process')
        $output = @(& $gitExecutable `
            -c 'core.fsmonitor=false' `
            -c 'core.hooksPath=NUL' `
            -c 'core.pager=cat' `
            -c 'pager.status=false' `
            -C $script:repositoryRoot @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "The read-only Git check failed: git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
        }
        return $output
    }
    finally {
        [Environment]::SetEnvironmentVariable('GIT_OPTIONAL_LOCKS', $previousOptionalLocks, 'Process')
    }
}

function Format-PowerShellLiteral {
    param([Parameter(Mandatory)][string]$Value)
    return "'$($Value.Replace("'", "''"))'"
}

# Resolve the repository root without loading project-controlled aliases,
# hooks, a file-system monitor, or a pager.
$script:repositoryRoot = $resolvedPath
$inside = @(Invoke-ReadOnlyGit @('rev-parse', '--is-inside-work-tree'))[-1].ToString().Trim()
if ($inside -ne 'true') {
    throw "The path is not a Git working tree: $resolvedPath"
}

$script:repositoryRoot = @(Invoke-ReadOnlyGit @('rev-parse', '--path-format=absolute', '--show-toplevel'))[-1].ToString().Trim()
$branch = @(Invoke-ReadOnlyGit @('rev-parse', '--abbrev-ref', 'HEAD'))[-1].ToString().Trim()
if ($branch -eq 'HEAD' -or [string]::IsNullOrWhiteSpace($branch)) {
    throw 'The repository is in detached-HEAD state. Check out the reviewed deployment branch first.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedBranch) -and $branch -cne $ExpectedBranch) {
    throw "The repository is on '$branch'; expected '$ExpectedBranch'."
}

$pending = @(Invoke-ReadOnlyGit @('status', '--porcelain=v1', '--untracked-files=all'))
if (-not [string]::IsNullOrWhiteSpace(($pending -join [Environment]::NewLine))) {
    Write-Output 'Working tree changes detected:'
    $pending | ForEach-Object { Write-Output "  $_" }
    throw 'The working tree is not clean. Review and commit changes yourself before continuing.'
}

$sha = @(Invoke-ReadOnlyGit @('rev-parse', '--verify', 'HEAD^{commit}'))[-1].ToString().Trim().ToLowerInvariant()
if ($sha -notmatch '^[0-9a-f]{40,64}$') {
    throw 'Git did not return a valid commit ID.'
}
$subject = (@(Invoke-ReadOnlyGit @('log', '-1', '--format=%s')) -join [Environment]::NewLine).Trim()
Invoke-ReadOnlyGit @('remote', 'get-url', $Remote) | Out-Null

$quotedRoot = Format-PowerShellLiteral $repositoryRoot
$quotedRemote = Format-PowerShellLiteral $Remote
$quotedRefspec = Format-PowerShellLiteral "$sha`:refs/heads/$branch"

Write-Output 'Read-only deployment preflight passed.'
Write-Output "Repository : $repositoryRoot"
Write-Output "Branch     : $branch"
Write-Output "Commit     : $sha"
Write-Output "Summary    : $subject"
Write-Output "Remote     : $Remote (configured; URL intentionally not displayed)"
Write-Output ''
Write-Output 'Nothing was staged, committed, pushed, deployed, or sent to the Abode to Web dashboard.'
Write-Output 'Review this exact commit. Only after you approve it, run the following command yourself:'
Write-Output "git.exe -C $quotedRoot push --verify $quotedRemote $quotedRefspec"
Write-Output ''
Write-Output 'After the push succeeds, choose Deploy in Abode to Web. It will fetch and deploy the exact remote commit through the health-gated pipeline.'
