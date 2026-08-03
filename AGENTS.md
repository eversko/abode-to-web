# Abode to Web contributor guide

This file applies to the entire repository. It is intentionally tool-agnostic so local developers and code-capable agents follow the same rules.

## Product boundary

Abode to Web is a Windows-first, health-gated, reversible Git release controller for one trusted machine. The stable path serves committed, prebuilt static output with install, build, and start commands unset. Any project command and every Node, command, or Docker Compose adapter require the explicit `experimentalProjectExecution` opt-in and must not be described as production-proven.

All experimental project processes use the shared `AbodeToWebRuntime` identity and therefore occupy one Windows trust domain. Do not imply isolation between mutually untrusted command-executing projects. In v0.1, execution-bearing releases are fresh-deploy only: automatic restart and retained start, restart, rollback, and recovery remain unavailable until sealed artifacts exist. Prebuilt static keeps normal rollback.

The dashboard is a loopback control surface. Do not add public control-plane access, arbitrary command execution, browser terminals, multi-user tenancy, or automatic DNS, firewall, router, power, secret, or administrator changes without an explicit security and product decision.

## Safe working rules

- Inspect the current branch, remotes, working tree, and nearby instructions before editing.
- Preserve unrelated changes. Do not reset, clean, force-push, or rewrite history unless the user explicitly requests it.
- Keep credentials, `.env` files, machine state, real user paths, hostnames, SIDs, and private inventory out of source, tests, fixtures, screenshots, and logs.
- Treat repository commands, manifests, Git configuration, retained artifacts, proxy headers, and runtime files as untrusted inputs at their boundaries.
- Keep the dashboard and application gateway separate. Maintain loopback defaults and fail closed when identity or routing evidence is missing.
- Never claim that a build, deployment, health check, rollback, or security fix passed unless the corresponding check actually ran.
- Use the approval contract in [docs/AGENT-QUICKSTART.md](docs/AGENT-QUICKSTART.md) for user onboarding. A request to assess is not permission to install or publish.
- Keep stable onboarding command-free. Detection may explain install/build/start possibilities, but saved defaults must leave all three unset and point to output already committed to Git.

## Build and test

From the repository root:

```powershell
dotnet restore AbodeToWeb.sln -p:NuGetAudit=true -p:NuGetAuditMode=all
dotnet format AbodeToWeb.sln --verify-no-changes --no-restore --verbosity minimal
dotnet build AbodeToWeb.sln --configuration Release --no-restore
dotnet test AbodeToWeb.sln --configuration Release --no-build
```

For the dashboard:

```powershell
Set-Location src/AbodeToWeb.Web
npm ci
npm run lint
npm test -- --run
npm run build
```

Run the narrowest relevant test while iterating, then the full affected suite before handoff. Building from a deeply nested Windows path can exceed legacy path limits; use a short worktree path rather than changing project names or excluding files.

## Change expectations

- Keep commits small, reviewable, and accompanied by verification evidence.
- Update tests and public documentation when behavior or safety boundaries change.
- Use generic examples such as `C:\Projects\sample-app` and `app.example.com`.
- Do not commit generated dependency folders, local manifests, build outputs unless intentionally versioned, release archives, logs, databases, or temporary diagnostics.
- Public claims must distinguish stable execution-free static releases from every form of experimental project execution.
