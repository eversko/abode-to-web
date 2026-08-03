# Threat model

This model covers one operator running Abode to Web on one Windows PC, the applications deployed through it, their Git remotes, and an optional Cloudflare Tunnel. It is a living engineering document, not a penetration-test report or security certification.

The 0.1 static path is a public preview validated end to end on one Windows host; broader compatibility remains unproven. Node, direct-command, and Docker Compose adapters are experimental and require trusted project code until their isolation gates are complete.

## Security goals

1. Internet traffic for an application cannot reach the dashboard or control API.
2. Browser and discovery input cannot become an arbitrary command, path, Git target, or environment filename.
3. A failed or unrelated process cannot satisfy a candidate health gate or replace the active route.
4. Abode to Web does not adopt, restart, or terminate a process based on a reused PID alone.
5. Project secret values are not returned by the API, committed, or deliberately persisted in logs or configuration.
6. Invalid configuration does not replace the last-known-good snapshot.
7. A hosted project does not receive the LocalService control-plane identity by default.
8. Restart and rollback do not trust a mutable retained artifact without integrity and topology checks.
9. Public identity headers are not trusted unless Abode to Web has validated and reconstructed them.
10. Security-relevant work is bounded so one project cannot trivially exhaust control-plane memory or storage.

## Assets

- control-plane service identity and binaries;
- dashboard authorization state and anti-forgery boundary;
- trusted manifest and last-known-good configuration;
- application secrets and runtime-account credential material;
- Git credentials, mirrors, exact commit selection, and release artifacts;
- active route and known-good deployment metadata;
- process ownership, health, restart, and rollback state;
- logs, audit records, backups, and Cloudflare tunnel material; and
- availability and integrity of other applications on the same Windows host.

## Actors and trust assumptions

| Actor | Trust assumption |
| --- | --- |
| Windows administrator or SYSTEM | Trusted; already able to replace Abode to Web and its protected state |
| Authorized dashboard user | Trusted to approve reviewed operations, but their browser can encounter malicious pages |
| Public internet user | Untrusted |
| Hosted application and its dependencies | Potentially vulnerable; dynamic adapters currently require operator trust |
| Git repository content and configuration | Untrusted until the operator reviews the exact commit |
| Cloudflare account and edge | Trusted for routing only to the extent configured by the operator |
| Other local standard users or malware | Untrusted, but Windows integrated authentication is not a defense against compromise of the authorized user |

The operator is expected to keep Windows patched, review commits and manifest commands, protect the Git and Cloudflare accounts with strong authentication, and maintain separate backups.

## Trust boundaries

```text
Internet
  │
  ▼
Cloudflare Tunnel ──► public loopback gateway ──► active application

Authorized browser ─► private loopback dashboard/API ─► LocalService control plane
                                                        │
                                                        ├─► protected config/state/secrets
                                                        └─► non-admin project runtime
                                                               │
                                                               └─► candidate worktree/process
```

The dashboard and public gateway are separate listeners. Crossing from browser input to a control-plane action, from Git content to process execution, from a project process to another project, and from mutable candidate files to an active route are the most important boundaries.

## Threats, controls, and promotion evidence

### Public gateway reaches administration

An attacker tries hostname confusion, forwarded-host spoofing, or a tunnel rule that points at the dashboard. The design separates listeners, rejects unknown hostnames, and does not serve control routes from the application gateway.

Residual risk: the owner can create an unsafe tunnel or reverse-proxy rule. Remote dashboard operation remains unsupported.

### Browser input becomes code or filesystem access

A malicious page, local process, or crafted onboarding result tries to submit a command, executable, remote, branch, working directory, or path. The API exposes named actions for a known project and resolves trusted definitions from the local manifest. Same-origin, authentication, anti-forgery, input-size, and rate controls protect mutations.

Promotion evidence required: every adapter, helper, and discovery path preserves this closed action model, and environment expansion cannot persist secret values into executable manifest fields.

### Repository content escapes its runtime boundary

Repository-controlled Git configuration, hooks, package scripts, executable lookup, junctions, hard links, or crafted files try to execute with broader rights or reach another project. Project commands use a separate non-administrator identity and generated deployment paths.

Promotion evidence required: trusted tool resolution is pinned; Git is isolated from repository-controlled configuration; filesystem topology is validated at use; and dynamic adapters receive per-project identities plus trusted-owner, read-only release materialization.

### Candidate health is spoofed

Another local process races for the candidate port or answers the health endpoint. Before a process-backed route activates, Abode to Web captures the Windows Job process tree and TCP listeners, runs a final direct loopback probe, then requires the same Job-owned loopback endpoint to remain present. Failure leaves the active route unchanged and cleans up the candidate.

Active managed routes repeat that ownership proof during health monitoring. An ownership violation marks the route unhealthy and stops only its supervised Job, never the foreign listener owner. An attested application can still return a healthy response while behaving maliciously later.

### Retained release is modified

A project changes its worktree after activation, then a restart or rollback executes those changed files. Exact Git selection protects initial intent but does not make a mutable worktree immutable.

Promotion evidence required: retained content, owner, ACLs, links, and topology are verified, or the release is rematerialized from the reviewed commit before restart and rollback. Until that evidence exists for a release, rollback is an availability feature, not a supply-chain integrity guarantee.

### Process confusion or escape

PID reuse, orphan children, breakaway processes, or an external daemon can cause Abode to Web to manage the wrong process. Windows Job Objects, recorded process identity, and pre-route plus ongoing endpoint attestation reduce that risk, and uncertain processes are not adopted after service restart.

Residual risk: Docker Compose exposes listeners through processes outside Abode to Web's Windows Job, so it cannot satisfy the current ownership proof. Compose candidates fail closed and clean up instead of activating a route. Docker, WSL, privileged children, and other external daemons remain outside Abode to Web's containment boundary.

### Secret disclosure

Secrets may escape through an API response, manifest expansion, command line, URI query string, application output, exception, deployment copy, process memory, or backup. Values are stored outside worktrees, the UI is write-only for values, and known values are redacted where practical.

Promotion evidence required: expanded values never become persisted manifest data, and credential-bearing URI components are removed from all logs. Redaction cannot reliably cover transformed or unknown secrets. Backups containing application secrets need separate encryption.

### Cloudflare identity spoofing

An internet client sends `Cf-Access-*` headers directly or a public application mistakes forwarded metadata for authenticated identity. Protected routes validate the Access application token.

Promotion evidence required: all client-supplied identity headers are stripped before forwarding, and identity metadata is introduced only from a validated assertion. The Cloudflare account-side policy and account security remain operator responsibilities.

### Resource exhaustion

A project floods logs, emits one extremely long line, creates a very large static tree, crashes repeatedly, or fills disk. Abode to Web uses timeouts, operation locks, restart backoff, retention, log rotation, and quotas.

Promotion evidence required: individual log lines and static traversal have explicit bounds. Finite home hardware, network, and power remain availability risks.

### Supply-chain or release compromise

A malicious dependency, compromised maintainer account, mutable action, or forged release can affect every installation. Lockfiles, dependency audits, secret scans, review, pinned CI actions, exact commits, and release checksums reduce risk.

Residual risk: a checksum published beside a compromised archive is not a signature. Authenticode signing and reproducible provenance are future hardening work.

## Out of scope and accepted limitations

- Abode to Web does not isolate code from an already-compromised Windows administrator or SYSTEM.
- It does not make a vulnerable hosted application safe.
- It is not a multi-tenant platform or adversarial-code sandbox.
- Local audit data is useful for accountability but is not tamper-evident against administrators.
- Application database migrations and external side effects cannot be automatically reversed by route rollback.
- Home hosting remains vulnerable to sleep, reboot, power loss, disk failure, ISP outage, Windows Update, and tunnel outage.
- Cloudflare terminates public TLS and can observe edge traffic.

## Verification expectations

Security-sensitive changes should include tests for the intended failure mode and a reviewer should still inspect reachability and platform behavior. Release candidates should pass build, test, dependency, secret, PowerShell, installer, and documentation checks. A passing suite is evidence, not proof of absence of vulnerabilities.

Review this model whenever Abode to Web adds an adapter, remote administration, automation trigger, credential backend, new privileged helper, or different process identity.
