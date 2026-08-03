# Domains

Abode to Web serves dynamic demos and local services. A primary portfolio should remain on an independent static platform such as Cloudflare Pages so it stays available through PC sleep, home power loss, internet failure, or maintenance.

## Request path

```text
https://sample-static.example.com
        │ Cloudflare public TLS + DNS
        v
Cloudflare Tunnel (outbound connector on the PC)
        │ http://127.0.0.1:4580, original Host preserved
        v
Abode to Web gateway
        │ active healthy deployment route
        v
Sample Static reserved internal port
```

Do not create a DNS A/AAAA record pointing at the home IP, open a router port, or expose `4545`. Public project traffic uses the gateway listener only.

## Add a hostname

1. Confirm you own the domain and that its zone is active in Cloudflare.
2. Open the project's **Domain** page.
3. Enter one fully qualified hostname, for example `sample-static.example.com`.
4. Review the confirmation. Changing a public hostname can break links and active sessions.
5. Validate and save the manifest.
6. Deploy/start the project and confirm it is healthy through a local gateway request:

   ```powershell
   Invoke-WebRequest http://127.0.0.1:4580/ -Headers @{ Host = 'sample-static.example.com' }
   ```

7. Regenerate tunnel ingress and explicitly create its DNS route:

   ```powershell
   pwsh -File scripts/configure-tunnel.ps1 `
     -TunnelName 'abode-to-web-home' `
     -CreateDnsRoutes `
     -DnsHostname 'sample-static.example.com' `
     -RestartService
   ```

8. Test `https://sample-static.example.com` from a phone on cellular data.

Hostnames are unique, normalized, and exact. Wildcards are not accepted in the initial release. Unknown gateway hosts return 404 without listing projects. A known stopped project receives the safe branded offline page when its `routing.offlinePage` setting is enabled; when disabled, the gateway returns an empty `503`. A known unhealthy project receives a safe temporary-unavailable page. None of these responses disclose local ports or paths.

## Cloudflare settings

Use **Full (strict)** for unrelated Cloudflare Pages/custom origins as appropriate. Tunnel-to-Abode to Web traffic is loopback HTTP by design because TLS already terminates at Cloudflare and no LAN hop exists. Keep Cloudflare proxying enabled for tunnel CNAMEs.

Configure caching per application, not globally, when dynamic responses or WebSockets are involved. Do not cache administrative or authenticated responses. Cloudflare Access is recommended for protected demos and required for any future remote dashboard hostname.

For a protected demo, create a Cloudflare Access self-hosted application for the exact hostname. Configure `abodeToWeb.cloudflare.access.teamDomain` with the team's HTTPS `*.cloudflareaccess.com` domain and `routing.accessApplicationAudience` with that application's 64-character hexadecimal AUD tag. Abode to Web validates Cloudflare's assertion against the team's certificate endpoint and the project-specific audience; it does not store an Access token. See [Cloudflare Tunnel](CLOUDFLARE.md#protect-an-application-with-cloudflare-access).

## Portfolio status export

`GET /api/v1/projects/export` returns non-secret project names, public URLs, status, and safe metadata for a separate portfolio build. Export it locally without exposing the dashboard API:

```powershell
$status = Invoke-RestMethod http://127.0.0.1:4545/api/v1/projects/export `
  -UseDefaultCredentials -AllowUnencryptedAuthentication -MaximumRedirection 0
$status | ConvertTo-Json -Depth 6 |
  Set-Content 'C:\path\to\portfolio\data\abode-to-web-projects.json' -Encoding utf8NoBOM
```

PowerShell requires `-AllowUnencryptedAuthentication` before it will perform Windows authentication over HTTP. This command is intentionally limited to Abode to Web's loopback listener and disables redirects; do not reuse those flags for a remote URL.

The portfolio's static build can consume that reviewed snapshot. Treat status as optional decoration: the portfolio must still build and render when Abode to Web is offline. Never tunnel the dashboard API merely so hosted CI can fetch live status, and never make the portfolio runtime depend on the home PC.

## Remove or rename

1. Disable or change the route in Abode to Web and confirm the manifest change.
2. Regenerate and validate tunnel configuration.
3. Restart `cloudflared`.
4. Confirm the old hostname returns 404.
5. Remove or redirect the old Cloudflare DNS record manually.

Abode to Web intentionally does not delete DNS records because they may be shared with Pages, redirects, email verification, or another service.
