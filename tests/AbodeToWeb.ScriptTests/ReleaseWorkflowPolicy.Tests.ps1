#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Test {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$workflowPath = Join-Path $repositoryRoot '.github\workflows\release.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$githubGuidePath = Join-Path $repositoryRoot 'docs\GITHUB.md'
$githubGuide = Get-Content -LiteralPath $githubGuidePath -Raw

foreach ($required in @(
    'actions: read',
    '- name: Verify trusted tag promotion',
    'REQUIRED_CI_WORKFLOW: ci.yml',
    'TRUSTED_RELEASE_BRANCH: main',
    'git cat-file -t $env:GITHUB_REF',
    'git rev-parse --verify "${env:GITHUB_REF}^{tag}"',
    'git merge-base --is-ancestor $tagCommit $trustedRef',
    'actions/workflows/$env:REQUIRED_CI_WORKFLOW/runs?branch=$env:TRUSTED_RELEASE_BRANCH&event=push&head_sha=$tagCommit',
    '$_.head_branch -ne $env:TRUSTED_RELEASE_BRANCH',
    '$_.head_branch -eq $env:TRUSTED_RELEASE_BRANCH -and $_.conclusion -eq ''success''',
    'environment:',
    'name: release',
    '- name: Recheck approved tag object',
    'EXPECTED_TAG_OBJECT_SHA: ${{ needs.package.outputs.tag_object_sha }}',
    '[Uri]::EscapeDataString($env:GITHUB_REF_NAME)',
    '/git/ref/tags/$escapedTagName',
    '$reference.object.sha -cne $env:EXPECTED_TAG_OBJECT_SHA',
    'is_prerelease: ${{ steps.version.outputs.is_prerelease }}',
    '- name: Create draft GitHub release',
    "'--draft'",
    "'artifacts/SHA256SUMS'",
    '- name: Publish complete GitHub release',
    "'--draft=false'",
    "'--latest'",
    'contents: write'
)) {
    Assert-Test ($workflow.Contains($required, [StringComparison]::Ordinal)) "Release workflow is missing '$required'."
}

foreach ($required in @(
    'enable GitHub immutable releases',
    'create a `v*` tag ruleset that blocks tag updates and deletions',
    'The ruleset is an external prerequisite',
    'creates a draft release, uploads the ZIP and `SHA256SUMS`, then publishes only after those uploads succeed'
)) {
    Assert-Test ($githubGuide.Contains($required, [StringComparison]::Ordinal)) "GitHub release guide is missing '$required'."
}

$promotion = [regex]::Match($workflow, '(?ms)^      - name: Verify trusted tag promotion\r?\n.*?(?=^      - name: Scan release source for secrets)')
Assert-Test $promotion.Success 'Could not isolate the trusted tag promotion step.'
Assert-Test ($promotion.Index -lt $workflow.IndexOf('- name: Scan release source for secrets', [StringComparison]::Ordinal)) `
    'The trusted tag promotion gate must run before package validation.'

$run = [regex]::Match($promotion.Value, '(?ms)^        run: \|\r?\n(?<script>(?:^          .*\r?\n?)+)')
Assert-Test $run.Success 'Could not isolate the trusted tag promotion PowerShell.'
$scriptText = $run.Groups['script'].Value -replace '(?m)^          ', ''
$tokens = $null
$errors = $null
[Management.Automation.Language.Parser]::ParseInput($scriptText, [ref] $tokens, [ref] $errors) | Out-Null
if ($errors.Count -ne 0) {
    throw "Trusted tag promotion PowerShell did not parse: $($errors.Message -join '; ')"
}

$tagRecheck = [regex]::Match($workflow, '(?ms)^      - name: Recheck approved tag object\r?\n.*?(?=^      - name: Create draft GitHub release)')
Assert-Test $tagRecheck.Success 'Could not isolate the tag-object recheck step.'
$recheckRun = [regex]::Match($tagRecheck.Value, '(?ms)^        run: \|\r?\n(?<script>(?:^          .*\r?\n?)+)')
Assert-Test $recheckRun.Success 'Could not isolate the tag-object recheck PowerShell.'
$recheckScript = $recheckRun.Groups['script'].Value -replace '(?m)^          ', ''
$tokens = $null
$errors = $null
[Management.Automation.Language.Parser]::ParseInput($recheckScript, [ref] $tokens, [ref] $errors) | Out-Null
if ($errors.Count -ne 0) {
    throw "Tag-object recheck PowerShell did not parse: $($errors.Message -join '; ')"
}

$draftRelease = [regex]::Match($workflow, '(?ms)^      - name: Create draft GitHub release\r?\n.*?(?=^      - name: Publish complete GitHub release)')
Assert-Test $draftRelease.Success 'Could not isolate the draft-release creation step.'
Assert-Test ($draftRelease.Value.Contains("'--draft'", [StringComparison]::Ordinal)) `
    'The release must be created as a draft before assets become immutable.'
Assert-Test ($draftRelease.Value.Contains('$archive.FullName', [StringComparison]::Ordinal) -and
    $draftRelease.Value.Contains("'artifacts/SHA256SUMS'", [StringComparison]::Ordinal)) `
    'The draft release must attach the archive and SHA256SUMS before publication.'
$draftRun = [regex]::Match($draftRelease.Value, '(?ms)^        run: \|\r?\n(?<script>(?:^          .*\r?\n?)+)')
Assert-Test $draftRun.Success 'Could not isolate the draft-release PowerShell.'
$draftScript = $draftRun.Groups['script'].Value -replace '(?m)^          ', ''
$tokens = $null
$errors = $null
[Management.Automation.Language.Parser]::ParseInput($draftScript, [ref] $tokens, [ref] $errors) | Out-Null
if ($errors.Count -ne 0) {
    throw "Draft-release PowerShell did not parse: $($errors.Message -join '; ')"
}

$publishRelease = [regex]::Match($workflow, '(?ms)^      - name: Publish complete GitHub release\r?\n.*\z')
Assert-Test $publishRelease.Success 'Could not isolate the final release-publication step.'
Assert-Test ($publishRelease.Value.Contains("'release', 'edit', `$env:GITHUB_REF_NAME, '--draft=false'", [StringComparison]::Ordinal)) `
    'The completed release must be published from the draft only after asset uploads succeed.'
$publishRun = [regex]::Match($publishRelease.Value, '(?ms)^        run: \|\r?\n(?<script>(?:^          .*\r?\n?)+)')
Assert-Test $publishRun.Success 'Could not isolate the release-publication PowerShell.'
$publishScript = $publishRun.Groups['script'].Value -replace '(?m)^          ', ''
$tokens = $null
$errors = $null
[Management.Automation.Language.Parser]::ParseInput($publishScript, [ref] $tokens, [ref] $errors) | Out-Null
if ($errors.Count -ne 0) {
    throw "Release-publication PowerShell did not parse: $($errors.Message -join '; ')"
}

$release = [regex]::Match($workflow, '(?ms)^  release:\r?\n.*\z')
Assert-Test $release.Success 'Could not isolate the write-capable release job.'
Assert-Test ($release.Value -match '(?ms)^    environment:\r?\n      name: release\r?\n    permissions:\r?\n      contents: write') `
    'The write-capable release job must be protected by the release environment.'
$releaseStepNames = [string[]]@(
    [regex]::Matches($release.Value, '(?m)^      - name: (?<name>.+)$') |
        ForEach-Object { $_.Groups['name'].Value }
)
$recheckIndex = [Array]::IndexOf($releaseStepNames, 'Recheck approved tag object')
$draftIndex = [Array]::IndexOf($releaseStepNames, 'Create draft GitHub release')
$publishIndex = [Array]::IndexOf($releaseStepNames, 'Publish complete GitHub release')
Assert-Test ($recheckIndex -ge 0 -and $draftIndex -ge 0 -and $publishIndex -ge 0 -and
    $recheckIndex + 1 -eq $draftIndex -and $draftIndex + 1 -eq $publishIndex) `
    'The tag-object recheck must happen immediately before draft creation, followed by publication.'

Write-Host 'Release workflow promotion-policy tests passed.'
