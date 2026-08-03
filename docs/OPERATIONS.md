# Operations

Most days require only the local dashboard. The Windows Service keeps applications supervised when every browser is closed.

## Open Abode to Web

Choose **Abode to Web** from the Start menu, or run:

```powershell
pwsh -File scripts/open-abode-to-web.ps1
```

The private dashboard is `http://127.0.0.1:4545`. Do not substitute the PC's LAN address. The browser uses your current Windows sign-in automatically; Abode to Web accepts only SIDs registered by the installer and does not have a separate local password.

## Start a project

1. Open **Projects** and select the project.
2. Review its deployment, port, health, and Git state.
3. Choose **Start**.
4. Follow the operation progress until health is **Healthy**.

Start uses the active retained deployment. If no successful deployment exists, deploy first.

## Deploy an update

1. Open the project and choose **Check for updates**.
2. Confirm branch, remote commit, message, and author.
3. Choose **Deploy**.
4. Watch the operation stages or leave the page; the service continues.
5. Open build logs if installation/build fails. The old healthy release stays routed.
6. After candidate health succeeds, Abode to Web switches the route and drains the old release.

Do not edit or delete a deployment directory while an operation is active.

## See logs

Open the project, choose **Logs**, then select **Runtime** or **Build**. Use severity filtering and search; pause automatic scroll before reading older lines. Downloads are bounded segments, not the entire log store.

Open **System** and scroll to **Abode to Web system logs** for service behavior. Administrative actions are under **Activity**. Tunnel diagnostics are in the **Cloudflare Tunnel** panel on **System**. Secret values are redacted where registered, but application output should never deliberately print credentials.

## Stop a project

Choose **Stop**. Abode to Web sends the configured graceful shutdown action, waits the grace period, and terminates the owned process Job Object only if needed. A force stop is a confirmed secondary action and is recorded in the audit log.

Stopping a project marked **Protect from bulk stop** requires an individual action; Quiet Mode skips it. This local runtime flag is separate from `visibility: protected`, which means Cloudflare Access is required for public requests.

## Restart a project

Choose **Restart**. This restarts the active deployment and does not fetch or rebuild. Use **Deploy** when code changed.

## Roll back

1. Open **Deployments** for the project.
2. Select a retained deployment marked known-good.
3. Review commit, build, health, and timestamp.
4. Choose **Rollback** beside that deployment and confirm.
5. Watch candidate start/health. The route changes only after success.

Rollback does not rebuild. It is unavailable after the artifact has been removed by retention. “Known-good” means the retained release previously built and passed health; it is not a cryptographic integrity guarantee. In 0.1 a dynamic release keeps `AbodeToWebRuntime` Modify access to its working tree while Abode to Web may run or restart it, and build-created files remain owned by that shared identity. Abode to Web does not re-hash the retained tree or reconstruct it under a trusted owner before rollback. Run only trusted code, keep uploads/databases outside release files, and do not rewrite files that a later rollback may reuse.

## Open a public project

Choose **Open** on the project card. This opens the configured HTTPS hostname, not a local port. If local health is good but the public URL fails, check the tunnel and Cloudflare DNS.

## Demo and Quiet modes

- **Stop All Demos / Quiet Mode** performs one audited bulk stop of every enabled project not marked **Protect from bulk stop**.
- Quiet Mode is a status after that bulk action, not a policy lock: you can manually start a project immediately. **Start Showcase** clears the indicator and starts the configured group. An Abode to Web service restart can also clear the in-memory indicator, but the projects' persisted desired-stopped state remains.
- **Start Showcase** starts only projects in the configured `showcase` demo group, with startup staggering to avoid CPU/disk spikes.

Bulk actions return an operation ID and show progress. If one project action fails, the bulk operation stops and is recorded as failed; inspect which projects already changed state before retrying.

## Remove a project

Project removal is a confirmed danger-zone action. It stops the project, removes its desired configuration and gateway route, deletes its Abode to Web-managed per-project secret file, and permanently retires that project ID so it cannot be reused. The source repository and deployment/audit history remain. Remove or archive the repository with your normal Git/file workflow only after taking any needed backup.

Retirement is written before the manifest entry is removed. If power or a service failure interrupts that narrow sequence, Abode to Web refuses to start with the retired ID still present rather than risk attaching a future project to old state. Follow [Project removal was interrupted](TROUBLESHOOTING.md#project-removal-was-interrupted); never recreate the deleted secret file or reuse the ID as a shortcut.

## Service controls

Status and dashboard launch are read-only and do not require elevation:

```powershell
pwsh -File scripts/status-abode-to-web.ps1
pwsh -File scripts/repair-abode-to-web.ps1
```

Windows service control requires an administrator PowerShell 7 session:

```powershell
pwsh -File scripts/start-abode-to-web.ps1
pwsh -File scripts/stop-abode-to-web.ps1
pwsh -File scripts/restart-abode-to-web.ps1
pwsh -File scripts/repair-abode-to-web.ps1 -Apply
```

Stopping the Abode to Web Windows Service intentionally closes its Job Objects and stops their owned application process trees. Starting Abode to Web again reconciles desired state and restarts projects whose policy calls for it. Closing only the browser has no runtime effect.

An operation that was queued or running when the service stopped is recorded as failed with `operation.interrupted`; it is not automatically submitted again. Before the API accepts new work, Abode to Web reconciles any interrupted deployment cutover: it keeps a nonactive candidate away from traffic, re-proves an already-active candidate, or falls back to a retained known-good release when possible. Check **Activity**, deployment state, route health, and logs before deliberately retrying the action.

The service remains the trusted control plane, normally as LocalService. Project install, build, and start commands run as the non-administrator `AbodeToWebRuntime` account. `status-abode-to-web.ps1` and read-only repair diagnostics report the account/ownership marker, DPAPI credential envelope, and narrow ACL state without revealing the password. An ordinary session returns an explicit limited result for those protected files; run the diagnostic elevated when a full runtime-identity/ACL check is needed.

Repair restores known directory ACLs, each regular `<app-id>.env` secret file's exact service ACL, service recovery, environment pointers, the managed runtime account, the dashboard SID allowlist, and the shortcut. Secret repair never reads or prints values; an unexpected file, directory, reparse point, or hard link directly under the secrets directory stops the repair for manual review. The machine-bound `runtime-credential.json` envelope remains under its separate read-only service ACL policy. Repair preserves a valid existing SID list unless `-DashboardAdminSid` is explicit; if the setting is absent or invalid, repair authorizes the current elevated Windows user. It will not overwrite an invalid/missing manifest or invent missing binaries.

Rotate the generated `AbodeToWebRuntime` credential if the protected envelope may have been copied or exposed:

```powershell
pwsh -File scripts/repair-abode-to-web.ps1 -Apply -RotateRuntimeCredential
```

The new credential is stored as a machine-bound DPAPI envelope; neither the old nor new password is displayed. A normal repair keeps the existing credential. If UAC was elevated with a different administrator account, obtain the intended dashboard user's SID in that user's unelevated PowerShell first and pass it with `-DashboardAdminSid`; only user SIDs are accepted. See [Installation](INSTALLATION.md#dashboard-windows-identity).

## Check the tunnel

```powershell
pwsh -File scripts/status-tunnel.ps1
```

If configuration and gateway are healthy but the connector is stopped, elevate and run `scripts/restart-tunnel.ps1`. If Cloudflare itself or the home internet is unavailable, Abode to Web keeps applications running locally and reports the external failure without redeploying them.

## Add a new hostname

Change and validate the project domain in Abode to Web first. Then regenerate ingress, explicitly approve DNS creation, and restart the separate tunnel service. Follow [Domains](DOMAINS.md).

## Back up and restore

```powershell
# Quiesced, checksummed backup with configuration, SQLite state, and secrets.
pwsh -File scripts/backup-abode-to-web.ps1

# Administrator PowerShell 7; validates checksums and creates a safety backup.
pwsh -File scripts/restore-abode-to-web.ps1 -BackupPath 'D:\Backups\abode-to-web-20260714-120000.zip'
```

See [Backup and restore](BACKUP_AND_RESTORE.md) before disaster recovery.

## After sleep, reboot, or Windows Update

Abode to Web and `cloudflared` start automatically. Abode to Web does not trust or adopt stale PIDs; it creates fresh processes for desired-running retained deployments and admits them only after health verification. Run both status scripts, then check recent activity. Projects configured for autostart are staggered.

Sleep makes every home-hosted application unavailable. A primary portfolio on Cloudflare Pages remains online.

## Capacity routine

Check free disk space monthly and before large deployments. Abode to Web applies the manifest's release-retention limit after successful deployments and prunes rotated logs automatically; it never removes the active deployment. A full disk can prevent SQLite commits, builds, and log rotation. The first release has no dashboard cleanup control: back up, then free space outside Abode to Web or carefully lower the trusted manifest's retention values—never delete random ProgramData folders while Abode to Web runs.

The dashboard reports host-wide CPU and memory use. Configurable per-application CPU/memory warning thresholds are not implemented in this first release, and Abode to Web does not enforce affinity, priority, or resource limits. Use application-specific tooling when one demo needs closer resource diagnosis.
