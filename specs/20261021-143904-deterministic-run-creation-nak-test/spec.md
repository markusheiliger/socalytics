# Feature Specification: Deterministic Unavailable-Lookup NAK Test

**Feature Branch**: `20261021-143904-deterministic-run-creation-nak-test`

**Created**: 2026-10-21

**Status**: Draft

**Fix for**: #214 (implementation of `specs/20261017-183953-analysis-run-supersession`, twin #147, pull request #211, archived at [specs/archive/20261017-183953-analysis-run-supersession](../archive/20261017-183953-analysis-run-supersession/spec.md))

**Input**: User description: "`RunCreationTests.UnavailableLookupNaksAndRunIsCreatedAfterRecovery` in SocAlytics.Platform.Integration.Tests/Analysis failed and then passed on its one rerun in the verification of implementation pull request #214, so it is flaky. It belongs to the analysis run creation feature (the recordings-finalized consumer of the [durable analysis workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md)). Make it deterministic without weakening, skipping, or deleting it or its assertions, and without retries or longer timeouts as the fix. The analysis run supersession spec (`specs/20261017-183953-analysis-run-supersession`, twin #147, pull request #211) depends on this spec."

## Clarifications

### Session 2026-10-21

- Q: May the fix change production code if the cause lives there? → A: Yes, but only where the evidence puts the cause, and without weakening the documented behavior: an unavailable recording set lookup postpones (NAKs) the notification and leaves no receipt, and exactly one run is created after recovery. Test-only causes are fixed in the test project.
- Q: Are longer limits or retries acceptable? → A: No. The test must wait on the awaited condition or control the racing input.
- Q: Does the spec cover the other tests of `RunCreationTests`? → A: Only through shared helpers they use. Their assertions are not otherwise in scope.
- Q: Is `WorkflowAvailabilityTests.Pending_notifications_wait_while_unresolvable_and_produce_one_run_after_recovery` in scope? → A: No. [Deterministic Pending-Notification Recovery Test](../20261021-010051-deterministic-workflow-availability-test/spec.md) owns that different test and this spec must not repeat it.

## Problem

The test starts the platform with the analysis workers enabled and a recording set lookup wrapper whose switch makes every lookup fail while it is set. It finalizes one recording set, waits until the outbox published the finalization event, sleeps a fixed three seconds, asserts that no notification receipt exists, clears the switch, and polls for exactly one run (120 polls, 500 ms apart). It failed in the verification of #214 and passed only on its one rerun.

The path of the test contains sources of nondeterminism that the implementation must examine and remove where they cause the failure:

- the fixed three-second sleep stands in for "the consumer has received the notification and postponed it at least once" (the postponement delay is one second), so the "no receipt yet" assertion either proves little (the consumer has not yet attempted the lookup) or races with consumer startup on a loaded runner;
- the wait after recovery depends on real-time redelivery of the postponed notification;
- the NATS server and its streams are shared by the tests of the collection, so the consumer of this test may fetch notifications left by earlier tests whose recording sets do not exist in this test's database, spending time or adding receipts;
- the switch is not observable: nothing tells the test that the lookup was attempted while unavailable.

The implementation first identifies the actual cause with evidence (repeated runs under load, consumer logs, shared NATS server and stream state of the collection, and the redelivery and postponement timing), and then removes it.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The NAK-and-recover test fails only when behavior is wrong (Priority: P1)

A maintainer running the Integration suite, or the verification of any implementation that changes run creation, sees the test pass whenever an unavailable recording set lookup postpones the notification without a receipt and exactly one run is created after recovery, however loaded the runner is, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verification of #214 (the implementation of the run supersession feature, which exercises run creation), which then passed only on a rerun and reported a flaky test that the feature did not cause.

**Independent Test**: Run the test repeatedly, alone and with the Integration suite under CPU load; every run passes. Then break the behavior with probes (acknowledge the notification while the lookup is unavailable, or create two runs after recovery) and confirm the test fails on its assertions.

**Acceptance Scenarios**:

1. **Given** a loaded runner and a lookup that is unavailable, **When** the finalization event is published, **Then** the test proceeds to its "no receipt" assertion only once the consumer provably attempted the lookup while it was unavailable, without relying on a fixed sleep.
2. **Given** the lookup becomes available, **When** the consumer redelivers the postponed notification, **Then** the test passes as soon as exactly one run exists, without depending on a fixed number of real-time polls.
3. **Given** earlier tests of the same collection published notifications, **When** this test runs, **Then** it consumes and counts only its own notification.
4. **Given** the behavior is wrong, **When** the test ends, **Then** the unchanged assertions fail and the test does not hang.

### Edge Cases

- The "no receipt exists" assertion MUST hold at a point that is provably after at least one unavailable lookup attempt of this test's notification, not merely after a delay.
- The "exactly one run" count stays exact: the fix must not accept a different count or wait until counts match.
- A late redelivery or duplicate notification must neither add a run nor hide a missing run from the test. This edge case is covered by the "two runs after recovery" probe of SC-002: the exact count is asserted after the awaited state has been reached, so a later duplicate that added a second run would fail the exact-one-run assertion.
- The count is read only after the awaited state has been reached, so no settle is in flight.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (repeated runs with and without CPU load, and the logs of the failing runs) and record it in a code comment next to the fix; the task result or pull request comment MUST summarize the reproduction and log evidence. If the failure cannot be reproduced after the documented runs, the implementation MUST record that result, base the cause on the log and timing evidence available (for example the code path showing the sleep or shared consumer racing), state it explicitly as the most likely cause in the code comment, and still apply the deterministic waits.
- **FR-002**: The cause MUST be removed where it lives (the test, its shared support, the NATS test fixture, or platform code) by controlling the racing input, isolating shared state, or waiting on the awaited condition, not by retrying the test, skipping, or lengthening a fixed limit or sleep.
- **FR-003**: The test MUST obtain an awaitable signal that the consumer attempted the lookup while it was unavailable (for example a counted, awaitable record of the calls of the lookup wrapper) and use it instead of the fixed sleep before asserting that no receipt exists.
- **FR-004**: After recovery the test MUST await the observable outcome (exactly one run) rather than a fixed polling budget measured in real time, and the wait MUST remain bounded so a wrong behavior fails instead of hanging. The wait MUST be a condition-driven loop with an explicit finite deadline (for example a linked `CancellationTokenSource` with a stated timeout, since the test's own cancellation token is not guaranteed to be finite) that fails the test with a clear message on expiry; the deadline is a failure bound, not the expected duration, and it MUST NOT exceed the 60 s budget of the existing shared `WaitAsync` (120 polls of 500 ms), so it is not a lengthened limit. The shared `WaitAsync` MUST keep its behavior for the other tests; only this test uses the new wait or a new helper (FR-007).
- **FR-005**: Every assertion of the test (no receipt while unavailable, exactly one run after recovery) MUST remain, and no assertion, expected value, or test of the class MAY be removed, loosened, or skipped.
- **FR-006**: Production behavior MUST NOT be weakened: any platform change MUST keep the postponement, redelivery, idempotency, and single-run semantics of the recordings-finalized consumer documented in `docs/architecture/`.
- **FR-007**: A change to shared code MUST keep the other tests of `RunCreationTests` and the collection behaviorally unchanged.
- **FR-008**: The change MUST NOT repeat the scope of [Deterministic Pending-Notification Recovery Test](../20261021-010051-deterministic-workflow-availability-test/spec.md) or [Timing Margins for Intermittent Integration Tests](../archive/20261019-221242-harden-integration-test-timing/spec.md).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The test passes in 100% of at least 20 consecutive runs alone and in at least 5 consecutive runs of its class under CPU load.
- **SC-002**: A probe that acknowledges the notification while the lookup is unavailable, and a probe that creates two runs after recovery, each make the test fail on its assertions; the probes are removed afterward.
- **SC-003**: The test contains no fixed-duration sleep standing in for a condition; the code comment names the evidenced cause, the task result or pull request comment summarizes the reproduction and log evidence, and the change touches only the files that cause lives in plus this spec folder; no assertion is textually removed.
- **SC-004** (post-merge evidence, observed when #214 retries and not verified by a task of this spec): the verification of #214 reports no flaky `RunCreationTests` test.

## Assumptions

- **Cause**: The test belongs to the analysis run creation feature (the merged [durable analysis workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md)); it races real time and shared NATS state between the test and the consumer worker. The concrete cause is established during implementation (FR-001).
- **Dependencies**: [Analysis Run Supersession](../archive/20261017-183953-analysis-run-supersession/spec.md) (twin #147, implementation #211/#214) depends on this spec. Related specs that cover other tests and must not be repeated: [Deterministic Pending-Notification Recovery Test](../20261021-010051-deterministic-workflow-availability-test/spec.md) and [Timing Margins for Intermittent Integration Tests](../archive/20261019-221242-harden-integration-test-timing/spec.md).
- **Out of scope**: Other flaky tests, the default values of the notification options (including the postponement delay), and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
