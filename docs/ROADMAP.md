# Roadmap

Abode to Web is a focused, Windows-first personal hosting control plane. The roadmap favors understandable failure behavior and reversible deployment over feature breadth.

This is directional, not a delivery promise. Priorities can change as real installations and security reviews produce better evidence.

## Current baseline: 0.1 preview

- A Windows Service keeps the control plane alive independently of the browser.
- A loopback dashboard is separated from the public hostname gateway.
- A reviewed YAML manifest and SQLite state drive operations.
- Exact-commit deployments use candidate worktrees, health checks, and route cutover.
- Static deployments are a public preview validated end to end on one Windows host; broader compatibility remains unproven.
- Process-backed route activation proves a stable loopback listener belongs to the supervised Windows Job immediately before cutover; active managed routes repeat the proof during health monitoring.
- Process supervision, bounded logs, restart backoff, local audit, backup tooling, and Cloudflare Tunnel guidance are implemented.
- Node, direct-command, and Docker Compose adapters are present but experimental.

The current evidence supports a public static-hosting preview. It does not support claims of multi-tenant isolation, hostile-code sandboxing, production certification, or broad Windows compatibility.

## Public-preview release criteria

Every public release candidate should meet these gates:

- clean builds, formatting, backend and frontend tests, PowerShell parsing, and documentation links;
- clean high-severity dependency audit and full-history secret scan;
- a clean-machine install, repair, upgrade, backup, restore, and uninstall exercise;
- installer and service-account checks that cover Windows naming and ACL constraints;
- no private paths, credentials, project inventory, or operator-specific data in source or history;
- public proxy identity headers sanitized before an application sees them;
- environment placeholders preserved without persisting expanded secret values;
- trusted Git and tool execution isolated from repository-controlled configuration and PATH substitution;
- process-backed routing fails closed unless the supervised Windows Job owns the stable loopback endpoint before activation and during ongoing checks;
- retained executable start, restart, rollback, and recovery remain disabled until content, ownership, and filesystem topology can be verified;
- bounded static traversal and individual log-line memory use; and
- known high-impact findings fixed or explicitly documented with a narrow compensating control.

These criteria summarize the repository-wide readiness and security review. Passing them supports release confidence; it is not a security certification.

## Promote static deployments

Static deployment is the first supported workload. Promotion beyond preview requires:

- successful use on more than one clean Windows machine;
- repeatable failed-candidate, restart, rollback, low-disk, reboot, and interrupted-cutover tests;
- reparse-safe file serving at the final file-open boundary;
- clear migration and recovery documentation; and
- a signed or otherwise verifiable Windows release path beyond a checksum published beside the archive.

## Promote Node and direct-command deployments

Node and direct-command deployments now verify a stable Job-owned loopback endpoint immediately before route activation and during ongoing health checks. An ownership failure leaves the old route in place or quarantines the active route and stops only the matching supervised Job.

Dynamic workloads remain experimental. General support still requires:

- a separate runtime identity or equivalently strong boundary per project;
- disposable build staging and sealed, trusted-owner release materialization;
- read-and-execute release trees with a separate writable data location;
- integrity and topology verification before retained start, restart, rollback, or recovery;
- validation of the current Job containment, endpoint attestation, and cleanup behavior on more Windows hosts; and
- clear handling of application data migrations and irreversible external side effects.

Only then should documentation present dynamic workloads as generally supported.

## Docker Compose

Docker Compose remains optional, experimental, and not activation-ready. Container listeners are owned outside Abode to Web's supervised Windows Job, so the current endpoint attestation cannot prove them. A Compose candidate must fail closed and clean up instead of activating a route. Promotion requires an equivalent ownership proof, a documented daemon-access boundary, lifecycle ownership, health behavior, cleanup, and an explicit statement of what remains Docker's responsibility.

## Later improvements

- Authenticode signing and verifiable build provenance.
- More clean-machine, sleep/wake, power-loss, low-disk, and Windows Update recovery coverage.
- DPAPI or Windows Credential Manager backing for application secrets with safe export and recovery semantics.
- Optional notifications for circuit-open, failed deployment, disk pressure, and tunnel disconnect without hosted telemetry.
- Better release-retention previews and resource accounting.
- Accessibility review with keyboard and screen-reader users.
- Carefully scoped, opt-in update checks only after manual deployment is proven.

Remote administration, webhooks, and automatic deployment require separate threat models and will not be enabled by default.

## Non-goals

Abode to Web is not pursuing Kubernetes, clustering, multi-node scheduling, billing, customer accounts, organizations, a marketplace, managed databases, email hosting, a browser terminal, an arbitrary command API, a code editor, or a general AI assistant.

It also does not manage router configuration, Windows Update policy, power plans, domain ownership, or Cloudflare account policy.

## Decision test

A feature belongs when it helps one person safely deploy and operate a small number of applications on one Windows machine, keeps recovery understandable, and does not widen the public control-plane surface without a compelling security model.
