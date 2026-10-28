# Part State Evidence Record

Development evidence for the deterministic evidence part state test. One section per measuring or confirming task.

## T001

Baseline from the repository root on Linux.

| Command | Exit code | Result |
| --- | --- | --- |
| `dotnet restore src/platform/SocAlytics.Platform.slnx` | 0 | Restored |
| `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore` | 0 | Build succeeded, 0 warnings, 0 errors |
| `docker info` | 0 | Docker daemon reachable |

Tool versions:

- .NET SDK `10.0.403`
- Docker `29.4.2`, build `055a478`
- `ffmpeg` `n8.1.3-9-g29e619e767-20260930`
- `ffprobe` `n8.1.3-9-g29e619e767-20260930`

## T002

Measured on Linux, 4 cores, built baseline of T001, `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build`.

| Run set | Filter | Load | Runs | Passed | Failed |
| --- | --- | --- | --- | --- | --- |
| Alone | `FullyQualifiedName~Never_materialized_segment_is_materializing` | none | 20 | 20 | 0 |
| Class under load | `FullyQualifiedName~Analysis.Evidence` | 4 busy loops (`while :; do :; done`, one per core; `stress-ng` is not installed) | 5 | 4 | 1 |

Each class run has 78 tests. The four passing runs took 1 m 21 s to 1 m 26 s. The failing run took 1 m 33 s with 77 passed and 1 failed.

Failure capture (load run 1):

- Failing test: `EvidencePartStateTests.Purged_segment_is_rematerializing_beside_available_parts_until_the_worker_ran` (not the target test), `EvidencePartStateTests.cs` line 145, the assertion after `MaterializeSegmentsAsync`.
- Expected `["available", "available"]`, got `["available", "rematerializing"]`.
- The signature is the one the plan expects from the default-on background worker: the purged segment's row is not yet `available` after the explicit materialization (a claimed or leased row finds nothing due for the explicit call).
- The target test passed in all 25 runs (20 alone and 5 under load), so its own failure was not reproduced by these runs.
- The test output carries no segment rows (state, lease owner, demanded), no `MaterializeSegmentsAsync` outcomes, and no `Factory.Logs`, and the failing run's containers are disposed, so those three captures are not available for this failure. The T003 probe measures the worker's effect directly.

## T003

Temporary probe `Probe_worker_iteration_between_resolution_steps` in `EvidencePartStateTests.cs` (removed by T004): 4 variants of 10 scenarios each, never-materialized scenario with `Segments:Materialization:PollInterval` 50 ms.

| Variant | Worker iteration | Runs that would fail the test |
| --- | --- | --- |
| `worker-after-first-resolution` | real worker enabled (`Analysis:Workers:Enabled=true`) | 10/10 |
| `worker-after-count` | real worker enabled | 10/10 |
| `explicit-after-first-resolution` | `MaterializeSegmentHandler.ProcessDueAsync` from a second scope, workers off | 0/10 |
| `explicit-after-count` | same, between the count and `MaterializeSegmentsAsync` | 0/10 |

In every failing run the first resolution reported `materializing` and one demanded row, `MaterializeSegmentsAsync` processed 0 rows, the row was still `materializing` (claimed by the worker), and the second resolution read `materializing`: the expected signature. The explicit call completes synchronously, so it never reproduces the failure; only the real background worker does.

## T004

Summary of the runs, failure captures, and probe results of T002 and T003, and the confirmed cause.

Runs and captures:

- T002: the target test passed in 25 of 25 runs (20 alone, 5 inside the class under CPU load). One of 5 class runs under load failed in the sibling `Purged_segment_is_rematerializing_beside_available_parts_until_the_worker_ran` (expected `["available", "available"]`, got `["available", "rematerializing"]`): the explicit `MaterializeSegmentsAsync` left a row not `available`, the signature of a row that something else had already claimed. Segment rows, explicit outcomes, and `Factory.Logs` were not captured by that run.
- T003: with the real worker enabled through `extraSettings` (`Analysis:Workers:Enabled=true`, `Segments:Materialization:PollInterval` 50 ms), the never-materialized scenario would fail the target test in 10 of 10 runs after the first resolution and in 10 of 10 runs after the count. The explicit `ProcessDueAsync` from a second scope with a distinct lease owner never reproduced it (0 of 20 runs), because it completes synchronously.

Confirmed cause: the candidate "the default-on background segment worker races the explicit materialization" is confirmed. `EvidenceScenario.Settings` does not set `Analysis:Workers:Enabled`, so the API host's `SegmentMaterializationWorker` runs by default beside the test's explicit `MaterializeSegmentsAsync` and `MaterializeAllAsync` calls. When the worker claims a demanded row first, the explicit call finds nothing due and the second resolution reads `materializing` (or `rematerializing`) instead of `available`. The race depends on runner load, which is why it appears intermittently. It is a test-scenario cause, not a platform defect, so T005 applies and T006 does not.

The temporary probe `Probe_worker_iteration_between_resolution_steps` and its helper `RunWorkerIterationAsync` have been removed from `EvidencePartStateTests.cs`; the file is back to its content before T003.

## T006

Decision: no platform defect exists, and no platform file is changed.

Basis: the `## T004` section confirmed a test-side cause. `EvidenceScenario.Settings` left `Analysis:Workers:Enabled` at its default, so the API host's `SegmentMaterializationWorker` ran beside the test's explicit `MaterializeSegmentsAsync` and `MaterializeAllAsync` calls. The platform behaved as documented in both outcomes: a row claimed by the worker is `materializing` (or `rematerializing`) until the worker finishes, and an explicit call that finds no due row processes nothing. The explicit `ProcessDueAsync` from a second scope never reproduced the failure (0 of 20 runs), which shows that demand recording, part states, and materialization are correct when the racing input is controlled. The default-on worker is fixed in the test scenario by T005.

Consequence (FR-004): nothing under `src/platform/SocAlytics.Platform.Application/Segments/` or `src/platform/SocAlytics.Platform.Infrastructure/Workers/` is changed, and no focused test is added under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/`.
