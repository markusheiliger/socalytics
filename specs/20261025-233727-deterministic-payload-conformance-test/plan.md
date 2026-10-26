# Implementation Plan: Deterministic Published Message Conformance Test

**Branch**: `20261025-233727-deterministic-payload-conformance-test` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261025-233727-deterministic-payload-conformance-test/spec.md`

## Summary

`PayloadConformanceTests.PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity` passes only on its rerun in some verifications. The test waits until the first `analysis.jobs.ready.*` message is visible, by polling with real delays while advancing the fake clock, then reads the outbox id snapshot and the two streams one after the other and asserts on whatever it saw. The implementation reproduces the failure (or widens the race window test-side), proves or rules out each suspected cause, and removes the evidenced cause with a wait on an observable condition (every outbox message of the run is published, and every ready job of the run has its ready-work message) and a consistent read of both streams, without weakening any assertion and without retries, fixed sleeps, or longer timeouts. Record the implementation evidence in [`PayloadConformanceEvidence.md`](../../src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PayloadConformanceEvidence.md).

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly, Npgsql, NATS JetStream client (all existing). No new dependency.

**Storage**: PostgreSQL (`socalytics.outbox_messages`, `analysis_logical_jobs`, `analysis_readiness_occurrences`), NATS JetStream streams `SOCALYTICS_ANALYSIS_JOBS` and `SOCALYTICS_EVENTS`, RustFS, all in disposable test containers.

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker-backed), the class run repeatedly alone and under CPU load.

**Target Platform**: Linux and developer machines with a Docker-compatible container runtime.

**Project Type**: Test change in the platform monolith's test project; production code only if the evidence puts the cause there.

**Performance Goals**: None.

**Constraints**: No assertion weakened, removed, or skipped; no retry, fixed sleep, or longer timeout as the fix; the only time limit is the test's cancellation token or an explicit `[Fact(Timeout = ...)]`; no change to outbox publication, message contracts, subjects, streams, run creation, or readiness behavior.

**Scale/Scope**: One test, its helpers in `PayloadConformanceTests.cs`, the evidence file `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PayloadConformanceEvidence.md`, and the code the evidence points to.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform` and this spec folder; no new `src` child. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | The cause is evidenced by repeated runs, a deliberate race widening, and probes that must still fail the test. | Pass |
| V. Focused, minimal changes | The expected changes are in one test file and its evidence file; no package or abstraction. | Pass |
| Environment features | Needs only the existing .NET SDK and Docker. No environment spec. | Pass |

Post-design re-check: Pass.

## Architecture Updates

None. The fix removes a race in test code and changes no architecture narrative, design decision, or status statement in `docs/architecture/`. If the evidence puts the cause in the outbox publisher or readiness, the fix keeps the documented publication, ordering, and readiness behavior, and the implementation re-checks this section before merge.

## Design

1. **Establish the cause** ([research.md](research.md)). Run the test at least 30 times alone and 10 times under CPU load (one busy loop per core). For each suspected cause run a demonstration against the old test: delay the publication of one message of the run test-side (for example by making one outbox row not due until the clock advances), reorder or stagger the stream reads, and inspect the shared `NatsFaultInjection` stream state. Record which cause reproduces the failure and which are ruled out in [`PayloadConformanceEvidence.md`](../../src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PayloadConformanceEvidence.md).
2. **Wait on the observable condition.** Replace `WaitForReadyWorkAsync` with a wait that evaluates, from the database, that the run exists, that the run's set of expected ready-work messages is complete, and that no outbox row of the run's message set is `pending` (a `failed` or `discarded` row fails the test at once, naming the row and its `failure_category`). The run's message set is the outbox rows selected by `run_id` (nullable, migration 0010, the primary attribution key) = the run's id with `message_type` in (`analysis.ready-work`, `analysis.run-state-changed`), with the fallback join through `socalytics.analysis_logical_jobs` kept only if T005 finds ready-work or run-state rows with a null `run_id`; other outbox rows are ignored, and a `discarded` row fails only because this test's single run has no supersession or cancellation. The expected set is complete when every node of the run's pinned snapshot has its logical job row (so the wait cannot return before the segment-scoped node's job has been created by the readiness worker or the Segment Service) and every logical job is settled. A job counts as settled only when it is Ready with its published `analysis.ready-work` row, or it waits on an upstream node result of the same run (a dependency on another node's accepted result). A job that waits on segment materialization (or whose segment association is not yet pinned) is never settled: the wait continues until that job is Ready and published (or the failure bound ends it with the pending rows named), and it does not exit while the run's segment-scoped node has no Ready job. The wait still advances the single fake clock to drive the publisher, but its exit is that condition; a fixed delay between checks is only the pace of the poll and never the success condition. The wait observes a cancellation token linked to the test token and the fact timeout (the hang guard stays a limit, not the success condition); on cancellation it fails with a message listing every row of the run's message set still `pending` (id, `message_type`, `subject`, `attempt_count`, `failure_category`, `next_attempt_at`). Both facts of the class carry an explicit `[Fact(Timeout = ...)]`, with an initial value derived from the T002 baseline of the unchanged class (at least ten times the observed maximum, re-checked against the fixed test by T010 and adjusted if it is no longer at least ten times the maximum) and documented as a hang guard only; the test's cancellation token is the only other limit.
3. **Read one consistent snapshot.** After the condition holds, take the outbox id set once, then read both streams; every id of the set is published, so the stream contents for the run are final, and the test checks that each id of the set was found in the streams (a message published more than once is accepted).
4. **Isolate shared state** only if the evidence shows it: messages of other tests are already ignored by outbox id; clear stale stream or consumer state the way `WorkflowAvailabilityTests` does only where proven.
5. **Keep every assertion** of both tests of the class unchanged; `CompletionBodiesAreValidatedAndStaleOnesAreRejectedByAcceptance` uses the same wait and keeps its first-job semantics.
6. **Prove it.** At least 30 solo and 10 loaded consecutive runs of the class pass; the three probes (invalid job payload, missing segment identity field, segment on a non-segment job) each fail the named test and are removed; the deliberate-delay reproduction fails the old test and passes the fixed one.
7. **Record the cause** in a code comment next to the fix and in [`PayloadConformanceEvidence.md`](../../src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PayloadConformanceEvidence.md).

## Project Structure

### Documentation (this feature)

```text
specs/20261025-233727-deterministic-payload-conformance-test/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── spec.md
├── tasks.md              # /speckit-tasks, not created by /speckit-plan
└── checklists/
    └── requirements.md
```

No `contracts/`: the feature exposes no interface and changes no contract.

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/
└── Analysis/
    ├── PayloadConformanceTests.cs           # wait, snapshot, evidence comment
    └── PayloadConformanceEvidence.md        # implementation evidence
```

**Structure Decision**: `PayloadConformanceTests.cs` and `PayloadConformanceEvidence.md` are the expected changes. `Analysis/Support/` or `Recordings/Support/` change only if the evidence puts the cause in a shared helper, and then every test using it is rerun. Production code under `src/platform/SocAlytics.Platform.Application/Messaging/` or `src/platform/SocAlytics.Platform.Infrastructure/Workers/` changes only where the evidence puts the cause there.

## Complexity Tracking

No violations.
