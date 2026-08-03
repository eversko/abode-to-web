# Cloudflare Tunnel

Cloudflare Tunnel is the supported public connection. It makes outbound connections from `cloudflared` to Cloudflare; no router port-forward, public home-IP A record, or inbound firewall opening is required. The tunnel is a separate Windows Service so it and Abode to Web can recover independently.

Public HTTPS terminates at Cloudflare. Tunnel ingress sends plain HTTP only to the loopback Abode to Web gateway at `127.0.0.1:4580`.

## Account prerequisites

You must own the domain and place its DNS zone on Cloudflare. Authentication and domain ownership are account-level actions Abode to Web cannot perform for you.

Open an administrator PowerShell 7 session only for the machine-wide binary install:

```powershell
pwsh -File scripts/install-cloudflared.ps1
```

Close that session. As your normal Windows user, perform the browser authentication and account-side tunnel creation so `cert.pem` and management credentials stay in the intended profile:

```powershell
cloudflared tunnel login
cloudflared tunnel create abode-to-web-home
cloudflared tunnel list
```

The login opens a browser and creates `cert.pem` in your user profile. Neither Abode to Web nor its scripts print or copy the certificate into the repository.

## Create and configure a locally managed tunnel

First validate that Abode to Web is running and every desired public project has a reviewed hostname:

```powershell
pwsh -File scripts/status-abode-to-web.ps1
```

Then open PowerShell 7 as administrator and run:

```powershell
pwsh -File scripts/configure-tunnel.ps1 `
  -TunnelName 'abode-to-web-home' `
  -TunnelId 'YOUR-TUNNEL-UUID' `
  -CredentialsFile 'C:\Path\to\.cloudflared\YOUR-TUNNEL-UUID.json' `
  -InstallService
```

The script prefers Abode to Web's non-secret `GET /api/v1/projects/export` registry and falls back to the reviewed ProgramData manifest. It validates every hostname, creates explicit ingress routes to `http://127.0.0.1:4580`, always adds a final `http_status:404` rule, validates with the installed `cloudflared`, copies the tunnel credential into a restricted ProgramData directory, and installs an automatically starting `cloudflared` service under LocalService plus a per-service SID. It also pins cloudflared's local metrics server to `127.0.0.1:49312`; Abode to Web reads only its `/ready` response to confirm an active Cloudflare connection without needing the interactive user's account certificate.

An empty or malformed registry is rejected so a transient error cannot replace working ingress with a 404-only configuration. `-AllowEmptyIngress` is available only for the deliberate act of withdrawing every public hostname.

This first command does not change DNS. Review `%ProgramData%\AbodeToWeb\cloudflared\config.yml`. If elevation uses the same Windows profile that owns `cert.pem`, explicitly create routes with the script:

```powershell
pwsh -File scripts/configure-tunnel.ps1 `
  -TunnelName 'abode-to-web-home' `
  -CreateDnsRoutes `
  -RestartService
```

`-CreateDnsRoutes` is the deliberate permission to change Cloudflare DNS. The script never changes nameservers, Access policies, router settings, firewall rules, or the PC power plan.

`cloudflared tunnel route dns` creates a CNAME and refuses to overwrite an existing A, AAAA, or CNAME. On later config regenerations, omit `-CreateDnsRoutes` when every route already exists. When adding one hostname to an established tunnel, combine `-CreateDnsRoutes -DnsHostname 'new.example.com'`; the script still generates the complete ingress registry but asks Cloudflare to create only that new route. If a hostname already points somewhere else, review and change or remove that record manually in Cloudflare; Abode to Web will not silently take it over.

If UAC uses a different administrator account, return to the original normal-user session for account-side DNS commands instead of copying `cert.pem` into the administrator profile:

```powershell
cloudflared tunnel route dns YOUR-TUNNEL-UUID sample-static.example.com
cloudflared tunnel route dns YOUR-TUNNEL-UUID sample-docs.example.com
```

## Existing tunnel

Supply an exact UUID when tunnel names are ambiguous:

```powershell
pwsh -File scripts/configure-tunnel.ps1 `
  -TunnelName 'abode-to-web-home' `
  -TunnelId '00000000-0000-0000-0000-000000000000' `
  -CredentialsFile "$HOME\.cloudflared\00000000-0000-0000-0000-000000000000.json" `
  -InstallService
```

The credential file is referenced by path and copied with restrictive ACLs. Its JSON content is never printed or embedded in generated YAML.

If a service named `cloudflared` already has a different command line, the script stops before repurposing it. Choose a distinct `-ServiceName`, or review the existing connector and pass `-ReplaceExistingService` deliberately. The uninstaller applies the same ownership check and requires `-Force` before removing a connector that does not reference Abode to Web's configuration.

The 0.1 dashboard diagnostic watches the conventional `cloudflared` service name and the generated readiness listener at `127.0.0.1:49312`. If you deliberately choose a different service name to coexist with another connector, use `status-tunnel.ps1 -ServiceName '<name>'` and the matching service-control script parameter as the authoritative local check; the dashboard's service-state card will not represent that custom name.

## Validate and check status

```powershell
pwsh -File scripts/status-tunnel.ps1
cloudflared tunnel --config "$env:ProgramData\AbodeToWeb\cloudflared\config.yml" ingress validate
```

Status checks binary/version, service state, configuration validity, loopback gateway reachability, and Abode to Web's tunnel diagnostic endpoint. A running service does not alone prove Cloudflare connectivity; confirm a public hostname from a network outside the PC.

To restart after a reviewed config change:

```powershell
# Administrator PowerShell 7
pwsh -File scripts/restart-tunnel.ps1
```

Long-lived WebSocket and streaming connections can drop during a connector restart.

## Add or remove a hostname

Change the Abode to Web project manifest/UI first and validate it. Regenerate the complete tunnel config, review it, explicitly create only the new DNS route with `-DnsHostname` if needed, then restart the tunnel. The gateway knows the hostname before public traffic arrives, avoiding accidental exposure or an unknown-host response.

Removing a hostname from ingress does not delete its Cloudflare DNS record. Delete that record manually only after confirming it is no longer used.

## Protect an application with Cloudflare Access

For a project with `visibility: protected`, first create a Cloudflare Access self-hosted application for that exact hostname and attach the account-side policy you intend to enforce. Then record only the verification metadata in the Abode to Web manifest:

```yaml
abodeToWeb:
  cloudflare:
    enabled: true
    tunnelName: abode-to-web-home
    access:
      teamDomain: "https://your-team.cloudflareaccess.com"

apps:
  - id: sample-static
    routing:
      hostname: sample-static.example.com
      accessApplicationAudience: "REPLACE_WITH_THE_64_HEX_CHARACTER_ACCESS_AUD"
    visibility: protected
```

`abodeToWeb.cloudflare.access.teamDomain` must be the HTTPS `*.cloudflareaccess.com` domain assigned to your Cloudflare Access team, with no path, query, or custom port. `routing.accessApplicationAudience` must be that Access application's 64-character hexadecimal AUD tag. Abode to Web rejects the whole manifest when a protected route is missing either field or either value is malformed.

At request time the gateway reads `Cf-Access-Jwt-Assertion`, resolves the matching RS256 signing certificate from the configured team's `/cdn-cgi/access/certs` endpoint, and verifies issuer, time claims, application token type, and that project's exact audience. Abode to Web stores no Cloudflare Access token or private credential. A request sent directly to the loopback gateway for a protected hostname has no valid Access assertion and is rejected; this is expected.

Creating the Access application and policy remains a deliberate Cloudflare account action. Changing `visibility` to `protected` does not create that policy for you.

## Dashboard access

The public application tunnel must not route the dashboard. Remote administration is disabled by default. If deliberately enabled later, use a separate `panel.example.com` route plus Cloudflare Access, configure expected identity/token validation in Abode to Web, keep the application gateway separate, and test that direct/bypass requests fail. Abode to Web does not provide a custom internet password system.

## Uninstall

```powershell
# Removes the local service and winget package; preserves local config/credential copies.
pwsh -File scripts/uninstall-cloudflared.ps1

# Also deletes the protected local tunnel material after confirmation.
pwsh -File scripts/uninstall-cloudflared.ps1 -RemoveConfiguration
```

This never deletes the Cloudflare tunnel object, DNS records, or Access policies. Remove those account resources manually if appropriate.

Official reference: [Cloudflare's Windows service guide](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/local-management/as-a-service/windows/) and [configuration validation](https://developers.cloudflare.com/tunnel/advanced/local-management/configuration-file/).
