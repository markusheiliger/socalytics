# Quickstart: Validating the Audit Evidence View

Run from the repository root. Docker is required for the platform tests and the `platform-audit` browser project.

## Platform

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~AuditEvidence"
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter "FullyQualifiedName~AuditEvidence|FullyQualifiedName~OpenApiSnapshot"
```

Expected: authorization, filtering, paging, minimization, self-audit, rate limit, index-backed performance (100,000 events, first page within 2 seconds), and typed OpenAPI schema tests pass; the OpenAPI snapshot test passes against the refreshed snapshot ([contracts/audit-events-api.md](contracts/audit-events-api.md)).

## Web UI

```bash
cd src/clients/web
npm ci
npm run build
npm test
npx playwright test --project simulated
npx playwright test --project platform-audit
```

Expected: unit tests and the simulated audit specs (access, filters in the address, paging, refresh, detail, states, offline, no stored audit data, accessibility) pass; the platform spec passes against the AppHost with real evidence produced by the scripted Club Admin ([contracts/audit-web-ui.md](contracts/audit-web-ui.md)).

## Manual scenario

1. Start the AppHost (`dotnet run --project src/platform/SocAlytics.Platform.AppHost`) and sign in as the bootstrap Club Admin.
2. Open Audit: the newest events appear with the read-only and audited notice.
3. Filter by event type `audit.evidence-read`: the read of step 2 is listed with its filter shape and no result content.
4. Sign in as a Coach: no Audit entry; opening `/audit` shows the not-permitted view; a later Club Admin read shows one `denied` event for the Coach.

## Documentation

`node .github/scripts/check-markdown.mjs` passes after the documentation changes.
