# Feature Specification: Deterministic Published Message Conformance Test

**Feature Branch**: `20261025-233727-deterministic-payload-conformance-test`

**Created**: 2026-10-25

**Status**: Draft

**Fix for**: #314 (implementation of `specs/20261023-074600-analysis-fact-timeline`)

**Input**: User description: "Make the Docker-backed integration test `SocAlytics.Platform.Integration.Tests.Analysis.PayloadConformanceTests.PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity` deterministic. It lives in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PayloadConformanceTests.cs` (with the support in `Analysis/Support/` and `Recordings/Support/`) and covers the outbox publisher, the NATS JetStream publication of the analysis messages (`analysis.jobs.ready.*` on `SOCALYTICS_ANALYSIS_JOBS` and `analysis.events.run-state-changed` on `SOCALYTICS_EVENTS`), their contract schemas, and the scope identity of the analyst jobs, owned by the durable analysis workflow feature. It failed and then passed on its one rerun in the verifications of implementation pull requests #184, #186, and #314, so it is flaky, and the flakiness is not caused by the stopped spec `specs/20261023-074600-analysis-fact-timeline`. Suspected causes to investigate and prove or rule out: the wait for ready work returns as soon as the first `analysis.jobs.ready.*` message is visible, polling with real delays while advancing the fake clock, although the test then asserts that a segment-scoped job exists and that every job carries its scope identity, so the assertions can run before all expected messages of the run were published; the two streams being read at different moments from the outbox id snapshot; and the shared `NatsFaultInjection` collection and NATS stream state shared with other tests. Find and confirm the actual cause, then remove it in the test or in the code it covers, for example by waiting on the observable condition that all outbox messages of the run are published before reading the streams, while every assertion (schema validity of each published message, at least one segment-scoped job, segment identity fields, no segment on non-segment jobs) stays. Do not weaken, skip, or delete the test or its assertions, and do not add retries, fixed sleeps, or longer timeouts as the fix. Other tests of `PayloadConformanceTests` that share a changed helper must stay passing and deterministic; other flaky tests are out of scope. The spec `specs/20261023-074600-analysis-fact-timeline` (implementation pull request #314) depends on this spec."

## Clarifications

### Session 2026-10-25

- Q: May the fix change production code if the cause lives there? → A: Yes, but only where the evidence puts the cause, and without changing the documented behavior of the outbox publisher, the NATS publication, the message contracts, or run creation and readiness. Causes in the test or its support are fixed there.
- Q: Are retries, fixed sleeps, or longer timeouts acceptable? → A: No. Every wait MUST be on an observable condition (such as the publication state of the run's outbox messages), and time that the test controls MUST come from one clock.
- Q: Is a fixed polling pace acceptable when waiting on an observable condition? → A: Yes. The pace between checks is not a fix; the wait MUST end only when the observable condition holds (or a failure bound is reached), never after a fixed elapsed time.
- Q: Which tests are in scope? → A: The named test, the other tests of `PayloadConformanceTests`, and any test that uses a shared helper the fix changes; all must stay passing and deterministic. Other flaky tests are out of scope.

## Problem

The test finalizes a recording set, lets the durable analysis workflow create a run and publish its messages through the outbox to NATS JetStream, reads the published messages of the two streams, validates each against its contract schema, and asserts the scope identity of the analyst jobs: at least one segment-scoped job exists, a segment-scoped job carries its segment identity fields, and a job of another scope carries no segment. In the verifications of three implementation pull requests it failed and then passed on its single rerun, so the failure depends on timing and not on the behavior under test.

Suspected causes, to be confirmed or ruled out by evidence before any is fixed:

- **Premature read**: the wait returns as soon as the first ready-work message is visible, although a run publishes several messages (several ready-work messages and run-state events) at different moments. The assertions can then see an incomplete set, for example no segment-scoped job yet.
- **Wait on elapsed time**: the wait polls with real delays while it advances the fake clock, so what it waits for is a guess about elapsed time and not an observable condition.
- **Staggered stream reads**: the outbox id snapshot is taken once per read, and the two streams are read one after the other, so a message of the run could be published between the snapshot and a stream read, or after the first stream was read, and be missed or counted in one stream only. This is a suspected cause that the implementation evaluates (T004); because the read filters by the fixed id snapshot, [research.md](research.md) records it as ruled out as a demonstrated cause.
- **Shared state**: the `NatsFaultInjection` collection and the NATS streams are shared with other tests, so messages or stream state of another test, or fault injection left over from one, can reach this test.
- **Covered production code**: publication ordering or batching in the outbox publisher, or readiness publishing the messages of a run in more than one step, behaves differently under load.

The implementation first reproduces the failure (repeated runs alone and under CPU contention) or demonstrates the suspected race by deliberately widening its window test-side (for example by delaying the publication of one message of the run), proves or rules out each suspected cause, and then removes the cause or causes that the evidence shows.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The conformance test fails only when published messages are wrong (Priority: P1)

A maintainer running the integration tests, or the verification of any implementation pull request, sees the test pass whenever every published analysis message of a finalized run conforms to its schema and the analyst jobs carry their scope identity, however slowly the publisher runs, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, which pass only on a rerun and report a flaky test that none of those features caused; the stopped implementation of #314 needs it reliable.

**Independent Test**: Run the test repeatedly alone and under CPU load, and with the publication of one message of the run deliberately delayed; every run passes. Then break the behavior with probes (for example an invalid job payload, a missing segment identity field, or a segment on a non-segment job) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** a finalized recording set whose run publishes several messages, **When** the publisher publishes them at different moments, **Then** the test evaluates its assertions only after every outbox message of the run is published, and it validates each of them.
2. **Given** the messages of the run are spread over both streams, **When** the test reads them, **Then** the set it asserts on is complete and consistent for both streams, whatever the moment of each read.
3. **Given** the run has not published all of its messages, or a message of the run cannot be published, **When** the test observes it, **Then** it fails on the observed outbox state, naming the unpublished messages and their last recorded error, and does not hang or assert on a partial set.
4. **Given** the test finished or failed in the middle, **When** another test of `PayloadConformanceTests` or of the `NatsFaultInjection` collection runs, **Then** it starts without stream, consumer, or database state left over from this test, and messages of another test never enter this test's assertions.

### Edge Cases

- A message published more than once MUST NOT fail the test while the platform behaves correctly.
- A failure in the middle of the test MUST NOT leave state that affects another test.
- The fix MUST NOT depend on the order in which tests or test classes run.
- A passing test MUST NOT hide an invalid payload, a missing segment-scoped job, a missing segment identity field, or a segment on a non-segment job.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (repeated runs of the test with and without CPU load, and for each suspected cause a demonstration that proves or rules it out, for example a deliberate test-side delay of one message's publication, or reordering of the stream reads, under which the old test fails and the fixed test passes) and record it in a code comment next to the fix and in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PayloadConformanceEvidence.md`.
- **FR-002**: The cause MUST be removed where it lives (the test, its shared support, or the covered production code) by waiting on an observable condition (all outbox messages of the run are published), reading the messages from a consistent snapshot, controlling the racing input, or isolating shared state, not by retrying, skipping, adding a fixed sleep as the success condition, or lengthening a timeout. A fixed delay between checks is acceptable pacing of a poll only while the wait exits on the observable condition, never on elapsed time.
- **FR-003**: Every assertion of the named test (schema validity of each published ready-work and run-state message, at least one segment-scoped job, the segment's recording set version matching its job and the presence of the segment identity fields `recordingVersionId`, `timelineMappingDigest`, `segmentationPolicyDigest`, and `segmentNumber`, and no segment on a job of another scope) MUST remain unchanged, and no assertion, expected value, or step MAY be removed, loosened, or skipped.
- **FR-004**: When the messages of the run are not all published, the test MUST fail on that observation, naming the unpublished messages and their last recorded error, and the wait MUST be bounded by an observable condition and not by a guess about elapsed time. The only time-based limit is the test's cancellation token or an explicit `[Fact(Timeout = ...)]`, which the wait MUST use, kept solely so a hung platform cannot block the run; it is not the success condition.
- **FR-005**: The other tests of `PayloadConformanceTests` (including `CompletionBodiesAreValidatedAndStaleOnesAreRejectedByAcceptance`), and any test that uses a shared helper the fix changes, MUST keep every assertion and pass deterministically.
- **FR-006**: Production behavior MUST NOT change: any change to the covered production code MUST keep the behavior documented in `docs/architecture/`, and no message contract, subject, or stream definition changes.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The named test passes in 100% of at least 30 consecutive runs alone and in at least 10 consecutive runs under CPU load (one busy-loop process per core for the duration of the runs). If the failure does not reproduce naturally, the evidence additionally contains a deliberate reproduction in which the old test fails and the fixed test passes.
- **SC-002**: Every test of `PayloadConformanceTests` passes in the same consecutive runs, and the other integration tests that use a changed helper still pass.
- **SC-003**: Each of three probes (an invalid job payload, a missing segment identity field, and a segment on a non-segment job) makes the named test fail on its assertions; the probes are removed afterward.
- **SC-004**: The code comment and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PayloadConformanceEvidence.md` name the evidenced cause and the suspected causes ruled out; the change touches only the files the cause lives in, this one evidence file, and this spec folder; no assertion is removed and no fixed wait or retry is added.
- **SC-005** (post-merge evidence, observed when #314 retries and not verified by a task of this spec): the verification of #314 reports no flaky `PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity` test.

## Assumptions

- **Cause**: The test belongs to the durable analysis workflow development evidence (see the merged spec [Durable Analysis Workflow](../20261005-130703-durable-analysis-workflow/spec.md)). The concrete cause is unknown; the suspected causes above are hypotheses established or ruled out during implementation (FR-001).
- **Dependencies**: [Analysis Fact Timeline](../../20261023-074600-analysis-fact-timeline/spec.md) (implementation #314) depends on this spec. A related spec that must not be repeated: [Deterministic Finalized Recording Set Run Test](../../20261024-070502-deterministic-finalized-run-test/spec.md) (a different test).
- **Out of scope**: Other flaky tests, the Web UI and Host test projects, and production values (outbox publication intervals, readiness timings, and stream settings are development values that stay as they are).
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
