# Architecture

Abode to Web is one Windows Service with two deliberately separate HTTP listeners and a small set of replaceable infrastructure adapters. Desired state comes from a trusted local manifest. Observed runtime state, operations, deployment history, and audit events live in SQLite.

## System context

```text
                         fetch exact commit
GitHub / local Git repo ─────────────────────┐
                                             v
                            Abode to Web service/control plane
                                  (LocalService by default)
                                             │
                             service-owned mirror / isolated worktree
                                             │ narrow ACL grant
                                             v
                              AbodeToWebRuntime project command
                                  build + health gate
                                             │ atomic active pointer
                                             v
Browser ── 127.0.0.1:4545 ── API/UI      Abode to Web gateway :4580
                                                 ^
                                                 │ loopback HTTP
                                          Cloudflare Tunnel
                                                 ^
                                                 │ public HTTPS
                                           .com hostname
```

Cloudflare terminates public TLS. Abode to Web does not open an inbound port and does not expose the dashboard through the application gateway.

## Components

`AbodeToWeb.Server` hosts ASP.NET Core, the generated OpenAPI document, static dashboard assets, the operation event stream, and both listeners. The dashboard listener authenticates the current Windows session with Negotiate and compares its exact user SID with the installer-managed allowlist. State-changing endpoints accept project IDs plus predefined action names; there is no generic command or path endpoint.

`AbodeToWeb.Core` contains configuration and state models, validators, restart/backoff and circuit-breaker policy, health transitions, deployment transitions, and port/hostname rules. It does not know about Windows services, SQLite, Git, or HTTP clients.

`AbodeToWeb.Infrastructure` implements SQLite migrations, atomic configuration writes, Git worktrees, Windows process and Job Object integration, log rotation/redaction, health probes, Cloudflare diagnostics, filesystem boundaries, and YARP route publication.

`AbodeToWeb.Web` is a React/TypeScript client. It uses generated/shared API contracts. The UI can request only actions already defined by the trusted local manifest.

External sites are inventory records, not gateway applications. The control plane performs a bounded, no-redirect HTTPS header check and returns health plus safe metadata to the authenticated dashboard. Provider-management URLs are navigation links only; Abode to Web does not receive provider sessions or credentials and exposes no remote deployment action for these records.

The Windows Service is the trusted control plane and runs as LocalService plus `NT SERVICE\AbodeToWeb` by default. It owns Git/Git Credential Manager access, protected repository mirrors, SQLite, logs, routes, secrets, and deployment ACL changes. Project dependency installation, build, and start commands are launched through `CreateProcessWithLogonW` as the separate local `AbodeToWebRuntime` user. The service reads the machine-bound envelope named by `ABODE_TO_WEB_RUNTIME_CREDENTIAL_PATH`; the runtime user cannot read that file. That account is a standard, non-administrator user; hiding it from the sign-in screen is housekeeping, not a security boundary.

## Configuration and state

The reviewed YAML manifest is desired state: project identity, repository, deployment adapter, commands, runtime/restart policy, health policy, routing, and visibility. Environment expansion is resolved once, then canonical paths and the complete schema are validated. Unknown fields are errors. A valid in-memory snapshot remains active if a replacement is invalid.

SQLite stores observations and history: desired/current runtime state, process identity plus creation time, restart/circuit state, operation progress, deployment stages and outcomes, current health, active route/deployment pointers, retired project IDs, migrations, and audit events. Rotating log content stays in bounded files under ProgramData rather than in SQLite. SQLite is not the source of desired commands.

Writes to the manifest are serialized. Abode to Web validates the complete model, serializes and validates it again, writes a unique same-directory temporary file, flushes it to disk, and replaces the authoritative file in one same-volume operation. The last-known-good copy is refreshed only from that complete validated file. A failed or cancelled write therefore leaves either the prior complete manifest or the next complete manifest on disk, never an intentionally published partial document. Database migrations run transactionally. Backups quiesce the service by default and checksum every archived file.

## Supervision

Each running release owns a Windows Job Object. The process is created suspended, assigned before it can spawn children, and then resumed. Identity includes root PID, creation time, executable path, deployment ID, and Job Object identity; a reused PID is never enough to terminate a process.

Before `CreateProcessWithLogonW`, Abode to Web revalidates the service process's non-interactive window-station/desktop security descriptor and grants `AbodeToWebRuntime` the user-object rights Windows requires for applications that initialize `user32`. It fails closed on a null DACL, an explicit deny, an unexpected object name, or an ACL update failure. This is the service desktop—not the signed-in user's interactive desktop.

Runtime and health state together model `Stopped`, `Starting`, `Healthy`, `Unhealthy`, `Stopping`, `Backoff`, `CircuitOpen`, `Exited`, and `Unknown`. Abode to Web tries adapter-specific graceful shutdown, waits the configured grace period, then terminates the Job Object. Unexpected exits follow the manifest restart policy with capped exponential backoff. Too many restarts inside the configured window open a circuit that persists across reconciliation until an audited manual start resets it.

On Abode to Web restart, persisted PID metadata is diagnostic only: Abode to Web never reopens, adopts, or terminates a process merely from that record. Closing the former service's Job handles ends its owned trees. Startup reconciliation uses desired state plus the durable active route/deployment record to create a fresh supervised process and verify health; missing or unprovable state becomes an actionable unhealthy/recovery result rather than a PID-based kill.

## Deployment transaction

Only one mutating operation per project runs at a time. Deploy returns an operation ID immediately and advances durable stages:

```text
Queued → Fetching → Resolving → PreparingWorktree → Installing → Building
       → StartingCandidate → CheckingHealth → SwitchingRoute
       → DrainingPrevious → Complete
                              └──────────────→ Failed (old route retained)
```

The control plane fetches through a service-owned bare mirror using the service identity and Git Credential Manager, then resolves the configured remote branch to an exact SHA. It materializes a uniquely named worktree under `%ProgramData%\AbodeToWeb\deployments\<project>\<short-sha>-<deployment-id>`, while SQLite retains the full SHA. Before project install/build/start commands, Abode to Web grants `AbodeToWebRuntime` Modify on only that deployment's configured working tree. A dynamic release keeps that grant while Abode to Web may run or restart it; normal stop, retirement, or failed-candidate cleanup revokes it. A static release is revoked after output validation. The candidate receives a reserved port, starts under supervision, and must meet its health threshold. Only then does one atomic active-deployment update republish gateway routes. The former release drains before stopping.

Rollback points the route at a retained known-good artifact and health-checks it; it does not rebuild. “Known-good” records a prior successful build and health result, not immutable files or a current integrity proof. Version 0.1 neither re-hashes retained content against Git nor reconstructs it under a different owner before reuse. Retention never removes the active release.

Operation progress is durable for diagnosis and audit, but queued work is not blindly replayed after a process restart. At database startup, operations left `queued` or `running` are marked failed with `operation.interrupted` and receive an interruption audit event. Deployment records receive a separate cutover reconciliation before new administrative work is accepted: a nonactive candidate is failed while the prior route stays in place; an active interrupted release is restarted and re-proven healthy; if that proof fails, Abode to Web attempts the newest retained known-good rollback and otherwise marks the route unhealthy. The owner can then inspect the recorded outcome and deliberately retry.

## Gateway

The dashboard/API listener is `127.0.0.1:4545`. The application gateway is `127.0.0.1:4580`. YARP routes by normalized hostname to the active release and reloads from an atomic route snapshot without restarting Abode to Web. It supports WebSockets and streaming, applies forwarded headers, body limits, and timeouts, rejects unknown hosts, and never discloses local paths or ports.

Known stopped projects receive a branded offline response when that project enables it. Known unhealthy projects receive a temporary-unavailable response. `/__abode-to-web/gateway/health` reports only gateway availability. Static deployments are served directly from a validated output directory.

## Data locations

```text
C:\Program Files\AbodeToWeb\              immutable application files
C:\ProgramData\AbodeToWeb\config\       desired manifest
C:\ProgramData\AbodeToWeb\state\        SQLite database and migrations
C:\ProgramData\AbodeToWeb\logs\         rotated system/app/build logs
C:\ProgramData\AbodeToWeb\deployments\  isolated retained releases
C:\ProgramData\AbodeToWeb\secrets\      protected per-project environment files
C:\ProgramData\AbodeToWeb\secrets\runtime-credential.json
                                       machine-bound runtime credential envelope
C:\ProgramData\AbodeToWeb\state\runtime-account.json
                                       installer ownership marker (no password)
C:\ProgramData\AbodeToWeb\backups\      local backup archives
C:\ProgramData\AbodeToWeb\cloudflared\  generated tunnel config and copied credential
```

Project development repositories stay outside both installation and deployment directories. `AbodeToWebRuntime` receives traverse-only access to the ProgramData root and deployments container; it has no broad grant to development repositories, logs, configuration, state, secrets, backups, Cloudflare files, or retained deployment trees. The service SID has the deployment-directory rights needed to add and remove each narrow working-tree grant.

## Trust boundaries

The browser is untrusted input, even on localhost. The manifest is trusted only after schema, command, hostname, path, and environment validation. Hosted applications do not run as the control-plane service, but they currently share one `AbodeToWebRuntime` Windows SID rather than receiving one identity per project. Narrow, temporary ACLs reduce its filesystem reach; they are not a complete sandbox. Cloudflare is trusted only to deliver application traffic. Protected application routes require a validated `Cf-Access-Jwt-Assertion` for the manifest's exact team issuer and per-project audience; Abode to Web fetches public signing keys and stores no Access token. Administrative dashboard exposure remains disabled unless a separate Cloudflare Access trust boundary is explicitly enabled.

See [Security](SECURITY.md) and [Threat model](THREAT_MODEL.md).
