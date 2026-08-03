# Backup and restore

An Abode to Web backup protects desired configuration, SQLite runtime/deployment history, and application secret files. Development repositories remain separate and should be backed up through Git or your normal workstation backup. The dedicated Windows runtime account and its machine-bound credential are deliberately not portable backup data.

Backups are ZIP archives with `backup.json` and SHA-256 for every payload file. ZIP is not encrypted and does not preserve NTFS ACLs. Treat it like a password vault because it normally contains application secrets.

## Create a normal backup

Open an administrator PowerShell 7 session:

```powershell
pwsh -File scripts/backup-abode-to-web.ps1
```

The script briefly stops Abode to Web for a consistent SQLite/file snapshot, restarts it, and writes a timestamped archive under `%ProgramData%\AbodeToWeb\backups`.

Write directly to another drive:

```powershell
pwsh -File scripts/backup-abode-to-web.ps1 -Destination 'D:\AbodeToWebBackups'
```

The default includes `config`, `state`, and application files under `secrets`. It always excludes `%ProgramData%\AbodeToWeb\secrets\runtime-credential.json` and `%ProgramData%\AbodeToWeb\state\runtime-account.json`; those two files belong to the current Windows installation. Optional data can be large:

```powershell
pwsh -File scripts/backup-abode-to-web.ps1 `
  -Destination 'D:\AbodeToWebBackups' `
  -IncludeDeployments `
  -IncludeLogs
```

Use `-IncludeTunnelCredentials` only when the backup location has equivalent protection. That option briefly quiesces the independent tunnel service and includes only its generated `config.yml` and protected `credentials` directory—not its live log. Cloudflare account recovery should not depend solely on this file.

Recursive backup/removal refuses a category containing a junction or symbolic link, rather than risking traversal outside ProgramData. If this affects an optional retained deployment, review the link and either omit deployments or remove the retained artifact through Abode to Web before retrying.

## Why the runtime credential is excluded

`runtime-credential.json` contains the randomly generated `AbodeToWebRuntime` password protected with DPAPI LocalMachine. It can be decrypted only in the context of the Windows installation that created it and is protected by a restrictive NTFS ACL. `runtime-account.json` is the local ownership marker that ties that account name to its SID. Copying either file to another PC would not safely recreate the account or its security boundary, so the backup script excludes both even when application secrets are included.

On the same PC, restore preserves the current runtime credential and marker rather than replacing them from an archive. On a clean or reinstalled PC, install Abode to Web first; the installer creates a new `AbodeToWebRuntime` account, password, DPAPI envelope, and marker. Then restore the portable Abode to Web data. Do not copy the old runtime files by hand. A restore archive that contains either machine-bound file is rejected.

## Secret-excluded or online backup

`-ExcludeSecrets` omits application secret files, but a normal ProgramData backup still needs elevation because configuration and SQLite state use restrictive ACLs. `-NoServiceStop` avoids service-control operations, copies the SQLite database and WAL while live, and prints a warning. Use online backups only when a short service interruption is impossible; test restoration because file copies are less reliable than a quiesced snapshot.

## Verify and store

After backup:

```powershell
$archive = Get-ChildItem "$env:ProgramData\AbodeToWeb\backups\abode-to-web-*.zip" |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1
Get-FileHash $archive.FullName -Algorithm SHA256
Test-Path $archive.FullName
```

Copy at least one recent archive off the PC. Use an encrypted drive or encrypted backup system. Do not commit archives, upload them to issue trackers, or store them in an unprotected cloud-synced folder.

Test restore on a disposable Windows installation or VM periodically. Restore deliberately accepts only the standard `%ProgramData%\AbodeToWeb` location and an owned installed Abode to Web service, so an arbitrary alternate data root cannot become a recursive-write target. A backup that has never been restored is an assumption.

## Restore

1. Confirm the release installed on the machine supports the backup schema.
2. Put the archive on a local trusted drive.
3. Open administrator PowerShell 7.
4. Run:

   ```powershell
   pwsh -File scripts/restore-abode-to-web.ps1 `
     -BackupPath 'D:\AbodeToWebBackups\abode-to-web-20260714-120000.zip'
   ```

5. Read the confirmation carefully.
6. Run `scripts/status-abode-to-web.ps1`, open the dashboard, check project state, and verify secrets by starting one noncritical project.
7. Check the tunnel separately.

Any operation captured as `queued` or `running` in the restored SQLite snapshot is marked failed as `operation.interrupted` when Abode to Web initializes; Abode to Web does not replay old administrative intent. Interrupted deployment records are reconciled against the restored active route and retained artifacts before new work is accepted. Review **Activity** and deployment health after restore, then retry only the action you still want.

Restore rejects rooted paths, `..` entries, oversized expansion, unsupported data categories, unchecksummed/missing files, checksum mismatches, and archives containing the machine-bound runtime files before touching ProgramData. It requires a healthy locally installed `AbodeToWebRuntime` identity, makes a pre-restore safety backup unless `-SkipSafetyBackup` is explicit, stops every non-stopped Abode to Web service state, and also quiesces an owned tunnel service when tunnel material is present. It swaps only categories in the archive, preserves the local runtime credential/marker, rebuilds service and runtime ACLs from local identities, and restarts services that were active. If the copy fails, it attempts to put the pre-operation directories back.

Before changing ProgramData, restore runs the installed release's full manifest validator against the candidate configuration. Every configured project root must already exist and pass the narrow-root/reparse-point checks. Restore then reconciles the Abode to Web service-SID grants from those validated roots, which makes recovery onto a clean machine deterministic without trusting ACL data from the ZIP.

`-Force` skips the interactive confirmation but does not skip archive or checksum validation. Use it only in a reviewed recovery procedure.

## What is not restored

- Program Files binaries; reinstall the desired Abode to Web release first.
- Hosted development repositories; recover them through Git/backups.
- Application-owned databases, uploads, and other mutable data outside the selected Abode to Web data categories; back these up with the application's own documented procedure.
- GitHub/Git Credential Manager authentication, which belongs to the service account profile.
- The `AbodeToWebRuntime` Windows account, its DPAPI credential envelope, and its ownership marker; installation creates these locally and restore preserves the current machine's copies.
- The dashboard administrator SID allowlist, which belongs to Windows service configuration; the prerequisite installation records the restoring Windows user unless an existing valid list is preserved.
- Cloudflare account tunnel/DNS/Access resources.
- Tunnel credentials unless the backup was created with `-IncludeTunnelCredentials`.
- Firewall, router, power-plan, or other Windows account settings.

If a retained deployment directory was not included, its history may remain in SQLite but the artifact cannot be rolled back until rebuilt or restored.

## Suggested schedule

- Before every Abode to Web upgrade or manifest migration.
- After onboarding or deleting a project.
- After changing secrets or domains.
- Weekly for an actively used host, with at least one off-PC copy.
- Before disk maintenance or Windows repair.
