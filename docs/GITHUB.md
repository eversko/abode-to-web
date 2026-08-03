# GitHub

GitHub is used for Abode to Web source, releases, issues, and optional project remotes. Abode to Web shells out to Git and GitHub CLI/Git Credential Manager; it does not store a GitHub password or personal access token in its manifest or database.

## Create the private Abode to Web repository

From the local Abode to Web repository, authenticate interactively:

```powershell
gh auth login --hostname github.com --git-protocol https --web
gh auth status --hostname github.com
```

If no suitable `origin` exists, create a private repository and push `main`:

```powershell
gh repo create eversko/abode-to-web --private --source . --remote origin
git push -u origin main
```

If the repository already exists:

```powershell
git remote add origin https://github.com/eversko/abode-to-web.git
git push -u origin main
```

Do not use `--public` until you have deliberately approved publication, reviewed history, run secret scanning, and accepted the support/security implications.

## Before pushing

```powershell
git status --short
git diff --check
dotnet test AbodeToWeb.sln --configuration Release
Set-Location src/AbodeToWeb.Web
npm ci
npm run lint
npm test
npm run build
Set-Location ../..
```

CI repeats backend build/tests, frontend lint/tests/build, NuGet/npm vulnerability audits, formatting, and full-history secret scanning. Dependabot proposes NuGet, npm, and GitHub Actions updates. Tagged releases publish a self-contained Windows ZIP, `SHA256SUMS`, and—when the repository is public—a GitHub build-provenance attestation.

## Project repository authentication

Git fetch, resolve, and protected-mirror management stay in the trusted Abode to Web service. Git and GitHub CLI authentication is therefore scoped to the Windows account running that service. The default LocalService identity cannot use credentials from your interactive profile. Public repositories need no credential. For private repositories, run the Abode to Web service under a dedicated non-administrator account as described in [Installation](INSTALLATION.md#private-git-repositories), then authenticate GitHub while signed in as that account.

Do not authenticate the managed `AbodeToWebRuntime` user. That separate account runs dependency installation, build, and application start commands through `CreateProcessWithLogonW`; it is intentionally denied Git credentials and development-repository access. Abode to Web gives it a narrow, temporary ACL only on the deployment working tree.

Prefer Git Credential Manager or `gh auth setup-git`. Never paste a token into YAML, an environment value, a start command, an issue, or a deployment log.

Useful checks from the control-plane service account's session:

```powershell
gh auth status --hostname github.com
git ls-remote https://github.com/OWNER/REPOSITORY.git HEAD
```

## Deployment behavior

For **Check for updates**, Abode to Web fetches with prune, resolves the configured remote branch, compares exact SHAs, and records branch, remote head, commit message, and author. GitHub downtime or an authentication failure becomes an actionable status; it does not stop the active deployment.

For **Deploy**, Abode to Web:

1. takes a per-project operation lock;
2. validates the configured repository and fixed remote/branch;
3. uses the service identity/Git Credential Manager to run `git fetch --prune` through a protected bare mirror;
4. resolves one exact commit;
5. materializes an isolated worktree under ProgramData;
6. grants `AbodeToWebRuntime` Modify on that deployment working tree, then installs, builds, and starts the candidate with the narrower identity;
7. switches the gateway only after health succeeds;
8. keeps the grant while a dynamic release may run or restart, revokes it on normal stop/retirement/failed-candidate cleanup (or after static validation), and retains the prior known-good artifact according to policy.

Rollback uses a retained artifact without fetching or rebuilding. A known-good record means that release previously built and passed health; it does not prove that its files or ACLs are unchanged. Version 0.1 does not re-hash the retained tree against Git before reuse, and project code had Modify access while it was active. Abode to Web never runs `git pull` over your active development workspace.

## Releases

Before enabling public releases, protect `main`, configure the GitHub `release` environment with required reviewers, enable GitHub immutable releases, and create a `v*` tag ruleset that blocks tag updates and deletions while restricting tag creation to approved maintainers. The ruleset is an external prerequisite: workflow checks cannot stop an administrator or another credential from moving or deleting a tag. The release workflow requires an annotated `v*` tag whose resolved commit is reachable from `origin/main` and has a successful `CI` push run from `main` for that same commit. The final write-capable job pauses at the `release` environment; after approval it re-reads the GitHub tag object and refuses publication if the tag moved or is no longer annotated. Configure and verify all four repository controls before public publication.

Create an annotated version tag only from a clean, verified `main` commit:

```powershell
git tag -a v0.1.0 -m 'Abode to Web v0.1.0'
git push origin v0.1.0
```

The release workflow first verifies trusted-main ancestry and the required CI result, captures the exact annotated tag object, then builds `win-x64` self-contained output, packages docs/scripts/config, and computes SHA-256 checksums. After release-environment approval and the tag-object recheck, it creates a draft release, uploads the ZIP and `SHA256SUMS`, then publishes only after those uploads succeed. Pre-release version tags remain pre-releases; final version tags are marked latest only when the complete release is published. Public-repository runs also produce a signed GitHub artifact attestation. The package job's own tests validate the selected tree; they do not replace the trusted-main, CI, tag-policy, tag-stability, or environment-approval gates. Verify a downloaded release with:

```powershell
gh attestation verify .\AbodeToWeb-0.1.0-win-x64.zip --repo eversko/abode-to-web
```

Keep initial releases private until publication is explicitly approved.

## Webhooks

Manual update checks and deploys are the initial supported workflow. A future optional webhook must use a high-entropy secret stored outside the repository, verify `X-Hub-Signature-256` over the raw body, reject duplicate delivery IDs, accept only allowlisted events and the configured branch, and never use payload fields as commands. A self-hosted Actions runner is not required.
