# Research: Deterministic Published Message Conformance Test

## Findings from reading the code

- `PayloadConformanceTests.WaitForReadyWorkAsync` loops up to 360 times: read all messages, return as soon as any `analysis.jobs.ready.*` message exists, otherwise advance the fake clock by 3 s and `Task.Delay(500)`. The exit condition is "the first ready-work message is visible", not "the run's messages are published".
- `ReadMessagesAsync` takes the id snapshot of every row in `socalytics.outbox_messages` first, then reads `SOCALYTICS_ANALYSIS_JOBS`, then `SOCALYTICS_EVENTS`, each over its own NATS connection, keeping messages whose `Nats-Msg-Id` is in the snapshot. Because the read is filtered by that fixed id set, a row published between the snapshot and a stream read is not missed or split (T004 evidence); a row inserted after the snapshot is simply absent from the snapshot, so the read is only as complete as the moment of the snapshot.
- `PublishOutboxBatch` claims due rows in batches, publishes each, and marks it published in the same unit of work, so rows of one run become visible in NATS one after the other across batches and polls; the worker's poll delay runs on `TimeProvider`, so the fake clock the test advances drives it.
- The test asserts at least one `segment` execution scope job and the segment identity of every segment job; a run publishes several ready-work messages and run-state events, so the first-visible set can lack the segment job.
- The class and its NATS container are shared with other `NatsFaultInjection` tests (stream state, durable consumers); `WorkflowAvailabilityTests` documents leftover `SOCALYTICS_EVENTS` state as a proven cause of another flaky test. This test filters by outbox id, so leftovers cannot enter its assertions; they can only slow the scheduler's finalization consumer.

## Decision

- **Decision**: Wait on the database-observable publication state of the run (the `run_id`-based message set of the run, every node of the pinned snapshot has a settled logical job, no row `pending`, a `failed` or `discarded` row fails at once with its category, and on cancellation a failure lists the `pending` rows), then read one id snapshot and both streams.
- **Rationale**: It is the observable condition the assertions depend on; it removes the premature read (the snapshot is taken only once the run's publication is complete) without a sleep or a longer limit, and it fails with the unpublished rows named (the same condition as plan.md Design step 2 and data-model.md) instead of a generic timeout.
- **Alternatives considered**: Waiting for a fixed count of messages (couples the test to run shape and hides a missing message); a longer loop or retry (rejected by the spec); purging streams before the test (only if the shared-state hypothesis is proven); changing the publisher to publish a run atomically (changes production behavior, rejected by FR-006).

## Evidence

Implementation evidence is recorded in [`PayloadConformanceEvidence.md`](../../src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PayloadConformanceEvidence.md). T001 to T005 append their results under the corresponding task headings there; T015 consolidates those entries with the later verification evidence (FR-001). Staggered stream reads were ruled out as a demonstrated cause by T004; the evidence file records that finding, and the one id snapshot read of both streams remains as a consistency measure.
