# Troubleshooting

Start with the read-only diagnostics:

```powershell
pwsh -File scripts/status-abode-to-web.ps1
pwsh -File scripts/repair-abode-to-web.ps1
pwsh -File scripts/status-tunnel.ps1
```

Do not delete SQLite files, deployment directories, Job Objects, or services as a first response. Capture the operation ID and bounded relevant logs before changing state.

## Dashboard does not open

If the service is stopped, open administrator PowerShell 7 and run `scripts/start-abode-to-web.ps1`. If it starts and exits, inspect `%ProgramData%\AbodeToWeb\logs` and Windows Event Viewer → **Windows Logs → Application**.

If port 4545 is owned by another process, diagnostics show its name and PID. Reconfigure one application; Abode to Web does not kill an unrelated listener. Confirm the manifest still binds the dashboard to `127.0.0.1`.

If the API works but the frontend is blank, request `/health` and `/openapi/v1.json`, check the browser console, and reinstall from a verified release. Hosted apps remain supervised even when static dashboard assets fail.

## Dashboard sign-in is denied

Abode to Web uses Windows integrated authentication. A **401** means the browser did not complete Negotiate sign-in; retry in the normal Windows browser session and review enterprise integrated-authentication policy. A **403** means Windows signed in successfully, but the authenticated user's exact SID is not in `ABODE_TO_WEB_ADMIN_SIDS`. Account names and membership in the local Administrators group are not substitutes for the recorded user SID. The 403 response body names the Windows account and SID the session actually authenticated as — read it first, because the denied identity is often not the account you meant to use.

**If the 403 reports `ANONYMOUS LOGON` (code `dashboard_identity_anonymous`):** the Windows account signed in with a blank password. Windows does not perform real network authentication for blank-password accounts, so the session is downgraded to an anonymous logon whose SID matches no operator — even when your own SID is correctly listed in `ABODE_TO_WEB_ADMIN_SIDS`. Set a password on the Windows account (Settings → Accounts → Sign-in options; `netplwiz` can keep automatic desktop sign-in), close every browser window to drop the cached anonymous session, then sign in again with the account name and its password. No Abode to Web repair is needed for this case.

If the denied account is a real user that should administer Abode to Web, first run this exact command in an ordinary, unelevated PowerShell 7 window as the Windows user who should administer Abode to Web, and copy the SID it returns:

```powershell
Import-Module .\scripts\AbodeToWeb.Common.psm1 -Force
Get-AbodeToWebCurrentUserSid
```

Then open an administrator PowerShell 7 session, inspect, and explicitly repair using the copied value:

```powershell
pwsh -File scripts/repair-abode-to-web.ps1
pwsh -File scripts/repair-abode-to-web.ps1 -Apply `
  -DashboardAdminSid 'S-1-5-21-EXAMPLE-EXAMPLE-EXAMPLE-1001'
pwsh -File scripts/restart-abode-to-web.ps1
```

Replace the example with the copied SID. This matters when UAC elevation uses a different administrator account: deriving the SID after alternate elevation authorizes that administrator, not the intended desktop user. To retain multiple administrators, pass their user SIDs as an array. Do not use an account name or group SID; the tooling accepts user SIDs only. A repair without `-DashboardAdminSid` preserves a valid existing list; it falls back to the current elevated user only when the setting is missing or invalid. The status and tunnel scripts send the current Windows credentials only to Abode to Web's loopback API and never to the public gateway.

## Manifest is invalid

Abode to Web keeps the last-known-good configuration. Open **System** and scroll to **Abode to Web system logs** for the field/path/line error, or validate a reviewed copy directly with `& "$env:ProgramFiles\AbodeToWeb\AbodeToWeb.Server.exe" validate-config --path C:\path\to\abode-to-web.yaml --json`. Unknown properties, duplicate IDs/hosts/ports, commands outside the adapter schema, and paths outside allowed roots are intentionally rejected.

Fix a copy, validate it, and replace atomically through Abode to Web. `repair-abode-to-web.ps1 -Apply` will not overwrite or invent a manifest.

## Project will not start

Check, in order:

1. active deployment directory exists;
2. project-specific secret keys exist (values are never displayed);
3. runtime executable is installed machine-wide and usable by `AbodeToWebRuntime`;
4. working directory and structured arguments match the manifest;
5. internal port is free;
6. runtime log shows a startup error;
7. health path/timeout reflects actual startup behavior.

An access error during **Fetch/resolve** usually means the service SID/account lacks access to the configured repository root. Rerun the installer with the explicit `-AllowedProjectsRoot`, or use a reviewed dedicated service account. An access error during **Install**, **Build**, or **Start** points instead to the narrow `AbodeToWebRuntime` grant on that deployment working tree. Run repair as described below; do not grant either identity access to the whole user profile or system drive.

## Runtime identity is unhealthy

`status-abode-to-web.ps1` and read-only `repair-abode-to-web.ps1` report a **limited** runtime-identity result in an unelevated session because the ownership marker and DPAPI envelope are intentionally unreadable there. This is not a failure. Rerun either command from an elevated PowerShell 7 session for the full check that:

- the `AbodeToWebRuntime` account exists, is enabled, and is not an administrator;
- `%ProgramData%\AbodeToWeb\state\runtime-account.json` names the same account SID;
- the DPAPI LocalMachine credential envelope decrypts and has the expected owner/DACL;
- the account has only the intended traverse access at the ProgramData/deployments containers, not broad access to logs, configuration, state, secrets, backups, Cloudflare material, or development roots.

Do not delete/recreate the account or edit the JSON files by hand. In an administrator PowerShell 7 session, repair the managed identity and ACLs:

```powershell
pwsh -File scripts/repair-abode-to-web.ps1 -Apply
```

If the credential file may have been copied or exposed, rotate the random password and DPAPI envelope:

```powershell
pwsh -File scripts/repair-abode-to-web.ps1 -Apply -RotateRuntimeCredential
```

Neither command prints the password. If the account name is already used but the ownership marker is missing, its SID differs, or its protected ACL/parent trust chain is unsafe, Abode to Web refuses to take over or delete that account; review the collision or ACL change manually instead of forcing ownership.

## Secret file permissions are invalid

Abode to Web refuses to read a project environment file whose owner or exact ACL no longer matches its protected service-SID policy. While any registered secret file cannot be trusted, captured log messages are replaced with a suppression notice because safe redaction cannot be proved.

Back up the machine, then run `pwsh -File scripts/repair-abode-to-web.ps1 -Apply` from an administrator PowerShell 7 session. Repair validates the protected secrets directory and every direct entry before changing any child, restores the exact ACL on regular `<app-id>.env` files without reading or changing their contents, and leaves `runtime-credential.json` under its separate policy. An unexpected file type, directory, reparse point, or hard link stops repair for manual review. After a successful repair, restart Abode to Web or allow up to 15 seconds for the redaction cache to reload; never solve the error by granting LocalService, Users, or Everyone access.

## Port collision

Abode to Web reserves and rechecks ports but another process can race. The error names the port owner when Windows permits. Stop/reconfigure that program or assign a different internal port. Do not force-stop an unknown PID; PID reuse is not proof of ownership.

## Crash loop or circuit open

Read the last runtime exit, health failures, and restart timeline. Fix the application or environment, then choose **Start**; an explicit, audited manual start resets the in-memory restart circuit. Do not repeatedly reset a project that still crashes. Automatic reconciliation preserves an open-circuit state across an Abode to Web or Windows restart instead of silently creating another crash loop.

## Deployment failed

Open the deployment operation and identify the failed stage:

- **Fetch/resolve**: remote, branch, network, or Git credential problem.
- **Prepare worktree**: disk, retention, or Git metadata problem.
- **Install/build**: project command or dependency problem; inspect build logs.
- **Candidate start**: executable, environment, port, or ACL problem.
- **Health**: incorrect endpoint, deadline, dependency, or application regression.

A failure before route switch leaves the old release online. If it did not, treat that as an Abode to Web defect and preserve logs/state for a security-quality bug report.

An HTTP health endpoint must return a direct `2xx` response on the reserved loopback port. Abode to Web deliberately does not follow `3xx` redirects and does not use the machine's configured HTTP proxy. If the log reports a redirect, exempt the health path from login, canonical-host, or HTTP-to-HTTPS redirects; do not weaken the health gate to accept the redirected page.

## Operation was interrupted by a service restart

On startup, Abode to Web marks every persisted operation left `queued` or `running` as failed with `operation.interrupted` and writes an audit event. It does not guess whether you still want a start, stop, deployment, rollback, or bulk action.

Deployment recovery is more specific. A candidate that never became active is failed and the previous route remains. A candidate already recorded as active is restarted and health-checked; if it cannot be proven healthy, Abode to Web tries the most recent retained known-good deployment and otherwise leaves the route unavailable. Check **Activity**, deployment history, current route health, and build/runtime logs. Retry only after that recovery result is clear.

## Project removal was interrupted

Removal permanently retires the project ID and clears mutable route/runtime state before deleting its exact secret file and atomically removing the manifest entry. If the operation or service stops between those durable steps, startup may report that the manifest still contains a retired project ID. This is an intentional fail-closed state.

Do not restore the secret file or reuse the ID. If the dashboard service is still running and the app entry remains, retry the same confirmed **Remove project** action; retirement and missing-secret deletion are idempotent, so the retry can complete the manifest removal safely. If Abode to Web can no longer start, back up the current ProgramData state, make a reviewed copy of the manifest with only that retired app entry removed, validate the complete copy with the installed `validate-config` command, then replace the ProgramData manifest using the same-directory atomic procedure in [Adding a project](ADDING_A_PROJECT.md#add-by-editing-yaml). The source repository and deployment/audit history are not deleted by project removal.

## Private Git repository cannot authenticate

Authentication belongs to the trusted Windows service account, not `AbodeToWebRuntime`. Your interactive `gh auth status` may succeed while LocalService cannot authenticate. Run the Abode to Web service under a dedicated standard Windows account, authenticate `gh`/GCM in that account's profile, and verify `git ls-remote`. The service fetches through its protected mirror; project install/build/start commands do not receive GitHub credentials or development-repository access. Never work around this by putting a token in the manifest or URL.

GitHub or internet downtime affects fetch/check/deploy, not an active local release.

## Public domain is offline

Separate the path:

1. Project health in Abode to Web.
2. Gateway routing with an explicit Host header:

   ```powershell
   Invoke-WebRequest http://127.0.0.1:4580/ -Headers @{ Host = 'sample-static.example.com' }
   ```

3. `scripts/status-tunnel.ps1`.
4. `cloudflared` service log.
5. Cloudflare DNS route and tunnel connector state.
6. Test from another network.

A known stopped project should show the offline page when `routing.offlinePage` is enabled; with that option disabled, an empty 503 is expected. A known unhealthy project should show temporary unavailable. An unknown hostname must return 404.

## Tunnel will not start

Run the installed version's validation command:

```powershell
cloudflared tunnel --config "$env:ProgramData\AbodeToWeb\cloudflared\config.yml" ingress validate
```

Confirm the credential path exists and its ACL grants read access to `NT SERVICE\cloudflared`. Do not print the JSON file. Regenerate with `configure-tunnel.ps1`; it writes only after validation. Newer cloudflared versions also run connectivity prechecks and report blocked outbound DNS/network paths.

## After sleep or reboot

Both services should start automatically. Check their statuses and recent audit/system events. Abode to Web never adopts or kills a process from a persisted PID: closing the service's Job handles ends its owned trees, then startup reconciliation creates fresh processes only for desired-running projects and verifies health. A manually started project retains desired-running state and can restart even when `autostart` is false; a project explicitly stopped with `autostart` false remains stopped. Crash circuits remain meaningful across restart.

If the PC sleeps, public projects are unreachable until wake and connector recovery. Abode to Web cannot prevent power loss, internet loss, or Windows maintenance.

## Low disk or log quota

Stop new deployments and take a backup first. Abode to Web automatically removes inactive releases beyond `retainedDeploymentsPerApp` after a successful deployment, and it prunes rotated logs against the configured quota and age every five minutes. There is no manual cleanup button in the first release. If space remains low, free capacity outside Abode to Web or carefully lower the retention values in the trusted local manifest, validate it, and deploy normally; never delete the active deployment or random ProgramData folders while Abode to Web runs.

## Process refuses to stop

Wait through the configured grace period. If Abode to Web owns and verifies the Job Object, confirmed **Force stop** terminates the complete tree and audits the action. If ownership is uncertain after service recovery, Abode to Web reports it rather than killing a possibly unrelated process.

## Collect a useful report

Include Abode to Web version, Windows version, operation ID, project adapter (not secret values), expected/actual state, bounded system/build/runtime log segments, and reproduction steps. Remove usernames, local paths if sensitive, domain/account identifiers, and all credentials before sharing.
