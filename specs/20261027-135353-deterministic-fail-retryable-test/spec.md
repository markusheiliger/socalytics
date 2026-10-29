# Feature Specification: Deterministic Retryable Failure of the Simulated Agent Workload

**Feature Branch**: `20261027-135353-deterministic-fail-retryable-test`

**Created**: 2026-10-27

**Status**: Draft

**Fix for**: #360 (implementation of `specs/20261027-082549-reliable-e2e-image-push`, twin #359)

**Input**: User description: "`SimulatedAgentWorkloadBehaviorTests.FailRetryableEndsWithAttemptsExhausted` in SocAlytics.Platform.Integration.Tests/AgentOrchestration failed and then passed on its one rerun in the verifications of three implementation pull requests (#201, #284, #360), so it is flaky. It is not caused by the stopped spec `specs/20261027-082549-reliable-e2e-image-push`, which changes only the End-to-End test support. Find and remove the real cause where it lives (the test, its shared helper `RunToEndAsync`, the simulated workload runner, or the platform code it covers) without weakening, skipping, or deleting the test or its assertions (state `failed`, `failureReason` `attempts-exhausted`, exactly 3 attempts), without retries, fixed sleeps, or longer timeouts as the fix, and without changing the documented behavior of submission, work delivery, fenced claim, lease, failure, retry, and attempt exhaustion. The spec `specs/20261027-082549-reliable-e2e-image-push` depends on this spec."

## Clarifications

### Session 2026-10-27

- Q: May the fix change the test support code, the simulated agent workload, or platform code? → A: Yes. The cause may live in the test, `RunToEndAsync`, the fixture `AgentOrchestrationFixture`, the simulated workload runner (`SimulatedAgentWorkloadRunner`), or the platform's failure, retry, redelivery, and attempt-exhaustion operations; the fix goes where the cause lives, and no documented behavior may change.
- Q: Are longer limits, fixed sleeps, or retries acceptable? → A: No. A bounded wait on an observable condition, or control of the racing input, is allowed; nothing else.
- Q: How does this spec relate to the merged specs `specs/archive/20261020-224223-deterministic-simulated-workload-retry` (fail-once), `specs/archive/20261023-150804-deterministic-stall-expiry-test` (stall expiry), and `specs/archive/20261019-221242-harden-integration-test-timing`? → A: None may be repeated. The fail-once spec made the shared helper `RunToEndAsync` wait deterministically for the shared flow, and the stall spec controlled the clock jump; neither examined the remaining fail-retryable path of three consecutive retryable failures, redelivery between attempts, and attempt exhaustion. This spec handles that path.

## Problem

`FailRetryableEndsWithAttemptsExhausted` submits a private conversation and a direct invocation whose question carries the marker `[simulate:fail-retryable]` through the `/api/v1` operations. The in-process simulated agent workload then claims the work with a fenced claim, writes a checkpoint, renews its lease, and reports a retryable failure with `private_key_jwt` and DPoP; the platform redelivers the work through NATS JetStream until the attempts are exhausted. The test expects the invocation to end `failed` with `failureReason` `attempts-exhausted` after exactly three attempts. It failed and passed only on its single rerun in the verifications of three implementation pull requests (#201, #284, #360), so the documented platform behavior is correct when it passes and the failure comes from an uncontrolled race or from state shared between attempts.

The earlier fixes of the sibling tests left this path untreated. The candidate causes the implementation has to separate with evidence are:

- the timing of the retry redelivery between attempts against the fake clock, the runner's retry delay, and the fetch expiry, so a redelivered work item may be fetched too early, too late, or twice;
- the ordering between the attempt rows and the invocation's terminal state, so the test may read the invocation as `failed` or the attempt count before the third attempt's failure and its attempt row are committed;
- the attempt count read while the runner or the platform still has a report in flight;
- state shared between the attempts, such as a consumed redelivery budget, a lease, a fence, a DPoP nonce or proof, or a token that one attempt leaves behind for the next;
- another cause that the evidence shows (an external dependency, scheduling under load, or an unobserved event).

The implementation first establishes which of these produces the failure, from the code or from reproduced failures, and then removes it where it lives.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The retryable-failure test fails only when behavior is wrong (Priority: P1)

A maintainer running the Integration suite, or the verification of any implementation, sees `FailRetryableEndsWithAttemptsExhausted` pass whenever three retryable failures exhaust the attempts of an invocation, however loaded the runner, NATS, and the host are, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, and the implementation of `specs/20261027-082549-reliable-e2e-image-push` (#360) is held by its report; the person who asked wants the cause removed rather than rerun.

**Independent Test**: Run the test repeatedly, alone and with its class without synthetic CPU load; every run passes. CPU-loaded runs are optional supplemental evidence. Then, as manual local evidence that a person runs outside the automated implementation, break the behavior with a probe (for example, stop redelivering after the second attempt, or make the third attempt succeed) and confirm the test still fails on its assertions or its bounded wait instead of passing or hanging.

**Acceptance Scenarios**:

1. **Given** a runner, NATS, and host, **When** each of three attempts is claimed, fails retryably, and is redelivered, **Then** the invocation is read as `failed` with `attempts-exhausted` and exactly three attempts only after the third attempt's failure is committed, and the test passes; CPU-loaded runs are optional supplemental evidence.
2. **Given** the retry redelivery between attempts, **When** the runner and the platform observe the fake clock, the retry delay, and the fetch expiry, **Then** every redelivery produces exactly one further attempt and none is skipped or duplicated.
3. **Given** the redelivery stops early or a third attempt succeeds, **When** the bounded wait ends, **Then** the test fails on its unchanged assertions or on the bounded wait and does not hang.
4. **Given** the cause is in shared code, **When** `FailOnceSucceedsOnTheSecondAttempt`, `FailPermanentEndsWithExecutionFailed`, `StallExpiresTheFirstAttemptAndSucceedsOnTheSecond`, and `SimulatedAgentWorkloadTests` run, **Then** they keep their assertions and behavior and pass at least as reliably.

### Edge Cases

- The expected attempt count is exact (three): the fix must not accept another count, for example by waiting until counts match.
- A late or duplicate work notification must neither add an attempt nor hide a missing attempt from the test.
- Counts of attempts are read only after the invocation is terminal and the runner has stopped, so no report is in flight.
- The retry delay and the fetch expiry of the runner must not be lengthened to hide the race, and no fixed sleep may stand in for an awaited condition.
- A fix that changes the fake clock must not change the other behaviors (`fail-once`, `fail-permanent`, `stall`), which keep their helper behavior unchanged.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure of the test with evidence (the code, or 20 repeated runs of the test alone and at least 5 repeated runs of its class without synthetic CPU load, with logs of failing runs); CPU-loaded runs MAY be reported as optional supplemental evidence. Record the cause in the code comment next to the fix and in the implementation pull request comments and task summaries; findings MUST NOT be written to `research.md`.
- **FR-002**: The cause MUST be removed where it lives (the test, `RunToEndAsync`, the fixture, the simulated workload runner, or the platform code it covers) by waiting on the awaited condition or by controlling the racing input; not by retries, fixed sleeps, skipping, or lengthening a limit. A bounded wait on a condition is allowed.
- **FR-003**: Every assertion of the test (`failed`, `attempts-exhausted`, exactly three attempts) MUST remain, and no assertion, expected value, or test of `SimulatedAgentWorkloadBehaviorTests` MAY be removed, loosened, or skipped.
- **FR-004**: The documented behavior of invocation submission, work delivery, fenced claim, lease, failure, retry, and attempt exhaustion MUST NOT change: any platform change MUST keep the Agent Orchestration semantics documented in `docs/architecture/`.
- **FR-005**: A change to shared code MUST keep the other tests of `SimulatedAgentWorkloadBehaviorTests` and the shared support they use behaviorally unchanged and deterministic, and MUST NOT repeat the changes of the fail-once and stall specs.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The test passes in 100% of at least 20 consecutive runs alone and in at least 5 consecutive runs of its class without synthetic CPU load. CPU-loaded runs are optional supplemental evidence and are not required to satisfy this criterion.
- **SC-002** (manual local evidence, run by a person outside the automated implementation and not verified by a task of this spec): A probe that stops the redelivery after the second attempt, and a probe that makes the third attempt succeed, each applied temporarily to `SimulatedAgentWorkloadRunner.cs`, each make the test fail within the existing limits; the probes are removed afterward. The implementation does not claim the probes as verified.
- **SC-003**: The pull request names the evidenced cause and changes only the files the evidenced cause lives in, a focused test for a platform fix, no `docs/architecture/` change unless the fix makes a statement false, and this spec folder; no assertion is textually removed.
- **SC-004** (post-merge evidence, observed when #360 retries and not verified by a task of this spec): the verification of #360 reports no flaky `SimulatedAgentWorkloadBehaviorTests` test.

## Assumptions

- **Cause**: The test belongs to Agent Orchestration direct invocations (merged spec [Agent Orchestration Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md)); it exercises the real queue, claim, lease, failure, redelivery, and exhaustion operations against the in-process runner and the fixture's fake time. The concrete cause is established during implementation (FR-001).
- **Dependencies**: [Reliable End-to-End Image Push](../20261027-082549-reliable-e2e-image-push/spec.md) depends on this spec. Related specs that must not be repeated: [Deterministic Fail-Once Retry of the Simulated Agent Workload](../archive/20261020-224223-deterministic-simulated-workload-retry/spec.md), [Deterministic Stall Expiry of the Simulated Agent Workload](../archive/20261023-150804-deterministic-stall-expiry-test/spec.md), and [Harden Integration Test Timing](../archive/20261019-221242-harden-integration-test-timing/spec.md).
- **Out of scope**: Other flaky tests, the Web UI and Host test projects, simulated behaviors unrelated to the retryable failure, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
