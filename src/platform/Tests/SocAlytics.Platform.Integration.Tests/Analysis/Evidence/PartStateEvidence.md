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

## T007

Result: the check passed and no test file is changed. `EvidenceScenario.StartAsync` and the evidence tests wait on no background worker, and every test that calls `MaterializeSegmentsAsync` or `MaterializeAllAsync` is the only source of materialization in its scenario.

Which workers run: every worker registered in `InfrastructureServiceCollectionExtensions` that does analysis, messaging, lease, monitor, or email work returns at start when `AnalysisWorkerOptions.Enabled` (`Analysis:Workers:Enabled`) is `false`: `OutboxPublisherWorker`, `RecordingsFinalizedConsumerWorker`, `LeaseExpiryWorker`, `SegmentMaterializationWorker`, `AgentAttemptMonitorWorker`, `AgentWorkReconciliationWorker`, `PublicationReconciliationWorker`, `OutboxRetentionWorker`, `QueueRevocationWorker`, and `EmailDeliveryWorker`. `EvidenceScenario.Settings` sets that option to `false` (T005), so none of them runs in an evidence scenario. The ungated hosted services are not waited on: `UploadSessionExpiryWorker` only expires upload sessions that are past their expiry (the scenario's sessions are completed immediately), and the seeders, `DevelopmentBucketInitializer`, `NatsConnectionStarter`, `RegistrationExpiryWorker`, and `ClubBootstrapHostedService` run at host start or on a sweep and produce nothing the tests read.

Handler calls of `EvidenceScenario.StartAsync`, all synchronous in the test's own flow:

- Recording finalization: `SyntheticMedia.UploadAndFinalizeAsync` calls the API operations `POST /api/v1/matches/{matchId}/upload-sessions`, `.../upload-sessions/{sessionId}/completion`, and, for a second recording, `POST /api/v1/matches/{matchId}/recording-set-versions` (`FinalizeSetAsync`) and awaits each response; no outbox publisher or consumer is involved.
- `CreateRunAsync`: `IRecordingSetLookup.GetAsync`, then `HandleRecordingsFinalizedNotificationHandler.HandleAsync` with a notification built in the test, and requires `NotificationOutcome.RunCreated`; it replaces the outbox publisher and the recordings-finalized consumer.
- `SeedFactsAsync`: direct SQL for the readiness occurrence and the stale attempt, then `IAcceptedResultStore.InsertAsync` and `IIndexedFactStore.InsertAsync` in one `IUnitOfWork`; no lease expiry, Job Monitor, or Manager is involved.
- Sign-ins (`MemberAsync`, `ApiSession.SignInAsync`) and the club hierarchy use the API and direct SQL; no email delivery is awaited.

Evidence tests: `EvidenceResolutionTests`, `EvidenceAuthorizationTests`, `EvidenceRateLimitTests`, `EvidenceAuditAndLoggingTests`, and `EvidencePartStateTests` only call the evidence-media operation (`POST /api/v1/analysis/runs/{runId}/evidence-media`), the segment stores in a scope (`TryPurgeAsync`), SQL, and `MaterializeSegmentsAsync` or `MaterializeAllAsync`, which run `MaterializeSegmentHandler.ProcessDueAsync` to completion. The only wait is the `Task.Delay` of 5 seconds in `EvidenceResolutionTests` that lets a presigned link expire, which waits on object storage and the test clock, not on a worker.

Explicit settings: no evidence test relies on background behavior, so none needs an override of `Analysis:Workers:Enabled`. The `extraSettings` in use are `Analysis:Evidence:MaxDuration` (`EvidenceResolutionTests`, `EvidencePartStateTests`) and `Analysis:Evidence:ResolutionsPerMinute` (`EvidenceRateLimitTests`), and `EvidencePartStateTests` uses `configureServices` for its switchable object storage; none enables a worker.

Sole source of materialization: `MaterializeSegmentsAsync` and `MaterializeAllAsync` are called in `EvidenceResolutionTests` (the polling helper and four `MaterializeAllAsync` scenarios, plus the two-recording scenario), `EvidenceAuditAndLoggingTests`, and `EvidencePartStateTests`; with the `SegmentMaterializationWorker` off, they are the only callers of `MaterializeSegmentHandler.ProcessDueAsync` in each scenario, each with its own lease owner `evidence-test-<round>`. `EvidenceAuthorizationTests` and `EvidenceRateLimitTests` never materialize.

## T008

Measured on Linux, 4 cores, with the fix of T005 (`Analysis:Workers:Enabled` is `false` in `EvidenceScenario.Settings`), `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build`.

SC-001 runs:

| Run set | Filter | Load | Runs | Passed | Failed |
| --- | --- | --- | --- | --- | --- |
| Alone | `FullyQualifiedName~Never_materialized_segment_is_materializing` | none | 20 | 20 | 0 |
| Class under load | `FullyQualifiedName~Analysis.Evidence` (78 tests per run) | 4 busy loops (`sh -c 'while :; do :; done'`, one per core; `stress-ng` is not installed) | 5 | 5 | 0 |
| Neighbors | `FullyQualifiedName~Segments` | none | 1 | 1 | 0 |

The class runs under load took 55 s to 57 s. The `Segments` run passed 103 of 103 tests.

SC-002 probes, each temporary and removed afterward:

| Probe | Where | Outcome |
| --- | --- | --- |
| Worker enabled through `extraSettings` (`Analysis:Workers:Enabled=true`, `Segments:Materialization:PollInterval` 50 ms) | `EvidencePartStateTests.StartAsync` | Still reproduces: the target test failed in 10 of 10 runs (second resolution not `available`) |
| Default scenario, workers off, no probe | none | Passed 20 of 20 runs (the alone set above) |
| Demand recording broken (the demand insert and the demand update of `SegmentStore.DemandAsync` write `demanded = false`; the insert alone was masked by the update) | `SegmentStore.cs` | The test failed on its unchanged assertion `count(*) ... WHERE demanded` equals 1 (`EvidencePartStateTests.cs` line 117) |
| `available` reported before materialization (`ResolveEvidenceMedia.Preparing` returns `Available` for a never-purged record) | `ResolveEvidenceMedia.cs` | The test failed on its first resolution before reaching its state assertion (`EvidencePartStateTests.cs` line 112, in `ResolveAsync`) |

Every probe is removed: `git status` and `git diff` show no change under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/` or the platform code beyond this section, and no assertion is removed.

## T009

Post-fix runs on the built tree of the fix of T005 (`Analysis:Workers:Enabled` is `false` in `EvidenceScenario.Settings`), Linux, 4 cores, `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build`, run after the removal of the probes of T008.

| Filter | Load | Runs | Passed | Failed |
| --- | --- | --- | --- | --- |
| `FullyQualifiedName~Never_materialized_segment_is_materializing` | none | 5 | 5 | 0 |
| `FullyQualifiedName~Analysis.Evidence` (78 tests) | none | 1 | 1 | 0 |

Probe results: the probes are those of `## T008` and are not repeated here. With the workers on through `extraSettings`, the target test still failed in 10 of 10 runs; with demand recording broken it failed on its unchanged count assertion; with `available` reported before materialization it failed on its first resolution. The probe-free default scenario passes, and `git status` shows no probe and no change under the evidence folder or the platform code beyond this record.

## T010

Scope check against the merge base with `main` (`85511d0a`), taken before the `## T010` and `## T011` sections were written, excluding this evidence record (the expected only evidence record) from the comparison: `git diff --stat 85511d0a -- . ':!src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md'`.

```text
 specs/archive/20261028-005033-deterministic-evidence-part-state-test/plan.md        |  6 ++++--
 specs/archive/20261028-005033-deterministic-evidence-part-state-test/quickstart.md  |  2 +-
 specs/archive/20261028-005033-deterministic-evidence-part-state-test/research.md    |  6 +++---
 specs/archive/20261028-005033-deterministic-evidence-part-state-test/spec.md        |  6 +++---
 specs/archive/20261028-005033-deterministic-evidence-part-state-test/tasks.md       | 23 +++++++++++-----------
 .../Analysis/Evidence/Support/EvidenceScenario.cs                           |  4 ++++
 6 files changed, 27 insertions(+), 20 deletions(-)
```

Result: the check passed.

- No probe remains: the only added test-code lines are the four of `EvidenceScenario.cs` (a three-line comment naming the evidenced cause and `values["Analysis:Workers:Enabled"] = "false";`), and no `Probe_worker_iteration_between_resolution_steps` or `RunWorkerIterationAsync` exists in the sources.
- No assertion is textually removed or loosened: the diff of `src/platform/Tests/` outside this record has no removed line and no changed assertion.
- Scope: only the file of the evidenced cause (`EvidenceScenario.cs`), this evidence record, and the spec folder `specs/archive/20261028-005033-deterministic-evidence-part-state-test/` changed; no platform file changed (`## T006`).
- No `docs/architecture/` change: `git diff --stat 85511d0a -- docs` is empty (SC-003).

## T011

Command: `node .github/scripts/check-markdown.mjs --files specs/archive/20261028-005033-deterministic-evidence-part-state-test/tasks.md src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md`

Result: exit code 0 with no finding. The script reads the first argument after `--files` as a list file, so this literal form reads `tasks.md` as that list and prints `No changed Markdown files to check.`

Check of both files: the same two paths in a newline-separated list file, `node .github/scripts/check-markdown.mjs --files <list>`, checked 2 Markdown files and reported `Summary: 0 issues in 0 files` (exit code 0), so no finding needed a fix.
