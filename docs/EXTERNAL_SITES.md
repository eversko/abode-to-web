# External sites

Abode to Web can keep a read-only inventory of sites hosted by cPanel, Spaceship, or another outside provider. External sites are deliberately separate from managed projects: they have no local process, gateway route, deployment, rollback, logs, or secret file.

Open **External sites** in the dashboard and choose **Add external site**. Record:

- a lowercase resource ID;
- the public HTTPS URL;
- a short provider label;
- an optional provider-management HTTPS URL;
- an optional local repository path for operator context.

Abode to Web validates and atomically saves the complete manifest. It checks only the configured public URL and reads response headers rather than downloading the response body. Redirects are not followed. A direct `2xx` response is healthy; other responses are unhealthy, and connection or TLS failures are reported as unknown with a safe message.

External-site entries never store cPanel credentials or authenticated browser session URLs. The management link is navigation only. Adding an entry does not change DNS, proxy the domain through Abode to Web, or prove that the local repository matches the public deployment.

## Configuration shape

```yaml
externalSites:
  - id: example-site
    name: Example Site
    description: "Externally hosted marketing site"
    url: "https://example.com/"
    provider: "Spaceship / cPanel"
    managementUrl: "https://www.spaceship.com/"
    repositoryPath: "C:/Projects/example-site"
```

Remote deployment remains outside this feature. Any future provider adapter must use an authenticated, encrypted protocol, require an explicit remote document root, fail closed on certificate errors, and preserve Abode to Web's reviewed-action model.
