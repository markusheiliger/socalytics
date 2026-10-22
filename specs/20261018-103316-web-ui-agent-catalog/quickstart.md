# Quickstart: Web UI Agent Catalog Administration

Validation guide for the Agent Catalog area. Commands run from the repository root unless stated otherwise; they are
the documented commands in `AGENTS.md`. A Docker-compatible container runtime is required for the platform tests and the
platform browser test.

## Prerequisites

- [Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md) and
  [Web UI Analyst Registry Administration](../archive/20261017-185438-web-ui-registry-admin/spec.md) are merged.
- .NET SDK from `src/platform/global.json`, Node.js 24, Kiota 1.36.0 on `PATH`, Playwright 1.63.0 Chromium.

## Automated checks

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected: `AgentCatalogOpenApiTests` and `OpenApiSnapshotTests` pass; the catalog integration tests pass unchanged
([contracts/agent-catalog-openapi.md](contracts/agent-catalog-openapi.md)).

```powershell
cd src/clients/web
npm ci
npm run build
npm test
npm run test:e2e -- --project simulated
npm run test:e2e
```

Expected: the build regenerates the Kiota client from the snapshot; Vitest passes the adapter, outcome, location,
version-proposal, limits-versus-contracts, form, dialog, i18n, and service-worker tests; the simulated project passes
`agent-catalog.spec.ts`, `agent-catalog-coach.spec.ts`, and `agent-catalog-access.spec.ts`; the full run includes the
chained `platform-agent-catalog` project.

## Manual walkthrough (local AppHost)

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost`, open the Web UI, and sign in as a Club Admin.
2. Open **Agent catalog**: nine agents; `specialist-tactics` and `specialist-video-analysis` show `0.1.0` as offered in
   triage conversations; Coach Agents show "not available in conversations yet" (SC-001: at most 3 steps to a prompt).
3. Open `specialist-tactics` → `0.1.0` → its prompt template; start a new version from it, change the text, and register
   it as `0.1.1` (proposed).
4. Open `specialist-tactics` → **New version** from `0.1.0`, choose prompt template `0.1.1`, register `0.2.0`, approve it
   (self-approval note), and enable it; the confirmation says only questions submitted afterwards use it.
5. The overview shows `0.2.0` as offered. Retiring `0.2.0` is refused as enabled; retiring `0.1.0` succeeds.
6. Sign in as a Coach: no **Agent catalog** entry; `/agent-catalog` shows the not-permitted view.
7. Go offline in the browser: the area shows the connection-required state.

Stop the AppHost with `Ctrl+C`.
