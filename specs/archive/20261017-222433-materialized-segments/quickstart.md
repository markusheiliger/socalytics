# Quickstart: Materialized Video Segments and Analyst Input Staging

Validation guide for the implemented feature. Data shapes are in [data-model.md](data-model.md); interfaces are in
[contracts/](contracts/).

## Prerequisites

- .NET 10 SDK per `src/platform/global.json` and `src/analysts/manager/global.json`; a running Docker-compatible
  runtime; on Linux, SoftHSM2 for Manager and end-to-end tests.
- FFmpeg and ffprobe of the version the [environment feature](../20261017-163801-analyst-media-environment/spec.md)
  pins (8.1 LGPL) on `PATH`, or configured through `Segments__Toolchain__FfmpegPath` and
  `Segments__Toolchain__FfprobePath`. The verification environment provides them; locally install a matching build.
- Python 3.12 and `uv` for the SDK tests (see the Python Analyst SDK feature).

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
```

From `src/analysts/sdk`: `uv sync --locked --all-groups` then `uv run pytest`.

Expected outcomes:

| Scenario | Evidence |
| --- | --- |
| Boundaries, clipping, and manifest contents | Segments integration tests: manifests valid against `segments/segment-manifest` v1; encoded interval covers the requested interval; media digest equals stored bytes (SC-001) |
| Concurrency | 8 parallel requests for one identity produce exactly one association and no leftover candidate objects (SC-002) |
| Forbidden video | A `forbidden` node creates no demand, its job message has no `segmentMedia`, and the media read refuses (SC-003) |
| Failure containment | Injected transient failure recovers; persistent failure fails only that segment's `required` jobs and readies `optional` jobs without media (SC-005) |
| Media read | Stale, fenced, foreign, and not-applicable requests are refused and audited; grants expire no later than the lease |
| Staging | Manager tests on both runtimes: verified files at `/socalytics/media/`, digest mismatches and limit overruns fail before launch, nothing left after the attempt (SC-004) |
| SDK | `context.media` typed manifest and verified path; named input errors for missing, unexpected, or mismatched media |
| Purge | Club Admin list and purge; in-use segments kept; purged segment rematerialized for a later run (SC-007); Coach and foreign Club Admin refused |
| End to end | `SocAlytics.Platform.EndToEnd.Tests`: a synthetic 10-second recording, a 5-second test policy, and the Docker test Analyst in `media` mode produce accepted results for both segments (SC-006) |

## Manual check with the AppHost

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` with the toolchain available to the API.
2. Upload and finalize a short recording for a match whose default workflow includes a segment-scoped node with a
   `required` video policy (Development seed or a registered profile).
3. As the Club Admin, call `GET /api/v1/matches/{matchId}/materialized-segments` and watch the segments reach
   `available`; then `POST …/materialized-segments/purge` and observe `purged` and `inUse` counts.
4. Stop the AppHost with `Ctrl+C`.

Successful local runs are development evidence only; they do not establish production storage, cost, or toolchain
values.
