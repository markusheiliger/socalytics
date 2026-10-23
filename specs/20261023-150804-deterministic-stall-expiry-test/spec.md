# Feature Specification: Deterministic Stall Expiry of the Simulated Agent Workload

**Feature Branch**: `20261023-150804-deterministic-stall-expiry-test`

**Created**: 2026-10-23

**Status**: Draft

**Fix for**: #184 (implementation of `specs/20261017-202624-member-invitations`, twin #154)

**Input**: User description: "`SimulatedAgentWorkloadBehaviorTests.StallExpiresTheFirstAttemptAndSucceedsOnTheSecond` in SocAlytics.Platform.Integration.Tests/AgentOrchestration failed and then passed on its one rerun in the verifications of three implementation pull requests (#184, #194, #214), so it is flaky. The Stall-specific cause is the unsynchronized step in the shared helper `RunToEndAsync` that polls the attempt rows for one row and then advances the shared fake clock by 3 seconds to expire the first attempt's lease. Find and remove the real cause where it lives, without weakening, skipping, or deleting the test or its assertions (state `succeeded`, exactly 2 attempts, at least one checkpoint), without retries, fixed sleeps, or longer timeouts as the fix, and without weakening production behavior. The Member Invitations spec depends on this spec."

## Clarifications

### Session 2026-10-23

- Q: May the fix change the test support code, the simulated agent workload, or platform code? → A: Yes. The cause may live in the test, its shared helper `RunToEndAsync`, the simulated workload runner (`SimulatedAgentWorkloadRunner`), the fixture `AgentOrchestrationFixture`, or the platform's claim, lease, and expiry operations; the fix goes where the cause lives, and no production behavior may be weakened.
- Q: Are longer limits, fixed sleeps, or retries acceptable? → A: No. A bounded wait on an observable condition is allowed; nothing else.
- Q: How does this spec relate to the merged fail-once spec (`specs/20261020-224223-deterministic-simulated-workload-retry`, #209) and `specs/archive/20261019-221242-harden-integration-test-timing`? → A: Neither may be repeated. #209 made the fail-once retry deterministic through the shared helper and left the Stall clock advance unchanged; the archived spec only raised limits. This spec handles the Stall-specific fake-clock expiry.

## Problem

`StallExpiresTheFirstAttemptAndSucceedsOnTheSecond` submits a question marked `[simulate:stall]` and runs the in-process simulated agent workload against the real queue, claim, lease, and expiry operations. The first attempt is claimed but neither renews its lease nor ends; the platform's monitor must expire it, and a second attempt must then succeed. The test expects the invocation to end `succeeded` after exactly two attempts with at least one checkpoint. It passed only on its rerun in three verifications, so the platform behavior is correct when it passes and the failure comes from an uncontrolled race.

The helper expires the first attempt by polling for one attempt row and then advancing the shared fake clock by 3 seconds (the lease is 2 seconds). That step is not synchronized with the runner or the platform:

- the attempt row can exist before the runner has received the claim response, decided to stall, or established its lease, so the clock jump can precede the stall being in effect;
- the clock jump races the runner's own lease, heartbeat, and retry-delay timing and the lease monitor's observation, so the first attempt may never be expired, may be expired twice, or the second attempt may begin before the stall is in effect;
- the runner and the platform share one fake clock that other parts of the test also read, so a jump is observed at an unpredictable moment.

The implementation first reproduces the failure and identifies which of these (or another cause: timing, ordering, shared state, fake-clock interplay, or an external dependency) produces it, using evidence such as repeated runs under load and the platform and runner logs, and then removes that cause where it lives.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The stall test fails only when behavior is wrong (Priority: P1)

A maintainer running the Integration suite, or the verification of any implementation, sees the stall test pass whenever a stalled first attempt is expired by the platform and the second attempt succeeds, however loaded the runner and the host are, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, and the Member Invitations implementation (#184) is held by its report; the person who asked wants the cause removed rather than rerun.

**Independent Test**: Run the test repeatedly, alone and with its class under CPU load; every run passes. Then break the expiry behavior with a probe (for example, never expire the first attempt, or make the second attempt fail) and confirm the test still fails on its assertions or its bounded wait instead of passing or hanging.

**Acceptance Scenarios**:

1. **Given** a loaded runner and host, **When** the first attempt is claimed and its lease established late, **Then** the first attempt is expired only after it is observably claimed, stalled, and leased, and the test passes once the invocation has succeeded with two attempts and at least one checkpoint.
2. **Given** the first attempt is expired, **When** the platform and the runner observe the clock, **Then** the expiry happens exactly once and the second attempt starts only after the stall of the first is in effect.
3. **Given** the first attempt is never expired or the second never succeeds, **When** the bounded wait ends, **Then** the test fails on its unchanged assertions or on the bounded wait and does not hang.
4. **Given** the cause is in shared code, **When** `FailOnceSucceedsOnTheSecondAttempt`, `FailRetryableEndsWithAttemptsExhausted`, `FailPermanentEndsWithExecutionFailed`, and `SimulatedAgentWorkloadTests` run, **Then** they keep their assertions and behavior and pass at least as reliably.

### Edge Cases

- The expected attempt count is exact (two): the fix must not accept another count, for example by waiting until counts match.
- A late or duplicate work notification must neither add an attempt nor hide a missing expiry from the test.
- The clock advance must not overshoot into expiring the second attempt's lease or the runner's retry timing.
- Counts of attempts and checkpoints are read only after the invocation is terminal and the runner has stopped, so no report is in flight.
- The other behaviors (`fail-once`, `fail-retryable`, `fail-permanent`) must not require the clock to be advanced and keep their helper behavior unchanged.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure of the stall test with evidence (repeated runs with and without CPU load, and the logs of failing runs) and record it in the pull request.
- **FR-002**: The cause MUST be removed where it lives (the test, `RunToEndAsync`, the fixture, the simulated workload runner, or the platform code it covers) by advancing the clock only after the stalled first attempt is observably claimed and leased, and in a way the runner and the lease monitor observe deterministically, or by waiting on another observable condition; not by retries, fixed sleeps, skipping, or lengthening a limit. A bounded wait on a condition is allowed.
- **FR-003**: Every assertion of the test (`succeeded`, exactly two attempts, at least one checkpoint) MUST remain, and no assertion, expected value, or test of the class MAY be removed, loosened, or skipped.
- **FR-004**: Production behavior MUST NOT be weakened: any platform change MUST keep the claim, fencing, lease, expiry, retry, and outbox semantics of Agent Orchestration documented in `docs/architecture/`.
- **FR-005**: A change to shared code MUST keep `FailOnceSucceedsOnTheSecondAttempt`, `FailRetryableEndsWithAttemptsExhausted`, `FailPermanentEndsWithExecutionFailed`, and `SimulatedAgentWorkloadTests` behaviorally unchanged and MUST NOT repeat the change of #209.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The stall test passes in 100% of at least 20 consecutive runs alone and in at least 5 consecutive runs of its class under CPU load.
- **SC-002**: A probe that never expires the first attempt, and a probe that makes the second attempt fail, each make the test fail within the existing limits; the probes are removed afterward.
- **SC-003**: The pull request names the evidenced cause and changes only the files the evidenced cause lives in, a focused test for a platform fix (T005), a docs/architecture correction only if a statement became false (T009), and this spec folder; no assertion is textually removed.
- **SC-004** (post-merge evidence, observed when #184 retries and not verified by a task of this spec): the verification of #184 reports no flaky `SimulatedAgentWorkloadBehaviorTests` test.

## Assumptions

- **Cause**: The test belongs to Agent Orchestration direct invocations (merged spec [Agent Orchestration Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md)); it races the shared fake clock between the test, the in-process runner, and the platform's lease monitor. The concrete cause is established during implementation (FR-001).
- **Dependencies**: [Member Invitations](../20261017-202624-member-invitations/spec.md) depends on this spec. Related specs that must not be repeated: [Deterministic Fail-Once Retry of the Simulated Agent Workload](../20261020-224223-deterministic-simulated-workload-retry/spec.md) and [Harden Integration Test Timing](../archive/20261019-221242-harden-integration-test-timing/spec.md).
- **Out of scope**: Other flaky tests, simulated behaviors unrelated to the stall, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
