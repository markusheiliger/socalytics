# Feature Specification: Deterministic Fail-Once Retry of the Simulated Agent Workload

**Feature Branch**: `20261020-224223-deterministic-simulated-workload-retry`

**Created**: 2026-10-20

**Status**: Draft

**Fix for**: #201 (implementation of `specs/20261020-072206-deterministic-manager-tests`, twin #200)

**Input**: User description: "`SimulatedAgentWorkloadBehaviorTests.FailOnceSucceedsOnTheSecondAttempt` in SocAlytics.Platform.Integration.Tests/AgentOrchestration failed and then passed on its one rerun in the verifications of three implementation pull requests (#151, #186, #201), so it is flaky. Find and remove the cause of the flakiness (timing, ordering, shared state, external dependencies) in the test or in the code it covers, without weakening, skipping, or deleting the test or its assertions, and without adding retries or longer timeouts as the fix. The Analyst Manager tests spec (`specs/20261020-072206-deterministic-manager-tests`) depends on this spec."

## Clarifications

### Session 2026-10-20

- Q: Is the fix allowed to change the test support code or the simulated agent workload (`SocAlytics.Platform.SimulatedAgentWorkload`) if the cause is there? → A: Yes. The cause may live in the test, its shared helper `RunToEndAsync`, the simulated workload runner, or platform code the test covers; the fix goes where the cause lives, and no production behavior may be weakened.
- Q: Are longer limits an acceptable fix? → A: No. The requester excludes retries and longer timeouts as the fix; the test must wait on the awaited condition or control the racing input.
- Q: Does the spec cover the other tests of the class that the verifications also reported flaky? → A: Only through the shared helper they use. `FailRetryableEndsWithAttemptsExhausted` runs through the same `RunToEndAsync`, so a cause in that helper or the runner is removed for it too; its assertions are not otherwise in scope.

## Problem

`FailOnceSucceedsOnTheSecondAttempt` submits a question marked `[simulate:fail-once]`, runs the in-process simulated agent workload against the real queue, claim, and report operations, and expects the invocation to end `succeeded` after exactly two attempts with at least one checkpoint. It passed only on its rerun in three verifications, and `FailRetryableEndsWithAttemptsExhausted`, which shares the same helper, was reported flaky as well. The platform behavior is correct when the test passes, so the intermittent failure comes from an uncontrolled race between the test, the runner, and the platform.

The path of the test contains several sources of nondeterminism that the implementation must examine and remove where they cause the failure:

- the polling loop and the runner's lifetime are bounded by two independent real-time limits started at slightly different moments, while the retry needs a second notification published by the platform's outbox worker, on its own polling interval, after the first attempt failed;
- the runner's fetch of work notifications expires on a real-time interval, and a notification that is not settled (a transient report failure) is redelivered only after the queue's acknowledgement wait;
- the test reads the attempt and checkpoint counts right after the runner is cancelled, while the runner may be in the middle of a report whose effect the platform has not yet committed;
- the runner and the platform share one fake clock that other parts of the test also read.

The implementation first reproduces the failure and identifies which of these (or another cause) produces it, using evidence such as repeated runs under load and the platform and runner logs, and then removes that cause.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The fail-once test fails only when behavior is wrong (Priority: P1)

A maintainer running the Integration suite, or the verification of any implementation, sees `FailOnceSucceedsOnTheSecondAttempt` pass whenever a first failed attempt is retried and the second attempt succeeds, however loaded the runner is, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, and the Analyst Manager tests implementation #201 cannot merge while the Integration suite shows it.

**Independent Test**: Run the test repeatedly, alone and with the Integration suite under CPU load; every run passes. Then break the retry behavior with a probe (for example, make the runner fail every attempt) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** a loaded runner, **When** the first attempt fails as simulated and the retry is published and claimed late, **Then** the test passes as soon as the invocation has succeeded with two attempts and at least one checkpoint.
2. **Given** the second attempt never succeeds, **When** the test ends, **Then** it fails on the unchanged state, attempt, or checkpoint assertions and does not hang.
3. **Given** the identified cause is in the shared helper or the runner, **When** `FailRetryableEndsWithAttemptsExhausted`, `FailPermanentEndsWithExecutionFailed`, and `StallExpiresTheFirstAttemptAndSucceedsOnTheSecond` run, **Then** they keep their assertions and pass at least as reliably.

### Edge Cases

- The expected attempt count is exact (two): the fix must not make the test accept a different count, for example by waiting until counts match.
- A late notification redelivery or a duplicate notification must neither add an attempt nor hide a missing retry from the test.
- Reads of the attempt and checkpoint counts happen only after the invocation is terminal and the runner has stopped, so no report is in flight.
- The `Stall` behavior keeps expiring its first attempt through the fake clock and is not changed in behavior.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure of `FailOnceSucceedsOnTheSecondAttempt` with evidence (repeated runs, with and without CPU load, and the logs of the failing runs) and record it in the pull request.
- **FR-002**: The cause MUST be removed where it lives (the test, its shared helper `RunToEndAsync`, the simulated workload runner, or platform code) by controlling the racing input or by waiting on the awaited condition (invocation terminal, runner stopped), not by adding retries, skipping, or lengthening a fixed limit.
- **FR-003**: Every assertion of the test (`succeeded`, exactly two attempts, at least one checkpoint) MUST remain, and no assertion, expected value, or test of the class MAY be removed, loosened, or skipped.
- **FR-004**: Production behavior of the platform MUST NOT be weakened: any platform change MUST keep the claim, fencing, lease, retry, and outbox semantics of Agent Orchestration documented in `docs/architecture/`.
- **FR-005**: A change to shared code (the helper or the runner) MUST keep the other tests that use it (`FailRetryableEndsWithAttemptsExhausted`, `FailPermanentEndsWithExecutionFailed`, `StallExpiresTheFirstAttemptAndSucceedsOnTheSecond`, and `SimulatedAgentWorkloadTests`) behaviorally unchanged.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: `FailOnceSucceedsOnTheSecondAttempt` passes in 100% of at least 20 consecutive runs alone and in at least 5 consecutive runs of its class under CPU load.
- **SC-002**: A probe that makes the second attempt fail makes the test fail on its assertions within the existing limits; the probe is removed afterward.
- **SC-003**: The pull request names the evidenced cause and changes only the files that cause lives in plus this spec folder; no assertion is textually removed.
- **SC-004** (post-merge evidence, observed when #201 retries and not verified by a task of this spec): the merge verification of #201 reports no flaky `SimulatedAgentWorkloadBehaviorTests` test.

## Assumptions

- **Cause**: The test belongs to Agent Orchestration direct invocations (merged spec [Agent Orchestration Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md)); it races real time between the test, the in-process runner, and the platform's outbox and queue. The concrete cause is established during implementation (FR-001).
- **Dependencies**: [Analyst Manager Deterministic Tests](../20261020-072206-deterministic-manager-tests/spec.md) (implementation #201) depends on this spec. Related merged specs, which cover other tests and must not be repeated: [Bounded Wait for the Simulated Agent Workload Advice Test](../archive/20261020-144627-deterministic-integration-races/spec.md) and [Fix Upload Expiry Test](../archive/20261014-114032-fix-upload-expiry-test/spec.md).
- **Out of scope**: Other flaky tests, simulated behaviors unrelated to the fail-once retry, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
