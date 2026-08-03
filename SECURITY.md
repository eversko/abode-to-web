# Security policy

Abode to Web is a privileged local control plane: it can start processes, read repositories, store application secrets, and change public routes. Please report suspected vulnerabilities privately.

## Supported versions

Abode to Web is currently a `0.x` preview and has no stable release line.

| Version | Security updates |
| --- | --- |
| Default branch | Best effort |
| Latest published preview | Best effort |
| Older previews and forks | Not supported |

Security fixes may require upgrading to the newest preview. Maintainers may decline to backport a fix when doing so would increase risk or delay the current release.

## Report a vulnerability

Use GitHub's private vulnerability reporting flow: **Security → Report a vulnerability**, or open a [private vulnerability report](https://github.com/eversko/abode-to-web/security/advisories/new).

Do not include vulnerability details in a public issue, discussion, pull request, or commit. If the private reporting button is unavailable, open a public issue containing only a request to enable private reporting; do not describe the vulnerability there.

A useful report includes:

- the affected commit or preview version;
- prerequisites and a minimal reproduction;
- realistic impact and who can reach the vulnerable path;
- sanitized logs or screenshots, if needed; and
- a suggested mitigation, if you have one.

Remove credentials, tokens, personal paths, private repository names, and unrelated user data from all evidence.

## What to expect

Abode to Web is maintained by one person. Reports are handled on a best-effort basis, not under a service-level agreement. The maintainer aims to acknowledge a report within seven calendar days and provide an initial assessment within fourteen days. Complex fixes, Windows-specific testing, or maintainer availability may extend those targets.

The maintainer will coordinate a fix and disclosure plan with the reporter when practical. Please allow a reasonable remediation window before publishing details, especially when a report could expose deployed applications or local secrets.

## Scope and severity context

High-value reports include realistic ways to:

- reach the dashboard or control API from an untrusted network path;
- turn browser, manifest, Git, or project input into unintended command execution;
- escape configured project or deployment roots;
- read or persist secret values outside their intended store;
- impersonate an authenticated Cloudflare Access identity;
- replace a healthy route without passing the deployment gate;
- cross a project or runtime-identity boundary; or
- stop, adopt, restart, or roll back the wrong process or release.

The operational boundary and known limitations are documented in [docs/SECURITY.md](docs/SECURITY.md) and [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).

The following are normally out of scope unless they reveal an Abode to Web defect:

- vulnerabilities in an application merely hosted by Abode to Web;
- compromise that already requires Windows administrator or SYSTEM access;
- denial of service that requires intentionally exhausting the reporter's own test machine;
- unsupported remote-dashboard exposure or bypassing documented safe defaults; and
- automated scanner output without a reachable, product-specific impact.

## Safe research

Good-faith research is welcome when it uses systems you own or have permission to test, avoids privacy violations and persistence, does not destroy data or disrupt third parties, and stops after demonstrating the minimum necessary impact. This statement does not authorize testing against systems you do not own or override applicable law.
