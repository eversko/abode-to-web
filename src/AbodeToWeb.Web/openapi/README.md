# Frontend API contracts

`abode-to-web.v1.json` is the OpenAPI document emitted by the matching
`AbodeToWeb.Server` build. `src/api/generated.ts` is generated from it and must
not be edited by hand.

When the server contract changes:

```powershell
Invoke-WebRequest http://127.0.0.1:4545/openapi/v1.json -OutFile openapi/abode-to-web.v1.json
npm run api:generate
npm run build
npm test
```

UI-friendly aliases and the few SSE response shapes not yet described by the
server document live in `src/api/schema.ts`. Endpoint paths and wire mapping
remain centralized in `src/api/routes.ts` and `src/api/client.ts`.

`npm run dev` starts the self-contained mock preview on port 4545. With a real
Abode to Web server already listening on 4545, `npm run dev:http` starts Vite on
4546 and proxies `/api` to that server.
