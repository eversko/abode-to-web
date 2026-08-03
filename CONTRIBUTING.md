# Contributing to Abode to Web

Abode to Web welcomes focused fixes, tests, documentation, and small features that make Windows self-hosting safer or easier to understand.

The project is an early preview maintained by one person. Static deployment is the primary proven workload; Node, direct-command, and Docker Compose paths are experimental. Please discuss large, security-sensitive, or architectural changes before investing substantial work.

## Good first contributions

- Reproduce and narrow a Windows-specific bug.
- Add a regression test for an existing behavior.
- Improve installation, recovery, or troubleshooting guidance.
- Remove an unsafe default or make a failure easier to understand.
- Improve keyboard, screen-reader, or plain-language usability.

For usage questions and bug-report expectations, see [SUPPORT.md](SUPPORT.md). Suspected vulnerabilities must use the private process in [SECURITY.md](SECURITY.md), never a public issue.

## Before opening a pull request

1. Search existing issues and pull requests.
2. Open an issue first for a new feature, schema change, privilege-boundary change, adapter, automation trigger, or public-network surface.
3. Keep the change narrow and explain the user-visible outcome.
4. Add tests for new behavior and regression coverage for fixes.
5. Update the security model, threat model, operations guide, or roadmap when the boundary changes.

AI-assisted contributions are welcome. The contributor remains responsible for understanding the change, reviewing generated code, removing private data, and providing verification evidence.

## Development setup

Prerequisites:

- Windows 10/11 or Windows Server;
- PowerShell 7;
- .NET 10 SDK;
- Node.js 22.12 or later;
- npm and Git.

Run the main checks from the repository root:

```powershell
dotnet restore AbodeToWeb.sln -p:NuGetAudit=true -p:NuGetAuditMode=all
dotnet format AbodeToWeb.sln --verify-no-changes --no-restore --verbosity minimal
dotnet build AbodeToWeb.sln --configuration Release --no-restore
dotnet test AbodeToWeb.sln --configuration Release --no-build

Push-Location src/AbodeToWeb.Web
npm ci
npm audit --audit-level=high
npm run lint
npm test -- --run
npm run build
Pop-Location
```

Some installer and ACL checks require an isolated Windows test machine and elevation. Do not weaken or skip a safety check merely to make a test pass on a development machine.

Never use real credentials, tunnel tokens, production environment files, private repository URLs, personal paths, or live project inventory in tests and examples.

## Pull request checklist

- The change has one clear purpose.
- User-visible and security-relevant behavior is described.
- New and changed behavior is tested.
- Formatting, builds, tests, and dependency audits pass.
- Documentation and example configuration stay accurate.
- Logs, fixtures, screenshots, and commit history contain no secrets or private data.
- The change preserves the separation between browser input, trusted manifest data, and project execution.
- Risky migration and rollback behavior is explained.

Maintainers may request a smaller change, additional evidence, or a design discussion. They may squash commits when merging.

## License and conduct

Abode to Web is licensed under the [Apache License 2.0](LICENSE). Contributions are accepted under the same license and include its patent terms. Third-party code and assets must have a compatible, documented license.

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md) and the maintainer-led process in [GOVERNANCE.md](GOVERNANCE.md).
