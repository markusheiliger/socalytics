# Quickstart: Validating the Explicit Analysis Run Request

Run from the repository root. Docker-compatible runtime required for the Integration tests. Operation details:
[contracts/request-analysis-run.md](contracts/request-analysis-run.md); entities: [data-model.md](data-model.md).

## 1. Build and run the focused evidence

```bash
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~ExplicitAnalysisRequest|FullyQualifiedName~RunCreation|FullyQualifiedName~RunSupersession"
```

Expected: the explicit-request tests pass, and the existing run creation and supersession tests still pass (the shared
creator changes no finalization behavior).

## 2. Scenarios the tests exercise

1. Finalized match, default workflow changed to a new version, Coach posts the request with a fresh key: `202`,
   `created: true`, a new run for the same recording-set version and the new workflow version, the previous run
   superseded by it, one `analysis.run.request` audit event, one `analysis-requested` receipt with `run-created`.
2. Same request again with a new key and no registry change: `202`, `created: false`, same run, no supersession, no new
   segment requests, a `run-exists` receipt.
3. Same key again: identical answer, no second receipt, no second audit event; the same key as another member or for
   another match: `409 idempotency-key-reused`.
4. Two simultaneous requests, and a request racing the finalization consumer: exactly one run for the identity.
5. Default workflow set back to a version whose run a newer run superseded: `created: false`, the superseded run, and
   `currentRunId` naming the current run.
6. Refusals: no finalized set (`409`, `rejected` receipt), unresolvable default workflow (`409`, `rejected` receipt),
   missing key (`400`), Viewer (`403`), foreign team and unknown match (`404`), unauthenticated (`401`), Registry
   unavailable (`503`): none creates a run; only the first two leave a receipt.
7. A repeated `rejected` request with the same key after the cause was fixed returns the recorded refusal.
8. Operations: the Club Admin's summary counts and the receipt list show `analysis-requested` receipts next to
   `recordings-finalized` ones; message detail has no payload.

## 3. Contract and structure checks

```bash
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~Structure|FullyQualifiedName~AnalysisSchema"
node .github/scripts/check-markdown.mjs
```

The snapshot is refreshed once, outside the verification environment, with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`.
The Web UI build and unit tests (`npm ci`, `npm run build`, `npm test` in `src/clients/web`) must stay green after the
snapshot changes.
