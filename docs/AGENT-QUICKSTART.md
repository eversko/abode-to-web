# Give this to your LLM

This page is a provider-neutral instruction contract for using a code-capable LLM to prepare one trusted repository for Abode to Web. It does not grant permission by itself.

Paste this prompt into your LLM from the repository you want to deploy:

```text
Read https://github.com/eversko/abode-to-web/blob/main/docs/AGENT-QUICKSTART.md and follow it for this repository. Start with ASSESS only: inspect read-only, report whether the project fits Abode to Web, and show the exact plan. Do not install, elevate, edit files, handle secrets, deploy, publish, or change DNS, firewall, router, or power settings. Stop and wait for my explicit APPROVE LOCAL SETUP message. After local verification, stop again and wait for my separate APPROVE PUBLISH message.
```

## Approval contract

Treat these as three separate phases. Approval applies only to the current repository and the exact plan most recently shown.

| Phase | What is allowed | Required stop |
|---|---|---|
| **ASSESS** | Read files and Git metadata; identify the framework, build output, health needs, and risks | Show the evidence and exact plan. Make no changes. |
| **APPROVE LOCAL SETUP** | Make the reviewed repository-local changes and run the reviewed non-elevated checks | Show the diff and verification. Do not deploy or publish. |
| **APPROVE PUBLISH** | Deploy the reviewed commit through Abode to Web exactly as described | Report the commit, health result, routing state, and whether rollback is available. |

None of these phrases authorizes unattended elevation, entering or revealing secrets, changing DNS, Cloudflare account resources, firewall, router, or power settings, or expanding access beyond the current repository. If one of those actions is needed, explain why and ask for a separate, specific approval. Let the human enter credentials directly into the owning product; never request that they paste a secret into chat.

Abode to Web's stable path is deliberately execution-free: the exact Git commit already contains the static output, and the manifest leaves install, build, and start unset. Any project command or non-static adapter requires the machine-wide `experimentalProjectExecution: true` opt-in. Every experimental project process uses the shared `AbodeToWebRuntime` identity, so all command-executing projects occupy one Windows trust domain and must mutually trust one another.

## Phase 1 — ASSESS

Stay read-only. Do not install dependencies, run a build that writes output, edit files, create commits, clone elsewhere, start services, or change machine state.

Inspect only what is needed to answer:

1. Is this a Git repository, and is the intended release committed?
2. Is it a static application with a deterministic output directory already committed to Git? Prefer this path.
3. Which exact local or CI command produces the static output, and can the result be verified and committed before Abode to Web sees it?
4. Would Abode to Web need to run any install, build, or start command, or use Node, command, or Docker Compose? If so, label the entire path **experimental project execution** and identify its machine-wide runtime and health endpoint.
5. What port variable and cheap, non-mutating health path would a dynamic candidate use?
6. Could build or startup contact a production database, queue, or third-party service?
7. Are secrets, `.env` files, private paths, or credentials at risk of entering Git, logs, build output, or the manifest?

Return this compact report:

```text
Fit: STATIC STABLE | PROJECT EXECUTION EXPERIMENTAL | NOT A FIT
Evidence: <framework, local build command, committed output, Git state>
Missing: <prerequisites or none>
Risk: <important side effects and trust assumptions>
Local setup plan: <ordered repository-local edits and checks>
Publish plan: <exact commit source, visibility disabled by default, health gate, rollback>
Separate approvals: <any admin, secret, DNS, firewall, router, power, or external-account work>
```

Then stop. Continue only after the user sends `APPROVE LOCAL SETUP` for that plan.

## Phase 2 — APPROVE LOCAL SETUP

After approval, carry out only the repository-local plan that was shown. Good stable static preparation usually means:

- confirm a lockfile-preserving install command;
- confirm the local production build command and deterministic output directory;
- build and inspect the static output locally or in trusted CI;
- include the reviewed output in the exact Git commit that Abode to Web will resolve;
- add a names-only `.env.example` if runtime values are needed;
- document the proposed Abode to Web settings with install/build/start unset, **visibility disabled**, and **Run a test build off**;
- keep real credentials and machine-specific paths out of source.

For experimental project execution, verify that the reviewed executable is directly invokable, binds the reserved loopback port through the declared environment variable, shuts down cleanly, and exposes a cheap health check. Do not use a shell wrapper when a structured executable and argument list works. State clearly that every command-executing project shares one Windows trust domain under `AbodeToWebRuntime`; do not mix mutually untrusted applications.

Execution-bearing v0.1 releases are fresh-deploy only. Automatic restart and retained start, restart, rollback, or recovery are intentionally unavailable until sealed artifacts exist. Prebuilt static releases retain normal rollback.

Run only the non-elevated checks from the approved plan. Do not edit the live manifest under `%ProgramData%`, install a Windows Service, approve UAC, commit, push, deploy, enable routing, or create external resources unless those actions received their own explicit approval.

Finish with:

- files changed;
- checks run and their exact result;
- proposed commit and Abode to Web settings;
- anything the human must review;
- a reminder that publishing has not happened.

Then stop. Continue only after the user sends `APPROVE PUBLISH` for the reviewed result.

## Phase 3 — APPROVE PUBLISH

Before acting, restate the exact commit or pending commit, adapter, committed output or experimental commands, health check, hostname, visibility, and rollback availability. If the repository changed after local verification, return to ASSESS.

`APPROVE PUBLISH` authorizes only the reviewed Git and Abode to Web operations in the plan. It does not authorize DNS, Cloudflare, firewall, router, power, secret, or administrator changes. Keep visibility disabled unless the user explicitly approved a hostname and routing state.

When authorized:

1. Ensure only reviewed files enter the commit; run a secret and privacy check first.
2. Resolve the configured Git reference to the exact commit.
3. Create an isolated candidate. Never build over the development checkout.
4. For stable static, verify the committed output is inside the retained release, contains a servable file, and has no install/build/start command.
5. For experimental project execution, confirm the machine-wide opt-in and shared trust-domain acceptance, then create a fresh candidate and wait for its configured health threshold. Do not offer retained start/restart/rollback/recovery in v0.1.
6. Activate routing only after the candidate passes. On failure, preserve the current release and report the safe failure.
7. Report the deployed commit, health evidence, effective visibility, and known-good rollback target—or state clearly that rollback is unavailable for an experimental execution-bearing release.

Publishing an application route does not create public DNS. If a hostname needs Cloudflare Tunnel or DNS work, leave the application disabled or locally routed and hand the user the separate documented plan.

## If Abode to Web is not installed

Do not install it during ASSESS. Link the user to [Installation](INSTALLATION.md), identify whether a release ZIP or source build is appropriate, and show the exact commands that would run. Machine-wide installation requires a human-reviewed manifest and an administrator session; stop before elevation.
