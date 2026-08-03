# Security model

Abode to Web is a privileged, single-operator Windows control plane. It reduces deployment mistakes and separates public application traffic from local administration; it is not a sandbox for hostile code and is not security-certified.

Vulnerability reports belong in the repository's [private reporting flow](../SECURITY.md). This document gives operators and reviewers the product-specific security boundary.

## Maturity in the 0.1 preview

| Surface | Current status |
| --- | --- |
| Static deployments | Operationally stable and end-to-end proven on one Windows host; broader compatibility remains unproven |
| Node and direct-command deployments | Experimental; use only with code you trust |
| Docker Compose deployments | Experimental and subject to Docker's own privilege model |
| Local dashboard | Loopback-only by default |
| Remote dashboard | Unsupported and disabled by default |
| Automatic deployment | Unsupported and disabled by default |

“Exercised” describes observed operation, not a security guarantee. Hardware, Windows policy, endpoint software, filesystem ACLs, and Cloudflare configuration vary between machines.

## System boundary

Abode to Web has three important execution zones:

1. The dashboard and control API listen on loopback and authenticate the installed Windows user.
2. The public application gateway uses a separate loopback listener and routes only configured hostnames.
3. The Windows Service owns trusted state and orchestration; project commands run through a separate non-administrator runtime identity.

Configuration is a reviewed local manifest. Mutable runtime and audit state use SQLite. Per-project environment values live in protected files outside deployment worktrees. Cloudflare Tunnel is optional and makes outbound connections, so Abode to Web does not need to open an inbound router port.

Windows administrators and SYSTEM remain trusted. They can replace binaries, manifests, credentials, state, or ACLs. Abode to Web does not defend the machine from them.

## Security invariants

These properties must hold for a supported release:

- Public hostnames cannot route to the dashboard or control API.
- Browser requests select a closed, server-defined action; they do not supply an executable, shell command, working directory, Git remote, or arbitrary path.
- Project, repository, static-content, backup, and deployment paths remain inside configured roots after Windows normalization and link/reparse validation.
- A candidate release cannot become active until the expected process owns the expected loopback endpoint and passes its health gate.
- A failed candidate leaves the known-good route unchanged.
- Restart and rollback verify the retained artifact instead of trusting mutable files solely because they were healthy earlier.
- Secret values are write-only through the API, are not copied into Git worktrees, and are not expanded into persisted manifest fields or logs.
- Untrusted proxy identity headers are removed. A protected route accepts identity only after validating the Cloudflare Access application token, issuer, audience, signature, and time claims.
- Repository-controlled Git configuration, hooks, aliases, executable paths, and package scripts cannot gain control-plane rights.
- Process adoption and termination use more than a PID; ownership includes creation and executable or deployment identity.
- File walking, log capture, request bodies, operation concurrency, and retained data are bounded.

A broken invariant is potentially reportable even when a workaround exists. Tests document intended behavior but do not prove that every deployment satisfies it.

## Current controls

- Dashboard and public traffic use separate loopback listeners.
- Unknown application hostnames are rejected.
- Remote administration and automatic deployment are off by default.
- Administrative actions use typed inputs, conflict locks, timeouts, and local audit records.
- Manifest changes are schema-validated before replacing the last-known-good configuration.
- Deployments resolve an exact Git commit, create an isolated candidate, and switch routing only after a health check.
- Project process trees are managed with Windows Job Objects.
- Project commands use a non-administrator runtime account rather than the LocalService control-plane identity.
- Secret files and the runtime credential envelope rely on restrictive NTFS ACLs; the runtime credential is additionally protected with machine-bound DPAPI.
- Cloudflare Access assertions are validated for protected routes; Cloudflare account policy remains an operator responsibility.
- Logs and downloads are bounded, and known secret values are redacted where technically practical.

These controls narrow risk. They do not make an arbitrary repository safe to execute.

## Release gates and review focus

The repository-wide review identified the following gates. Each release candidate needs current evidence for them; listing a gate here does not claim that the control is absent or certified:

- sanitize proxy identity headers before forwarding public traffic;
- preserve environment placeholders and prevent expanded secret values from being written back to configuration;
- isolate trusted Git and tool execution from repository-controlled configuration and PATH substitution;
- reserve candidate ports and prove that the managed candidate owns the endpoint that passed health checks;
- verify retained release content, ownership, and topology before restart or rollback;
- strengthen reparse-point and hard-link checks at the point static content is opened;
- redact query strings and other credential-bearing URI components from logs;
- bound individual log lines and large static directory walks;
- keep dependency audits clean and exercise installer/account-name constraints in CI; and
- promote Node, direct-command, or Docker workloads only after stronger per-project isolation and read-only release materialization are proven.

Until those gates are complete, use static deployments for the public preview, run only repositories you trust, keep secrets out of the manifest, and treat rollback as availability recovery rather than a cryptographic integrity guarantee.

## Secrets and backups

Application secret values are protected primarily by NTFS ACLs. Registered values are redacted from captured output where practical, but encoded, transformed, split, unknown, or application-generated secrets may evade redaction. Applications must not print credentials.

Backups may contain application secret files and are not automatically encrypted. Store backups in encrypted, access-controlled storage. The machine-bound runtime credential is excluded because it is not portable.

## Cloudflare and remote access

Keep the dashboard on loopback. Do not expose the dashboard port through a generic tunnel rule or invent an internet password for Abode to Web.

Public applications may use Cloudflare Tunnel. A protected application also needs a correctly configured Cloudflare Access application and audience. Abode to Web cannot prove that the account-side policy matches the operator's intent, and a compromised Cloudflare account remains outside the local trust boundary.

Public applications must not make authorization decisions from `Cf-Access-*` identity headers unless the Abode to Web release in use explicitly documents verified header sanitization.

## Operating checklist

- Use the latest preview and keep Windows, Git, cloudflared, runtimes, and hosted applications patched.
- Keep the dashboard loopback-only and verify unknown hostnames are rejected.
- Review the manifest, exact commit, lockfiles, and generated plan before deploying.
- Run only trusted repositories, especially with experimental adapters.
- Keep mutable application data outside release directories.
- Protect backups separately and test restoration.
- Review dependency and secret-scanning results for every release candidate.
- Redeploy from a reviewed commit when retained-artifact integrity is uncertain.

See the [threat model](THREAT_MODEL.md) for attacker paths and residual risk.
