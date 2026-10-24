# Quickstart: Match Conversation Archive and Delete

Validation guide for the implemented feature. Everything is development evidence; none of it is deployment support or
production readiness.

## Prerequisites

- .NET 10 SDK (`src/platform/global.json`), a running Docker-compatible runtime, Node.js 24 with Kiota 1.36.0 on
  `PATH`, and Playwright 1.63.0 Chromium (`src/clients/web`).

## Platform

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~AgentOrchestration"
dotnet test src/platform/Tests/SocAlytics.Platform.Architecture.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter OpenApiSnapshotTests
```

Expected outcomes (see [spec.md](spec.md) and [contracts/](contracts/README.md)):

1. Archive then unarchive: the conversation leaves and returns to the active list, history stays readable, questions
   are refused with `conversation-archived` while archived (US1, SC-004).
2. Delete: reads, polls, and workload operations answer not found at once; nonterminal invocations end
   `lifecycle-suppressed` with unchanged `attempts_used`; the purge completes; no question, advice, evidence, or lineage
   text remains in the Primary copy or in audit and logs (US2, SC-001 to SC-003).
3. Repeat delete: `204` for the initiator in scope, `404` for everyone else (clarification 1).
4. A deliberately inconsistent tombstoned conversation is restored to suppression by one reconciliation pass; a
   purge failing past `MaxAttempts` is `unresolved`, then completes on a later reconciliation (SC-006, FR-010).
5. Concurrent archive, unarchive, and delete requests yield one success and conflicts or already-satisfied reports (SC-007).

After an API change, refresh the OpenAPI snapshot only locally with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`.

## Web UI

```bash
cd src/clients/web
npm ci && npm run build && npm test
npx playwright test --project simulated
npx playwright test --project platform-triage
```

Expected: archive, unarchive, delete, filter, refusal, conflict, unavailable, and offline states pass in both projects;
actions are absent for non-initiators and Viewers; no conversation content appears in `localStorage`, `sessionStorage`,
IndexedDB, or Cache Storage (SC-005, SC-008).

## Documentation

```bash
node .github/scripts/check-markdown.mjs
```
