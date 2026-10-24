# Quickstart: Analysis Run Supersession

Validation guide for the feature. Data shapes are in
[data-model.md](data-model.md); API fields in
[contracts/analysis-run-reads-api.md](contracts/analysis-run-reads-api.md);
Web UI behavior in
[contracts/web-ui-supersession-markers.md](contracts/web-ui-supersession-markers.md).

## Prerequisites

- The Analysis Run Cancellation feature
  ([spec](../20261017-182600-analysis-run-cancellation/spec.md)) is merged.
- .NET SDK from `src/platform/global.json`, a running Docker-compatible
  container runtime, and on Linux SoftHSM2 for the end-to-end tests.
- Node.js 24, Kiota 1.36.0 on `PATH`, and Playwright 1.63.0 Chromium for the
  Web UI.

## Platform

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~RunSupersession"
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~Structure"
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter OpenApiSnapshotTests
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build --filter "FullyQualifiedName~RunSupersession"
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected:

- Finalizing a second recording-set version while the first run runs leaves
  the first run `Cancelled` with `cancellationCause: "superseded"`, no
  `cancelledBy`, `supersededBy` naming the new run, one
  `analysis.run-superseded` audit event, one `analysis.run-state-changed`
  outbox message, discarded ready-work, and `attempt-obsolete` /
  `claim-obsolete` answers for its work.
- A completed first run keeps its state and version and only reports
  `supersededBy`; no audit event or notification is written for it.
- Every delivery order of three versions (with duplicates and a concurrent
  member cancellation) ends with the same supersession rows, and the newest
  version's run is the only `isCurrent` run.
- Reconciliation supersedes runs inserted without supersession rows and is
  idempotent.
- With the simulated runtime, the Manager stops and cleans up a superseded
  run's attempt within two heartbeat intervals.

## Web UI

Run from `src/clients/web`:

```powershell
npm ci
npm run build
npm test
npm run test:e2e -- --project simulated
npm run test:e2e
```

Expected: the match page marks the current run and each superseded run with
the superseding run's recording-set finalization time and the supersession
time; a run cancelled by supersession reads as superseded without a member;
the simulated and platform browser projects and the axe checks pass.

## Manual check (optional)

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` and open the
   Web UI.
2. As a Coach, create a match, upload a recording, finalize the set, and wait
   until its run is `Running` with an active simulated attempt.
3. Finalize the set again (a new recording-set version) and watch the first
   run turn into "Cancelled (superseded)" with the superseded marker and the
   new run marked "Current run" without a reload.
