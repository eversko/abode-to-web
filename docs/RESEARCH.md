# Research and acknowledgements

Abode to Web was designed after reviewing public documentation and user-facing behavior from established tools. This research supplied patterns and failure vocabulary only. No source code, styles, screenshots, text, icons, or assets from these projects/products were copied.

## Safe predefined actions

[OliveTin](https://github.com/OliveTin/OliveTin) demonstrates the value of predefined YAML actions, structured execution rather than a browser shell, bounded concurrency/timeouts, retained action output, auditability, and operation tracking IDs. Its [security design](https://docs.olivetin.app/2k/security/design_choices.html), [shell-versus-exec guidance](https://docs.olivetin.app/2k/action_execution/shellvsexec.html), and [Windows service layout](https://docs.olivetin.app/install/windows_service.html) informed Abode to Web's closed action registry and Program Files/ProgramData split. OliveTin is AGPL-3.0; its implementation was not reused.

## Dashboard information hierarchy

[Homepage](https://gethomepage.dev/configs/services/) shows an effective group → service → compact status hierarchy with progressive disclosure. Homepage is GPL-3.0; no code or visual assets were reused.

[Dashy](https://dashy.to/docs/pages-and-sections/) demonstrates responsive page → section → item hierarchy and status indicators that combine labels/shapes with color. Dashy is MIT-licensed, but Abode to Web still implements its interface independently rather than importing a generic dashboard.

## Supervisor concepts

[Supervisor](https://supervisord.org/configuration.html) distinguishes startup success, backoff, fatal state, expected/unexpected exit, graceful timeout, process-group stop, and bounded stdout/stderr rotation.

[systemd start limiting](https://www.freedesktop.org/software/systemd/man/latest/systemd.unit.html) supplies a useful circuit-breaker concept: cap restarts in a time window and require time/manual reset after the limit.

[PM2 restart strategies](https://pm2.keymetrics.io/docs/usage/restart-strategies/) document capped exponential restart delay and reset after stable runtime.

Abode to Web adopts those reliability concepts with its own state machine and Windows implementation. Supervisor has a permissive custom license; systemd includes GPL/LGPL code; PM2 is AGPL-3.0. None of their source was reused.

## Windows process trees

Microsoft's [Job Objects](https://learn.microsoft.com/windows/win32/procthread/job-objects) and [nested jobs](https://learn.microsoft.com/windows/win32/procthread/nested-jobs) are the native basis for grouping descendants, accounting, notifications, and verified whole-tree termination. Abode to Web combines a Job Object with PID creation time, executable/deployment identity, and cautious restart reconciliation; PID files alone are insufficient.

## Health-gated deployment flow

[Render deploys](https://render.com/docs/deploys), [failure handling](https://render.com/articles/how-render-handles-deploy-failures), and [artifact rollback](https://render.com/docs/rollbacks) demonstrate isolated build, parallel candidate startup, health gating, route switch, drain, and preservation of the old release after candidate failure.

[Vercel projects/deployments](https://vercel.com/docs/deployments/overview) and [instant rollback](https://vercel.com/docs/deployments/rollback-production-deployment) informed commit-centered deployment history and retained-artifact route rollback.

[Railway deployment states](https://docs.railway.com/deployments/reference), [deployment actions](https://docs.railway.com/deployments/deployment-actions), and [build/runtime logs](https://docs.railway.com/observability/logs) informed durable stage progress, separated logs, contextual secondary actions, overlap, and draining.

[Cloudflare Pages rollbacks](https://developers.cloudflare.com/pages/configuration/rollbacks/) reinforce that rollback targets should be previously successful and require confirmation.

These commercial product interactions were observed at a conceptual level. Abode to Web does not copy their dashboards, wording, screenshots, or proprietary implementation.

## Design boundary

The result is intentionally narrower than a home-lab portal, cPanel, Portainer, or a commercial platform: one person's Windows apps, predefined safe actions, Git worktrees, health-gated routes, useful logs, and Cloudflare Tunnel. Research was used to avoid rediscovering known failure modes, not to inherit another product's feature set.
