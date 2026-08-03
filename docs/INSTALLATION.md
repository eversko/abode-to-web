# Installation

Abode to Web supports Windows 10/11 and current Windows Server on x64. A release installation is self-contained: the PC does not need a global .NET runtime, SDK, Node.js, IIS, Docker, WSL, or a Linux VM to run Abode to Web.

Stable hosting serves committed, prebuilt static output with no project install, build, or start command. Any project command and every Node, command, or Docker Compose adapter require the explicit `experimentalProjectExecution` opt-in.

## LLM-assisted start

From the repository you want to deploy, give any code-capable LLM this exact prompt:

```text
Read https://github.com/eversko/abode-to-web/blob/main/docs/AGENT-QUICKSTART.md and follow it for this repository. Start with ASSESS only: inspect read-only, report whether the project fits Abode to Web, and show the exact plan. Do not install, elevate, edit files, handle secrets, deploy, publish, or change DNS, firewall, router, or power settings. Stop and wait for my explicit APPROVE LOCAL SETUP message. After local verification, stop again and wait for my separate APPROVE PUBLISH message.
```

The [agent quickstart](AGENT-QUICKSTART.md) separates read-only assessment, repository-local setup, and publishing. An LLM must stop before administrator elevation. Review and run the machine-wide installation command yourself in an administrator PowerShell 7 session; installation never implies permission to change secrets, DNS, firewall, router, or power settings.

## Before installing

You need:

- PowerShell 7 (`pwsh`), not Windows PowerShell 5.1.
- Git for deployments.
- A reviewed Abode to Web manifest.
- Administrator elevation only for Program Files, ProgramData ACLs, service registration/recovery, and the Start-menu shortcut.

Building Abode to Web from source additionally needs .NET 10 SDK and Node.js 22.12 or newer. A stable static project needs only its committed output at release time. Experimental command-executing projects need their own machine-wide runtimes.

Do not install the repository itself under `%ProgramData%\AbodeToWeb\deployments`. Keep Abode to Web source and hosted development repositories separate.

## Install a release ZIP

1. Download both the versioned `AbodeToWeb-<version>-win-x64.zip` and `SHA256SUMS` from the same release.
2. Verify the archive before extraction:

   ```powershell
   $archive = Get-Item .\AbodeToWeb-*-win-x64.zip
   $expectedLine = Get-Content .\SHA256SUMS |
     Where-Object { $_ -match [regex]::Escape($archive.Name) } |
     Select-Object -First 1
   if (-not $expectedLine) { throw "No checksum entry for $($archive.Name)." }
   $expected = ($expectedLine -split '\s+')[0]
   $actual = (Get-FileHash $archive.FullName -Algorithm SHA256).Hash
   if (-not $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
     throw 'Release checksum mismatch. Do not install this archive.'
   }
   ```

3. Extract the ZIP to a temporary folder.
4. Review `config/abode-to-web.example.yaml`. Copy it to `config/abode-to-web.yaml`, set `projectsRoots` to a short list of narrow parent directories, and leave `experimentalProjectExecution: false` for stable execution-free static hosting.
5. Open PowerShell 7 as administrator in the extracted folder:

   ```powershell
   pwsh -File scripts/install-abode-to-web.ps1 `
     -AllowedProjectsRoot 'C:\Projects'
   ```

The installer detects the included `AbodeToWeb.Server.exe`; it does not download a runtime or rebuild.

## Install from source

As a normal user, build the self-contained release first:

```powershell
Copy-Item config/abode-to-web.example.yaml config/abode-to-web.yaml
# Edit and review config/abode-to-web.yaml.
pwsh -File scripts/install-abode-to-web.ps1 -PublishOnly
```

Then elevate only for installation:

```powershell
pwsh -File scripts/install-abode-to-web.ps1 `
  -PublishedRoot '.\artifacts\publish\win-x64' `
  -AllowedProjectsRoot 'C:\Projects'
```

The script runs the frontend build, publishes `win-x64` with `--self-contained true`, stages files, stops an existing service for an idempotent update, preserves an existing ProgramData manifest unless `-ReplaceConfiguration` is explicit, restores service recovery, and starts Abode to Web.

## Service and project runtime identities

The safe control-plane default is `NT AUTHORITY\LocalService` combined with the dedicated `NT SERVICE\AbodeToWeb` service SID. The service owns the API, SQLite, Git operations, protected repository mirrors, logs, routing, secrets, and deployment ACL changes. An update migrates a legacy Abode to Web service still running as LocalSystem to LocalService unless you explicitly select another service identity.

The installer also creates a hidden local user named `AbodeToWebRuntime`. It is enabled, belongs only to the normal Users group, and is not an administrator. Stable mode does not run project code. If `experimentalProjectExecution` is explicitly enabled, Abode to Web uses `CreateProcessWithLogonW` to run dependency installation, build, and application start commands as this account instead of LocalService. “Hidden” only keeps a service-only account out of ordinary sign-in lists; it is not a security boundary and does not prevent an administrator from finding or using the account.

The runtime identity has traverse-only access to `%ProgramData%\AbodeToWeb` and its deployments container. Before launching an experimental install/build/start command, the service grants it Modify on that deployment's configured working tree. These directory grants are narrow, but the Windows identity is shared: every concurrently command-executing project can use every grant available to `AbodeToWebRuntime`. They therefore occupy one Windows trust domain and must fully trust one another. The identity receives no broad access to development repositories, logs, configuration, SQLite state, secrets, backups, or Cloudflare material. The service SID has the deployment-directory ACL rights needed to manage grants.

This temporary grant is narrower than running project code as the control-plane service, but it is not an immutable-release boundary. Build-created files are owned by `AbodeToWebRuntime`, and an active application can change release files or permissions. Version 0.1 does not seal execution-bearing artifacts, so those releases are fresh-deploy only: automatic restart and retained start, restart, rollback, or recovery are intentionally unavailable. Prebuilt static releases retain normal rollback. Run only mutually trusted experimental applications, keep uploads/databases outside release files, and read the residual-risk discussion in [Security](SECURITY.md#current-controls).

The runtime password is generated from 48 cryptographically random bytes and is never displayed. Its only stored form is a DPAPI LocalMachine envelope at `%ProgramData%\AbodeToWeb\secrets\runtime-credential.json`, which the service locates through `ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH`. The file is readable by SYSTEM, Administrators, and `NT SERVICE\AbodeToWeb`, but not by `AbodeToWebRuntime`. DPAPI LocalMachine makes the envelope specific to this Windows installation; the file ACL remains essential because an administrator or SYSTEM can decrypt it. `%ProgramData%\AbodeToWeb\state\runtime-account.json` records the account name and SID so repair and uninstall can prove that Abode to Web created the account. It contains no password.

The marker is not trusted from JSON alone. Abode to Web requires its owner and protected DACL to be exactly the managed SYSTEM/Administrators boundary, validates the protected Abode to Web and state-directory ACLs above it, and rejects an unsafe parent `DeleteChild` grant. Installation, repair, restore, and uninstall refuse account ownership operations when that chain cannot be proved.

To rotate the generated credential during an update, add `-RotateRuntimeCredential` to the normal installer command. To rotate it without reinstalling binaries, use an administrator PowerShell 7 session:

```powershell
pwsh -File scripts/repair-abode-to-web.ps1 -Apply -RotateRuntimeCredential
```

A normal elevated repair also restores the protected secrets-directory boundary and the exact service-SID ACL on each regular `<app-id>.env` file without reading or rewriting its contents. It stops for manual review if an unexpected file, directory, reparse point, or hard link appears directly under the secrets directory. The machine-bound runtime credential remains under its separate ACL policy.

The nonsecret validator returns the normalized `projectsRoots`, and the installer reconciles only service-SID grants on those roots: it removes grants for roots no longer in the manifest and uses `/grant:r` for current roots. `AbodeToWebRuntime` is not granted access to the development roots. If supplied, `-AllowedProjectsRoot` is an explicit confirmation and must exactly match the validated list. Uninstall removes managed grants without deleting repositories.

### Private Git repositories

Git fetch/resolve operations stay in the trusted service and use GitHub CLI or Git Credential Manager for the service identity. `AbodeToWebRuntime` does not receive those credentials or access to the active development repository. LocalService does not inherit your interactive GitHub profile. Public repositories and local-only deployments work normally. For private repositories, create a dedicated standard Windows user for the Abode to Web service, authenticate Git/GitHub while signed in as that account, and pass its credential at install time:

```powershell
$credential = Get-Credential '.\AbodeToWebRunner'
pwsh -File scripts/install-abode-to-web.ps1 `
  -ServiceCredential $credential `
  -AllowedProjectsRoot 'D:\AbodeToWebProjects'
```

Do not use your primary administrator account and do not grant “Log on as a service” more broadly than needed. Windows normally grants the right when the service credential is registered; domain policy may override it. Abode to Web never stores this service password in its manifest. This optional service identity is separate from the automatically managed `AbodeToWebRuntime` project-command identity.

## Dashboard Windows identity

The dashboard uses Windows integrated authentication (Negotiate), not an Abode to Web password. On the first install, the installer records the exact SID of the Windows user running the elevated installation as `ABODE_TO_WEB_ADMIN_SIDS` in the service environment. A normal update preserves the existing SID list. This dashboard identity is independent of both the control-plane service identity and `AbodeToWebRuntime`.

Edge, Chrome, and other Windows-integrated browsers normally use the signed-in Windows session automatically for the loopback dashboard. A different Windows user receives **403 Access denied**, even if that user can reach port 4545. Private-browsing or enterprise browser policy can require an authentication prompt.

If UAC elevation uses alternate credentials, the elevated installer sees the administrator account rather than the unelevated desktop user. Without an explicit SID, first install would therefore authorize that alternate administrator. Before elevating, run this exact command in an ordinary PowerShell 7 window as the person who should use the dashboard, and copy its output:

```powershell
Import-Module .\scripts\AbodeToWeb.Common.psm1 -Force
Get-AbodeToWebCurrentUserSid
```

Then pass that value to the elevated installer. Use one or more user SIDs only; account names and group SIDs are rejected:

```powershell
pwsh -File scripts/install-abode-to-web.ps1 `
  -PublishedRoot '.\artifacts\publish\win-x64' `
  -DashboardAdminSid 'S-1-5-21-EXAMPLE-EXAMPLE-EXAMPLE-1001' `
  -AllowedProjectsRoot 'C:\Projects'
```

Replace the example with the copied SID; do not type the word `EXAMPLE`. See [Troubleshooting](TROUBLESHOOTING.md#dashboard-sign-in-is-denied) for recovery.

## Installed layout

Immutable binaries go to `%ProgramFiles%\AbodeToWeb`. Mutable configuration, SQLite state, logs, deployments, secrets, backups, and tunnel material go to `%ProgramData%\AbodeToWeb`. Development repositories remain where you put them.

The service starts automatically with a short boot delay and has three progressively delayed recovery restarts. A Start-menu Internet shortcut opens `http://127.0.0.1:4545`.

## Verify

```powershell
pwsh -File scripts/status-abode-to-web.ps1
pwsh -File scripts/open-abode-to-web.ps1
```

For a service restart, elevate and run:

```powershell
pwsh -File scripts/restart-abode-to-web.ps1
```

Confirm that the dashboard is reachable only at loopback and the gateway health endpoint answers at `http://127.0.0.1:4580/__abode-to-web/gateway/health`.

## Availability recommendations

Run `scripts/repair-abode-to-web.ps1` without `-Apply` for read-only diagnostics. It reports port owners, low disk space, and an AC sleep timeout. Abode to Web does not change sleep, firewall, router, DNS, or power settings. If continuous hosting matters, decide manually whether the PC should remain awake on AC power.

## Update

Back up first. Extract a newer release over a new temporary directory and rerun the installer. It stops the service only during the binary swap and preserves ProgramData. Never overwrite the live Program Files directory by hand.

## Uninstall

```powershell
# Preserves all mutable data and project repositories.
pwsh -File scripts/uninstall-abode-to-web.ps1

# Destructive; take and verify a backup first.
pwsh -File scripts/uninstall-abode-to-web.ps1 -RemoveData -Force
```

Cloudflare is an independent service and is not removed by the Abode to Web uninstaller. Use `scripts/uninstall-cloudflared.ps1` separately. No uninstaller deletes hosted repositories, Cloudflare account resources, DNS records, firewall rules, router settings, or power settings.

The Abode to Web uninstaller removes the `AbodeToWebRuntime` local account only when `runtime-account.json` exists and its recorded SID matches the account. If ownership cannot be proved, it leaves the account in place and reports the manual review required. The runtime password is never printed during removal.
