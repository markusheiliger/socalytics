# Feature Specification: Deterministic Finalized Recording Set Run Test

**Feature Branch**: `20261024-070502-deterministic-finalized-run-test`

**Created**: 2026-10-24

**Status**: Draft

**Fix for**: #273 (implementation of `specs/20261023-012951-conversation-archive-delete`)

**Input**: User description: "Make the Docker-backed integration test `SocAlytics.Platform.Integration.Tests.Analysis.RunCreationTests.FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun` deterministic. It lives in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs` and covers the outbox publisher (`OutboxPublicationStore` and its worker), the publication of `matches.recordings-finalized` through the message broker, and the recordings-finalized consumer that creates the analysis run and its notification receipt. It failed and then passed on its one rerun in the verifications of implementation pull requests #201, #216, and #273, so it is flaky, and the flakiness is not caused by the stopped spec `specs/20261023-012951-conversation-archive-delete`. Find and remove the cause of the nondeterminism in the test or in the code it covers, without weakening, skipping, or deleting the test or its assertions (exactly one run and a single notification receipt after duplicate publications stay asserted) and without retries or longer timeouts as the fix. The spec `specs/20261023-012951-conversation-archive-delete` depends on this spec."

## Clarifications

### Session 2026-10-24

- Q: May the fix change production code if the cause lives there? → A: Yes, but only where the evidence puts the cause, and without changing the documented behavior of outbox publication, duplicate suppression, or run creation. Test-only causes are fixed in the test or its shared helpers.
- Q: Are fixed sleeps, retries, or longer timeouts acceptable? → A: No. Every wait must be on an observable condition. The fixed 4-second sleep before counting runs and receipts is the kind of timing guess to remove, and no replacement may be a fixed sleep.
- Q: Which tests are in scope? → A: The named test, and any test of `RunCreationTests` whose changed shared helper it uses, which must stay passing and deterministic. Other flaky tests are out of scope.

## Problem

The test finalizes a recording set, waits until the analysis run exists, and then asserts that the outbox message of the finalization is already in state `published`. It then publishes the same finalization message twice more and proves that duplicates create no second run and no second receipt. It fails in the verification of implementation pull requests and then passes on its single rerun, so the failure depends on timing and not on the behavior under test.

Suspected causes, to be confirmed by evidence before they are fixed:

- **Assertion races the publisher**: the consumer can receive the message and create the run before the publisher has recorded the message as published, so the immediate `published` assertion can observe the state a moment too early. This is the most likely cause and is verified first.
- **Absence proven by elapsed time**: the duplicate handling is shown by sleeping a fixed 4 seconds and then counting runs and receipts, so a duplicate that is still in flight when the count is taken is missed (a false pass), and a slow runner can make other steps exceed their expectations.
- Other candidates the implementation must rule in or out: ordering between the publisher, the broker, and the consumer; state shared between the tests of the class (broker streams, durable consumers, or settings) and left over from a previous test; and external dependencies such as container startup or topology provisioning that a wait does not observe.

The implementation first reproduces the failure, or demonstrates the removed race with a deliberate widening of the window, and identifies which cause (or another) produces it, and then removes that cause.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The finalized recording set test fails only when behavior is wrong (Priority: P1)

A maintainer running the integration tests, or the verification of any implementation pull request, sees the test pass whenever a finalized recording set flows through publication to exactly one run and one receipt even when duplicates are published, however loaded the runner is, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, which pass only on a rerun and report a flaky test that none of those features caused; the stopped implementation of #273 needs it reliable.

**Independent Test**: Run the test repeatedly alone and under CPU load; every run passes. Then break the behavior with a probe (for example a consumer that creates a second run for a duplicate) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** a finalized recording set whose message is being published and consumed, **When** the test waits for the run, **Then** it asserts the published state only after waiting for that state as an observable condition, so that no ordering of publisher and consumer makes it fail.
2. **Given** the first message was handled and the run exists, **When** the same message is published twice more, **Then** the test waits until both duplicate publications are observably handled (for example by the receipt of each duplicate message identifier or by the consumer having acknowledged them) before it counts, and the counts of exactly one run and one notification receipt are asserted unchanged.
3. **Given** the duplicate handling were broken so that a second run or receipt appears, **When** the test runs, **Then** the unchanged assertions fail and the test does not hang.
4. **Given** the other tests of `RunCreationTests` use the same helpers, **When** they run, **Then** each keeps its assertions and starts without broker or database state left over from a previous test.

### Edge Cases

- A duplicate that the consumer handles after the counts were taken MUST NOT be able to pass the test unnoticed; the counts MUST be taken only after the duplicates were observably handled.
- A message handled before the publisher recorded its published state MUST NOT fail the test; the publisher's state MUST be reached through an observable wait.
- A failure in the middle of the test MUST NOT leave broker or database state that affects another test.
- The fix MUST NOT depend on the order in which the tests of the class run.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (repeated runs of the test with and without CPU load, and a demonstration of the removed race, for example a deliberate test-side delay of the publisher's published-state commit under which the old test fails and the new one passes) and record it in a code comment next to the fix and in the `## Evidence` section of `research.md` (the commit message, written by the person or automation that commits, repeats it).
- **FR-002**: The cause MUST be removed where it lives (the test, its shared helpers, or the covered production code) by waiting on an observable condition, controlling the racing input, or isolating shared state, not by retrying the test, skipping it, adding a fixed sleep, or lengthening a timeout. The fixed 4-second sleep MUST NOT remain or be replaced by another fixed wait.
- **FR-003**: The assertion that the outbox message is in state `published` MUST remain and MUST be evaluated only after the state was reached or a bounded wait on that condition has failed.
- **FR-004**: The proof that two duplicate publications cause exactly one run and one receipt MUST wait for the observable handling of both duplicates before counting, and the assertions of exactly one run and one notification receipt MUST remain unchanged.
- **FR-005**: Every assertion of every test of `RunCreationTests` MUST remain, and no assertion, expected value, or step MAY be removed, loosened, or skipped.
- **FR-006**: Production behavior MUST NOT be changed: any change to the outbox publisher, publication, or the consumer MUST keep the documented behavior in `docs/architecture/`, and any change to shared test helpers MUST keep the other integration tests that use them behaviorally unchanged and passing.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The named test passes in 100% of at least 30 consecutive runs alone and in at least 10 consecutive runs under CPU load (one busy-loop process per core for the duration of the runs). If the failure does not reproduce naturally, the evidence additionally contains a deliberate reproduction (the race widened test-side only) in which the old test fails and the new test passes.
- **SC-002**: Every test of `RunCreationTests` passes in the same consecutive runs, and the other integration tests that use the changed helpers still pass.
- **SC-003**: Each of two probes (a consumer that creates a second run for a duplicate, and a consumer that writes a second receipt) makes the named test fail on its assertions; the probes are removed afterward.
- **SC-004**: The code comment and the `## Evidence` section of `research.md` name the evidenced cause, and the change touches only the files the cause lives in plus this spec folder; no assertion is textually removed and no fixed wait or retry is added.
- **SC-005** (post-merge evidence, observed when #273 retries and not verified by a task of this spec): the verification of #273 reports no flaky `FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun` test.

## Assumptions

- **Cause**: The test belongs to the durable analysis workflow evidence (outbox publication, broker publication, and the recordings-finalized consumer, see the merged specs under `specs/archive/`). The primary cause is presumed to be the immediate published-state assertion racing the publisher, and the fixed sleep an additional weakness; this is a hypothesis, and the concrete cause is established during implementation (FR-001).
- **Dependencies**: [Conversation Archive and Delete](../20261023-012951-conversation-archive-delete/spec.md) (implementation #273) depends on this spec. Related specs that must not be repeated: [Deterministic Stall Expiry Test](../archive/20261023-150804-deterministic-stall-expiry-test/spec.md) and [Deterministic Integration Races](../archive/20261020-144627-deterministic-integration-races/spec.md) (different tests).
- **Out of scope**: Other flaky tests and production values (publisher poll intervals, redelivery delays, and retention are development values that stay as they are). `UnavailableLookupNaksAndRunIsCreatedAfterRecovery` has a similar fixed 3-second wait but is a different test and is not changed unless it shares a helper this fix changes.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
