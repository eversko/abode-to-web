# Adding a project

Abode to Web discovers projects only inside configured project roots. It never scans the whole PC. The guided flow produces a manifest change for review; framework detection is advice, not authority.

The stable path serves committed, prebuilt static output with install, build, and start unset. Any project command and every Node, command, or Docker Compose adapter require the explicit machine-wide `experimentalProjectExecution` opt-in.

## Give the repository to your LLM

Paste this exact prompt into any code-capable LLM from the repository you want to deploy:

```text
Read https://github.com/eversko/abode-to-web/blob/main/docs/AGENT-QUICKSTART.md and follow it for this repository. Start with ASSESS only: inspect read-only, report whether the project fits Abode to Web, and show the exact plan. Do not install, elevate, edit files, handle secrets, deploy, publish, or change DNS, firewall, router, or power settings. Stop and wait for my explicit APPROVE LOCAL SETUP message. After local verification, stop again and wait for my separate APPROVE PUBLISH message.
```

The [agent quickstart](AGENT-QUICKSTART.md) defines the boundary. **ASSESS** is read-only. `APPROVE LOCAL SETUP` permits only the reviewed repository-local work and non-elevated checks. `APPROVE PUBLISH` is a separate decision for the reviewed Git commit and Abode to Web operation. None of these approvals silently includes administrator elevation, secrets, DNS, Cloudflare account changes, firewall, router, or power settings.

## Guided flow

1. Open **Projects → Add project**.
2. The simple first screen offers **Copy prompt**. To use the built-in wizard instead, choose **Set up manually in dashboard**.
3. Choose **Existing local Git repository** or **Clone GitHub repository**. The clone option uses `gh repo clone`, so GitHub CLI must be installed and authenticated for the Abode to Web control-plane identity.
4. Select a path under an allowed project root, or enter an owner/repository identifier and destination beneath that root.
   When cloning, Abode to Web shows the deterministic destination immediately, tracks the clone as a background operation, and begins detection only after that operation succeeds.
5. Review detected framework, lockfile, package manager, Git remote names, current branch, committed static output, port environment variable, and possible health path. Abode to Web may display install/build/start suggestions in an explicitly experimental section, but all remain unchecked by default. Abode to Web never sends remote URLs or embedded Git credentials to the browser.
6. Confirm or edit the repository provider, remote name, deployment branch, committed static output directory, and port variable. For the stable path, leave every command suggestion unchecked; the saved manifest persists install, build, and start as unset.
7. Choose an internal port that is available when checked. Abode to Web does not reserve it through launch; immediately before a process-backed route activates, it proves that the healthy loopback listener belongs to the supervised Windows Job.
8. Leave the default visibility **disabled** while preparing the project. A hostname is optional while disabled.
9. Choose visibility:
   - **public**: reachable through the application gateway and eligible for tunnel export;
   - **protected**: routed only with a valid Cloudflare Access assertion for this application;
   - **disabled**: never published by the gateway.
10. Validate. Unknown fields, path escapes, duplicate ports/hosts, invalid commands, and unsafe roots must be fixed before saving.
11. Leave **Run a test build** off by default.
12. Choose **Save project**. Abode to Web writes the command-free manifest atomically and does not execute or publish the project.
13. If you deliberately enable **Run a test build**, the button changes to **Save and test**. With no command selected, this validates committed static output without executing project code. If any install/build/start command was selected, the operation is experimental and requires `experimentalProjectExecution: true`.
14. The next screen first reports that the operation was accepted. It reports the project as added only after the background operation reaches `succeeded`; failures remain visible in the operation panel.
15. Keep visibility disabled after local verification. Publishing is a separate approval: review the exact Git commit, hostname, visibility, activation check, and rollback availability before enabling a route or choosing **Deploy**.
16. Only a healthy candidate becomes active. A failed candidate is recorded while the previous route remains unchanged.

If any candidate step fails, open its build log. A current healthy release continues serving.

Experimental project execution can cause side effects outside the deployment directory before health succeeds, including contact with shared databases, queues, or third-party services. Route preservation does not undo those effects. Keep startup idempotent, use backward-compatible data migrations, and separate destructive maintenance from ordinary deployment.

For **protected**, create the self-hosted application and policy in Cloudflare Access, then set global `abodeToWeb.cloudflare.access.teamDomain` to `https://<your-team>.cloudflareaccess.com` (no path, query, or custom port) and set this project's `routing.accessApplicationAudience` to its 64-character hexadecimal AUD tag. Validation fails closed when either value is absent or malformed. Abode to Web uses `Cf-Access-Jwt-Assertion` and the team's public certificate endpoint; it stores no Access token.

## What detection looks for

Abode to Web may suggest:

- npm, pnpm, or Yarn from the corresponding lockfile;
- common Node scripts in `package.json`;
- static output folders such as `dist`, `build`, or framework-specific exports;
- ASP.NET project files and conventional health endpoints;
- Docker Compose only when a Compose file already exists;
- likely health paths from framework conventions; candidate health is the proof that a suggestion is correct.

A suggestion is shown with its evidence, remains unchecked by default, and is never needed for the stable static path. Selecting any command is an experimental opt-in at the project level; validation also requires the machine manifest to set `abodeToWeb.experimentalProjectExecution: true`.

Create React App projects detected through `react-scripts` suggest `build`; Vite projects suggest `dist`. ASP.NET Core projects suggest `ASPNETCORE_HTTP_PORTS`, so the candidate binds the assigned port. These remain editable suggestions: use the directory or variable your application actually supports.

## Deployment adapters

**Stable static** serves output that already exists in the exact Git commit and runs no project command. Build locally or in trusted CI, inspect the result, and commit the output directory. No Docker or Node process manager is needed on the Abode to Web machine.

All other paths are **experimental project execution**. Project commands run as the shared `AbodeToWebRuntime` identity, not as your interactive desktop user. Every command-executing project therefore belongs to one Windows trust domain and can reach resources granted to that shared identity; do not mix mutually untrusted applications. The account does not load your PowerShell profile or user-scoped version-manager environment. Install required runtimes and package-manager shims machine-wide, or otherwise make the reviewed executable safely available to that standard account.

Execution-bearing v0.1 releases are fresh-deploy only. Automatic restart and retained start, restart, rollback, or recovery are intentionally unavailable until artifacts can be sealed. Prebuilt static releases retain normal rollback.

**Node (experimental)** uses structured executable plus arguments where possible. Review the detected package-manager command and prefer its lockfile-preserving mode (`npm ci`, `pnpm install --frozen-lockfile`, or the equivalent), build if needed, and pass the assigned port through the declared port environment variable.

**Command (experimental)** starts a reviewed executable with a fixed argument list. It is suitable for .NET, Python, Go, Java, game servers, and custom binaries. Avoid `cmd /c` or `pwsh -Command` when the program can be invoked directly.

**Static with host-side install/build commands (experimental)** uses the static adapter but still executes project code and enters the shared Windows trust domain. Prefer committed prebuilt output instead.

**Docker Compose (experimental)** is optional and project-specific. A container listener is external to Abode to Web's supervised Windows Job and cannot satisfy the current endpoint-ownership proof. The candidate therefore fails closed and cleanup runs instead of route activation; Compose is not activation-ready. Docker is not an Abode to Web prerequisite for stable static hosting.

Omitting install/build from Node or command still executes the start process and remains experimental.

## Environment and secrets

Manage operator-defined runtime environment values on the project's **Environment** page. Version 0.1 has no arbitrary environment-value map in the manifest and does not load a repository `.env`; both sensitive and non-sensitive runtime values go into one protected file under `%ProgramData%\AbodeToWeb\secrets\<project>.env`. The API returns only key names and registers values for log redaction. Adding, replacing, or deleting a value is audited; existing values cannot be revealed. Abode to Web rejects invalid names and variables that can alter process loading, Git/package-manager behavior, or `ABODE_TO_WEB_` control values; choose an application-specific key instead.

Protected application secrets are injected into the start/runtime process only. Version 0.1 deliberately does not expose them to dependency-install or build commands, so a project that requires a secret during compilation is not directly supported. Do not work around this by putting a credential in the manifest or repository; prebuild the artifact through a trusted process or change the project so the secret is read at runtime.

Use **Download .env.example** on **Environment** when a repository needs a names-only template. The no-store download sorts the current names as `KEY=` with empty values, is limited to 500 keys and 64 KiB, and never includes a secret value in the response. Do not point Abode to Web at a development `.env`, copy secrets into a worktree, or commit `%ProgramData%` content.

## Repository rules

- The local path must remain under an allowed root after canonicalization.
- A junction, symlink, or `..` segment cannot escape that root.
- Deployment never runs `git pull` in the development checkout.
- Onboarding reads only safe Git remote names and the current branch for review. It does not return remote URLs to the dashboard.
- The reviewed provider, remote, exact remote URL, and branch are then fixed by the manifest, not supplied by later action requests. Every deploy compares the repository-local remote configuration with the protected `expectedRemoteUrl` before it fetches; a project without that pin cannot deploy until its reviewed manifest is updated.
- Local filesystem remotes, including UNC paths, are unsupported for deployment. Use a reviewed HTTPS or SSH remote.
- A project ID retired by project removal cannot be reused. Choose a new ID; this keeps retained deployment, log, audit, and runtime records isolated from the new project.
- Dirty development changes are not included unless committed and resolved through the configured Git reference.
- Two deployments of the same project cannot run concurrently.

## Health checks

Prefer a cheap endpoint that checks whether the application can serve requests without disclosing configuration. Configure interval, timeout, success/failure thresholds, and candidate startup deadline. An HTTP health check succeeds only on a direct `2xx` response from the assigned loopback port. Abode to Web disables proxy use and does not follow redirects: a redirect to a login page, public hostname, or HTTPS endpoint is not proof that the candidate itself is ready. Exempt the local health path from application-wide redirects when necessary. A health check should not mutate data or depend on an unrelated internet service.

For a process-backed candidate, Abode to Web captures the Windows Job process tree and TCP listeners before and after a final direct probe. The route activates only when the same loopback endpoint remains owned by that Job. Active managed routes repeat this proof during health monitoring; an ownership violation marks the route unhealthy and stops only its supervised Job.

For applications without HTTP, use the supported process or TCP health type and understand that it proves less than an end-to-end response.

A stable static deployment has no application process to probe. Abode to Web accepts it only when the committed output stays inside that deployment's retained worktree and contains a servable file; the health monitor marks the route unhealthy if that output later disappears or becomes empty.

## Add by editing YAML

Advanced users can merge an entry from `config/apps.example.yaml` into a copy of the complete manifest, then validate the entire candidate before replacing ProgramData. Keep `experimentalProjectExecution: false` and omit all project commands for the stable path:

```powershell
& "$env:ProgramFiles\AbodeToWeb\AbodeToWeb.Server.exe" validate-config `
  --path 'C:\path\to\candidate-abode-to-web.yaml'
```

Use an administrator session for the final same-directory temporary-file move into `%ProgramData%\AbodeToWeb\config\abode-to-web.yaml`; do not stream a partial edit over the live file. The watcher accepts only a fully valid snapshot and retains the last known good configuration if a replacement is invalid.

Do not edit the SQLite database to configure a project.
