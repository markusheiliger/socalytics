# Payload Conformance Evidence

Passing and failing results in this file are development evidence only: they were produced on one 4-core development machine with Docker, they are not a guarantee of determinism on other environments, and they are not production-readiness evidence. The detailed entries per task follow in [Evidence](#evidence).

## Summary

### Counts

| Run | Before the change (T001, T002) | After the change (T010) |
| --- | --- | --- |
| Baseline, single run of the class | 2 passed of 2 | - |
| Repeated, alone | 30 runs, 30 passed, 0 failed | 30 runs, 30 passed, 0 failed |
| Repeated, under CPU load (one busy loop per core) | 10 runs, 10 passed, 0 failed | 10 runs, 10 passed, 0 failed |
| Maximum test duration | 18 s (loaded) | 17 s (loaded) |

The original intermittent failure did not reproduce in the 40 runs before the change, so the cause is established by deliberately widening the race window test-side (T003, T004) and by inspecting the code (T005), not by reproducing the failure. The hang guard stays 180 s (at least ten times the maximum observed duration); it bounds a stuck run and is never a success condition.

### Suspected causes

| Suspected cause | Result | Evidence |
| --- | --- | --- |
| Premature read | Proven (test-side) | T003: with later ready-work rows delayed, the old wait accepted the first visible row as the complete set (variant 1 passed without reading the delayed rows), and with the segment rows delayed it failed with `No ready-work message was published.` (variant 2). T011: the fixed wait passes under the same widening. |
| Wait on elapsed time | Proven (test-side) | T003 variant 2: the old outcome depended on 360 fake-clock advances with 500 ms delays and on publication order, not on the publication state of the run. T013: the fixed wait fails by its own named limit when a row is never published. |
| Staggered stream reads | Ruled out as a demonstrated cause | T004: with 20 fake-clock advances before each of the two stream reads, both tests passed and every id of the snapshot was found; the read is filtered by one fixed outbox id set, so a message published between the snapshot and a read is neither missed nor split. The set is only as complete as the moment of the snapshot, which is the premature-read cause. The one id snapshot read of both streams remains as a consistency measure. |
| Shared state | Ruled out as a cause of wrong assertions; not evidenced as a failure cause | T005: every test has its own database and outbox; leftover messages of other tests in the shared NATS streams are filtered out by outbox id. A shared durable consumer can only slow the finalization notification, which is not proven as a failure cause, so no isolation was added. T010: the two tests pass in either order, and a failure in one test does not affect the other. |
| Covered production code | Ruled out | T005: the outbox publisher claims and publishes rows in creation order within a batch, the run's rows commit atomically with the run, and the run's messages are single rows with one `Nats-Msg-Id` each; no production code changed (T014: no shared helper changed either). |

### Widening before and after

- Before (T003): delaying the pending ready-work rows of the run, the old wait either accepted a partial set (variant 1, passed after reading one message) or timed out on the first condition that depended on publication order (variant 2, failed after 3 m 8 s).
- After (T011): the same delay applied to the fixed test, which passed with 2 of 2 tests in 8 s because it returns only once the run's publication state is complete.

### Probes that must still fail (T012)

With the fixed test, each of three temporary test-side probes made it fail on the intended assertion without a hang: an invalid job payload (`teamId` as a number; schema assertion), a missing segment identity field (`segment.timelineMappingDigest`; segment-identity assertion), and a segment on a non-segment job (no-segment assertion). All probe edits were reverted.

### Unpublished and unpublishable rows (T013)

A `failed` row of the run's message set fails the test at once, naming the row and its failure category. A `pending` row that is not due fails through the wait's own limit (150 s, inside the 180 s hang guard) and names id, `message_type`, `subject`, `attempt_count`, `failure_category`, and `next_attempt_at`. Neither case asserts on a partial set or hangs.

### Findings and decisions of the spec artifacts that the evidence contradicts

The implementation changes no spec artifact; the following statements are recorded here instead.

- `research.md` already records the staggered-read finding; `spec.md` lists staggered stream reads as a suspected cause that the implementation evaluates, and the evidence rules it out as a demonstrated cause (T004).
- The spec's input and research.md state that the first-visible set can lack the segment job so that an assertion fails. T003 shows the old test failing at the wait with a `TimeoutException`, before the `jobs.ShouldContain(... "segment")` assertion was reached, and passing with a partial set when only later rows were delayed; the demonstrated failure mode of the premature read is therefore a wait outcome that depends on elapsed time and publication order, not a segment assertion that was shown to fail.
- The spec describes the failure as three observed intermittent failures; none reproduced in 40 runs here, so the observed failures are explained by the widening demonstrations only.
- research.md says leftover stream state can only slow the scheduler's finalization consumer; T005 treats even that as not proven as a failure cause.

### Scope

Only `PayloadConformanceTests.cs` and this file changed in the test project; no production code and no shared helper changed (T014), so no architecture statement changed.

## Evidence

### T001 Baseline

- Docker: available (`docker info` succeeded).
- `dotnet restore src/platform/SocAlytics.Platform.slnx`: succeeded.
- `dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests`: succeeded, 0 warnings, 0 errors (26 s).
- `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~PayloadConformanceTests"`: Passed 2, Failed 0, Skipped 0, Total 2; test duration 8 s (wall clock 35 s including startup).

### T002 Repeated runs

Unchanged class, `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~PayloadConformanceTests"`, Docker available, 4 cores.

- Alone: 30 runs, 30 passed (2 of 2 tests each), 0 failed, 0 skipped; test duration 9 s (28 runs) or 10 s (2 runs); wall clock 19-20 s per run. No failing assertion lines.
- Under CPU load (one busy-loop process per core, 4 processes, load average rose to about 11): 10 runs, 10 passed, 0 failed, 0 skipped; test duration 15-18 s; wall clock 32-34 s per run. No failing assertion lines. The load processes were stopped afterward.
- Result: the failure did not reproduce in 40 runs in this environment. The observed maximum test duration is 18 s (loaded), the basis for the hang-guard timeout of T006 (at least ten times that, 180 s).

### T003 Premature read

Temporary test-side edit of `StartAsync` in `PayloadConformanceTests.cs` (reverted completely; `git status` shows no change to the file): after finalization a background task repeatedly set `next_attempt_at` to the fake clock plus two hours on the pending `analysis.ready-work` outbox rows (`socalytics.outbox_messages`, no publisher change), so those rows were not due. Command: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~PayloadConformanceTests.PublishedMessagesConform"`.

- Variant 1, all ready-work rows except the first by `created_at, id` delayed: Passed 1 (6 s). The wait returned once one ready-work message existed, so it accepts whatever is published at that moment as the complete set; the delayed rows were never read or asserted on.
- Variant 2, only rows whose payload `executionScope` is `segment` delayed: Failed 1 (3 m 8 s) with `System.TimeoutException : No ready-work message was published.` at `WaitForReadyWorkAsync` (line 151). The wait has no condition on the publication state of the run: it succeeds when any ready-work message happens to be visible and otherwise gives up after 360 iterations of fake-clock advances and 500 ms delays, so the outcome depends on the elapsed time and on publication order. The `jobs.ShouldContain(... "segment")` assertion was not reached.
- Result: the premature read and the wait on elapsed time are demonstrated as test-side causes; the wait returns on the first ready-work message, not on a complete set of the run's messages.

### T004 Staggered reads

Temporary test-side edit of `ReadMessagesAsync` in `PayloadConformanceTests.cs` (reverted completely with `git checkout`; `git status` shows no change to the file): after the outbox id snapshot and before each of the two stream reads (`SOCALYTICS_ANALYSIS_JOBS`, then `SOCALYTICS_EVENTS`), 20 fake-clock advances of 3 s with a 250 ms delay let the publisher run, and each call logged the snapshot size and the number of messages read. Command: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~PayloadConformanceTests"`.

- Result: Passed 2, Failed 0 (47 s). Each test's first read returned snapshot=1, read=1, ready=0, events=0; its later read returned snapshot=3, read=3, ready=1, events=1.
- Interpretation: messages published between the snapshot and a stream read are not missed or split, because every outbox id of the snapshot was found in a stream and the read is filtered by that fixed id set. Rows created after the snapshot are simply absent from it, so a read is a consistent view of the snapshot only. The old test is therefore not shown to split messages through staggered reads; the staggered reads are ruled out as a demonstrated cause. The set the old read takes is only as complete as the moment of the snapshot, which is the premature-read cause of T003, and is why T007 reads both streams against one set taken after the run's publication state is complete.

### T005 Shared state and production code

Read-only inspection; no test or production file changed by this task.

#### Database and NATS provisioning

- `FinalizationSetup.StartAsync` calls `postgres.CreateDatabaseAsync` and runs the Migrator, so every test has its own PostgreSQL database and its own outbox: database state is not shared between the two facts. `RustFsContainerFixture` is shared, but the test's result objects use a per-test key prefix and the object store is not read by the wait.
- `NatsContainerFixture` is an assembly fixture: one NATS container (`-js`) for the whole run, shared by the `NatsFaultInjection` collection. The test sets `Messaging:Nats:ProvisionTopology=true`; `NatsTopology.EnsureAsync` runs `CreateOrUpdateStreamAsync` for `SOCALYTICS_EVENTS` and `SOCALYTICS_ANALYSIS_JOBS` (work queue) and `CreateOrUpdateConsumerAsync` for the durable consumer `analysis-scheduler-recordings-finalized` (filter `matches.recordings-finalized`, explicit ack). Streams and the durable consumer are therefore shared across tests and keep messages of earlier tests' databases. Only `WorkflowAvailabilityTests` deletes `SOCALYTICS_EVENTS` (`ResetEventsStreamAsync`) to isolate itself.
- Ruling: leftover stream messages cannot enter this test's assertions, because `ReadStreamAsync` keeps only messages whose `Nats-Msg-Id` is an outbox id of this test's database; ruled out as a cause of wrong assertions. The shared durable consumer can only delay this test's own finalization notification when earlier tests left notifications for recording sets that do not exist in this database; that delay is not evidenced as a failure here (40 runs passed in T002), so no stream or consumer isolation is added. It is a source of elapsed-time variation that the condition-based wait of T006 (the run exists, no wait on elapsed time) covers; not proven as a cause.

#### Outbox publisher and readiness publishing

- `OutboxPublisherWorker` polls every `PollIntervalSeconds` (1) on the single fake clock and runs `PublishOutboxBatch.HandleAsync` in one unit of work: `ClaimDueAsync` takes `state = 'pending'` rows with `next_attempt_at` null or due, `ORDER BY created_at, id`, `LIMIT BatchSize` (50), `FOR UPDATE SKIP LOCKED`, publishes each row one by one, and marks it `published` or records a failed attempt with a retry delay. A published row is only visible after the batch commits, so `published` rows appear batch-wise; a transport failure keeps the row `pending` with a later `next_attempt_at`, which is why a wait must advance the fake clock and observe `pending` rows. Publication order is creation order within a batch, not guaranteed across batches. The run's rows (a handful) fit in one batch. Ruled out as a cause: no multi-step publishing of a run exists, each row is one message with one `Nats-Msg-Id` (the outbox id, followed by `.r<round>` for `publication_round` above 1).
- `RunReadinessCoordinator.ApplyAsync` is called inside the unit of work that creates the run (`HandleRecordingsFinalizedNotification`, `coordinator.ApplyAsync` after `jobs.InsertAsync`), so the run, its nodes, its logical jobs, the first readiness occurrences, and the outbox rows (`analysis.run-state-changed` for `Requested` to `Running`, then one `analysis.ready-work` per job that is ready) commit atomically; later `analysis.ready-work` rows come from `CompleteAttempt`, `MaterializeSegment`, and `RequeueOrFailAsync`. The set of ready-work rows of a run is therefore complete in the database from the run's creation for jobs without an unsatisfied dependency; the publication of those rows by the publisher is what the test waits on, and publication is the only asynchronous step. The old wait observed none of this state. Ruled out: production code defects in publication order, batching, or multi-step publishing. The cause remains test-side (T003, T004).

#### Null `run_id`

- Every `OutboxMessageDraft` of `analysis.ready-work` (`RunReadinessCoordinator`, both enqueue sites, `run.Id` or `hierarchy.Run.Id`) and of `analysis.run-state-changed` (`EnqueueRunStateAsync`, `run.Id`) sets `RunId`, and `Outbox.EnqueueAsync` writes it to `run_id` (migration 0010, nullable). Only `matches.recordings-finalized` rows (and agent-orchestration rows) have a null `run_id`, and they are not in the run's message set. No `analysis.ready-work` or `analysis.run-state-changed` row has a null `run_id`: `run_id` is the sole attribution key of T006, and the join through `socalytics.analysis_logical_jobs` is not needed.

#### Concrete names for T006

- Run and nodes: `socalytics.analysis_runs` (`id`, `state` `Requested`/`Running`/...); the run's nodes are the rows of `socalytics.analysis_workflow_nodes` with `run_id` = the run id (`id`, `node_key`, `execution_scope` `segment`/`match`, `state` `Waiting`/`Active`/`Completed`/`Failed`/`Blocked`); the pinned snapshot is `analysis_node_snapshots` (`node_id`, `snapshot` jsonb with `videoPolicy`) and `analysis_run_snapshots` (`run_id`, `snapshot`).
- Logical jobs: `socalytics.analysis_logical_jobs` (`id`, `run_id`, `node_id`, `execution_scope`, `segment_number`, `state` `Waiting`/`Ready`/`Claimed`/`Completed`/`Failed`/`Blocked`, `current_readiness_occurrence_id`, `segment_rejection_reason`). The run's expected jobs exist when every node of the run has at least one logical job row with `node_id` = the node's `id` (jobs are inserted together with the run, before readiness runs).
- Segment-scoped node: the node whose `analysis_workflow_nodes.execution_scope = 'segment'` (development workflow: `dev-segment-node`, `VideoPolicy` `forbidden`, so it waits for no segment media); its jobs have `analysis_logical_jobs.execution_scope = 'segment'` and a non-null `segment_number`.
- Ready job: `analysis_logical_jobs.state = 'Ready'` (or later, `Claimed`/`Completed`) with an `analysis_readiness_occurrences` row (`logical_job_id`, `sequence`, `cause`) and its `analysis.ready-work` outbox row (`run_id`, `logical_job_id`, `causation_id` = the occurrence id). A Ready job is settled only when that outbox row is `published`.
- Waiting on an upstream result: `analysis_logical_jobs.state = 'Waiting'` whose node is the `dependent_node_id` of a row in `socalytics.analysis_dependency_edges` (`run_id`, `upstream_node_id`, `dependent_node_id`, `kind`; development workflow: `dev-match-node` and `dev-partial-match-node` depend on `dev-segment-node`, kind `required`) and that has no row yet in `socalytics.analysis_accepted_results` for the upstream node's jobs (`run_id`, `node_id`, `logical_job_id`). This job is settled while waiting.
- Waiting on segment materialization: `state = 'Waiting'` on a `segment` job whose node snapshot `videoPolicy` is `required` or `optional` and whose `analysis_logical_job_inputs.segment_media` row is not yet pinned (written only when the job becomes Ready; `segment_rejection_reason` is set when the request was rejected, `materialized_segments` state `Absent`/`Materializing`/`Available`/`Failed`). Never settled.
- Not yet evaluated: `state = 'Waiting'` with no incoming `analysis_dependency_edges` row for its node and no segment-media wait (for example a `Requested` run before `ApplyAsync` or a job whose readiness has not run); no `analysis_readiness_occurrences` row exists for it. Unsettled.
- Messages and states: `socalytics.outbox_messages` (`id`, `message_type`, `subject`, `run_id`, `logical_job_id`, `state` `pending`/`published`/`failed`/`discarded`, `attempt_count`, `failure_category`, `next_attempt_at`, `publication_round`); the run's message set is `run_id` = the run id and `message_type` in (`analysis.ready-work`, `analysis.run-state-changed`). `discarded` arises from `DiscardUnpublishedReadyWorkAsync` (a Blocked job) and from supersession or cancellation.

#### T005 result

Shared state is ruled out as a cause of wrong assertions and not evidenced as a failure cause (no isolation added); the publisher and readiness publishing are ruled out as causes (no production change); no ready-work or run-state row of a run has a null `run_id`.

### T010 Repeated runs of the fixed class

Fixed class, `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~PayloadConformanceTests"`, Docker available, 4 cores. Passing results are development evidence only.

- Alone: 30 consecutive runs, 30 passed (2 of 2 tests each), 0 failed, 0 skipped; test duration 7 s (8 runs) or 8 s (22 runs).
- Under CPU load (one busy-loop process per core, 4 processes): 10 consecutive runs, 10 passed, 0 failed, 0 skipped; test duration 12-17 s; wall clock 26-32 s per run. The load processes were stopped afterward.
- Hang guard: `HangGuardMilliseconds` stays 180 000 ms (180 s). The maximum duration observed on the fixed class is 17 s (loaded), so ten times is 170 s and the value is still at least ten times the maximum; no adjustment. It is a hang guard only.

#### Test order

Separate invocations with `--filter "FullyQualifiedName~PayloadConformanceTests.<test>"`, each run in a new process after the other:

- `CompletionBodiesAreValidatedAndStaleOnesAreRejectedByAcceptance` alone, then `PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity` alone: Passed 1 of 1 and Passed 1 of 1 (8 s each).
- `PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity` alone, then `CompletionBodiesAreValidatedAndStaleOnesAreRejectedByAcceptance` alone: Passed 1 of 1 and Passed 1 of 1 (8 s each).

#### Failure mid-test followed by the other test

A temporary edit made `PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity` throw `InvalidOperationException` right after the wait (an unconditional `throw` is a compile error in this project, so it was guarded by `messages.Count > 0`). The class run reported Failed 1, Passed 1 of 2: the edited test failed with `temporary mid-test failure` and `CompletionBodiesAreValidatedAndStaleOnesAreRejectedByAcceptance` still passed. The edit was reverted and `git status` / `git diff` show no change to `PayloadConformanceTests.cs`.

### T011 Widening against the fixed test

Temporary test-side edit of `StartAsync` in `PayloadConformanceTests.cs` (reverted completely with `git checkout`; no `TEMP-T011` marker and no change to the file remain): the same widening as T003 variant 1. After finalization a background task repeatedly set `next_attempt_at` to the fake clock plus two minutes on every pending `analysis.ready-work` outbox row of `socalytics.outbox_messages` except the first by `created_at, id` (no publisher change). Command: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~PayloadConformanceTests"`.

- Effect check: a second run of the same edit logged the delay it applied; it delayed 2 pending ready-work rows in one logged update (the other test of the class logged none), and the class again passed.
- Result: Passed 2, Failed 0, Skipped 0, Total 2 (8 s). The fixed wait returns only once the run's publication state is complete, so it is not affected by delayed publication of the later ready-work rows; under the same widening the old wait accepted a partial set (T003).

### T012 Probes that must still fail the test (SC-003)

Three temporary test-side probes in `PayloadConformanceTests.cs`, applied one after the other to the published payloads (the helper and edits marked `TEMP-T012`), each run with `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~PayloadConformanceTests.PublishedMessages"` and reverted with `git checkout` afterward. Passing and failing results are development evidence only.

- Invalid job payload (`teamId` set to the number 123 on every ready-work payload): Failed 1 of 1 in 5 s on the schema assertion, `analysis.jobs.ready.dev-segment-capability: /teamId:type, ...`. No hang.
- Missing segment identity field (`segment.timelineMappingDigest` removed from every segment job): Failed 1 of 1 in 4 s on the segment-identity assertion, `timelineMappingDigest`. No hang. (A first attempt whose edit did not reach the file passed unchanged; it was discarded and the probe reapplied and confirmed with `git diff --stat` before the run.)
- Segment on a non-segment job (a copy of the first job with `executionScope` set to `match` while keeping `segment`): Failed 1 of 1 in 5 s on the no-segment assertion, `job.TryGetProperty("segment", out _)`. No hang.

After the last probe `git status --short` shows no change to `PayloadConformanceTests.cs`; every probe change is gone.

### T013 Unpublishable and unpublished rows (FR-004)

Temporary test-side probe in `StartAsync` of `PayloadConformanceTests.cs` (marked `TEMP-T013`, reverted completely with `git checkout`): after finalization a background task repeatedly updated the first `analysis.ready-work` outbox row of the run (by `created_at, id`) in `socalytics.outbox_messages`. Each probe ran `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~PayloadConformanceTests.PublishedMessages"`. Passing and failing results are development evidence only.

- Failed row (`state = 'failed'`, `failure_category = 'publish-rejected'`; the check constraint `ck_outbox_messages_failure_category` allows only `transport-unavailable`, `publish-timeout`, `publish-rejected`, so a first attempt with another value was rejected by the database and had no effect and was discarded): Failed 1 of 1 in 3 s, no hang, no assertion on a partial set: `System.InvalidOperationException : Outbox rows of the run's message set failed or were discarded: id=01a18e36-d48d-7033-ade4-3872e95fa412 message_type=analysis.ready-work subject=analysis.jobs.ready.dev-segment-capability state=failed attempt_count=0 failure_category=publish-rejected next_attempt_at=-`. It names the row and its last failure category.
- Pending row not due (`state = 'pending'`, `next_attempt_at` 30 days ahead): Failed 1 of 1 after 2 m 32 s (the wait's own limit, `WaitLimit` of 150 s, inside the 180 s hang guard) through the wait's own failure, not the xUnit timeout: `System.TimeoutException : The run's publication state was not complete before the hang guard. Pending rows: id=01a18e37-3792-7a44-b72b-0a3f45d011b9 message_type=analysis.ready-work subject=analysis.jobs.ready.dev-segment-capability state=pending attempt_count=0 failure_category=- next_attempt_at=2026-11-29T19:11:34.9751720Z`. It names id, `message_type`, `subject`, `attempt_count`, `failure_category`, and `next_attempt_at`.

After the probes `git checkout` restored `PayloadConformanceTests.cs`; `git status --short` shows no change to it and no `TEMP-T013` marker remains.

### T014 Shared helpers (FR-005)

No shared helper changed. `git diff --stat 9bd4d0fe HEAD -- Analysis/Support Recordings/Support` (`9bd4d0fe` is the merge base with the default branch) prints nothing, and `git status --short` shows no uncommitted change under either folder. The only test-project files this implementation changed are `Analysis/PayloadConformanceTests.cs` and this file, and T005 changed no file. Therefore no other integration test uses a changed helper, and no further test run is needed for this task.
