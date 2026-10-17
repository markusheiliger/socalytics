# Quickstart: Analysis Run Cancellation

Validation guide for the implemented feature. Contracts:
[cancel-analysis-run-api.md](contracts/cancel-analysis-run-api.md) and
[web-ui-cancel-action.md](contracts/web-ui-cancel-action.md). Data changes:
[data-model.md](data-model.md).

## Prerequisites

- .NET SDK from `src/platform/global.json`, a running Docker-compatible
  runtime, SoftHSM2 on Linux for the end-to-end tests.
- Node.js 24, Kiota 1.36.0 on `PATH`, Playwright 1.63.0 Chromium for the Web
  UI (`src/clients/web`).

## Platform

From the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~RunCancellation|FullyQualifiedName~AnalysisValueObjectTests"
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build --filter "FullyQualifiedName~RunCancellationEndToEndTests"
```

Expected:

- A Coach or Club Admin cancels a `Requested` and a `Running` run (`200`); a
  repeat with the old or new version returns `200` unchanged; a Viewer gets
  `403`, another team's member `404`, a missing version `428`, a wrong version
  `412`, a completed run `409 analysis-run-terminal`.
- After a cancellation: no `analysis.ready-work` row of the run is `pending`;
  exactly one `analysis.run-state-changed` row with `state: Cancelled`; one
  `analysis.run-cancelled` audit event; claims answer `claim-obsolete`;
  heartbeats, result grants, upstream reads, and completions of the former
  active attempt answer `attempt-obsolete`; accepted results are unchanged.
- The end-to-end test shows the simulated runtime's container stopped and the
  attempt directory removed within two heartbeat intervals, and no result of
  the run accepted afterwards.
- `OpenApiSnapshotTests` pass with the refreshed snapshot (refresh only in a
  development environment with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`).

## Web UI

From `src/clients/web`:

```powershell
npm ci
npm run build
npm test
npm run test:e2e -- --project simulated
npm run test:e2e
```

Expected:

- Unit tests: adapter mapping and `If-Match`, action visibility by role and
  state, dialog behavior and reason limit, cancelled-run display.
- `simulated`: `run-cancellation.spec.ts` cancels a running run through the
  dialog, shows the cancelled state with the reason, hides the action for
  Viewers, handles a conflict, and passes the axe checks and keyboard flow.
- Platform chain: the cancellation project after `platform-upload` cancels
  the run created by the upload flow on the running platform.

## Manual check (optional)

Start `dotnet run --project src/platform/SocAlytics.Platform.AppHost`, sign
in as a Coach, upload and finalize recordings for a match, and cancel the
resulting run from the match page; the run shows `Cancelled` with your account
name and reason.
