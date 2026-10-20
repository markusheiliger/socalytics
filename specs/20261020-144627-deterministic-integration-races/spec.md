# Feature Specification: Bounded Wait for the Simulated Agent Workload Advice Test

**Feature Branch**: `20261020-144627-deterministic-integration-races`

**Created**: 2026-10-20

**Status**: Draft

**Fix for**: #201 (implementation of `specs/20261020-072206-deterministic-manager-tests`, twin #200)

**Input**: User description: "SimulatedAgentWorkloadTests.RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice in SocAlytics.Platform.Integration.Tests/AgentOrchestration fails intermittently on loaded runners (advice null): it gives the simulated runner and the polling loop 30 seconds of real time and then asserts the advice. Make it wait for the awaited condition with a generous bounded limit without weakening any assertion. The prerequisite of the Analyst Manager tests spec is that the platform Integration suite passes in the merge verification."

## Problem

The merge verification of #201 changed only Analyst Manager tests but failed in platform Integration tests of other features. Three tests failed intermittently:

- `PendingRequestNotificationTests.AClubAdminWhoLosesTheRoleBeforeTheSendReceivesNothing` and `SecretHygieneTests.NoSecretsReachLogsPersistedRecordingDataAuditOrRetryOutcomes` are already covered by the merged specs [Deterministic Club Admin Role-Loss Notification Test](../20261020-081004-deterministic-role-loss-test/spec.md) and [Timing Margins for Intermittent Integration Tests](../archive/20261019-221242-harden-integration-test-timing/spec.md); this spec does not repeat them.
- `SimulatedAgentWorkloadTests.RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice` is not covered. The test starts the simulated runner, then polls the invocation for at most 30 real seconds, and cancels the runner after the same 30 seconds. On a loaded runner the runner's connection, claim, and completion can take longer, the loop ends with no advice, and the assertion reports `null`. The platform behavior is correct; the test depends on winning a race against wall-clock time.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The advice test fails only when behavior is wrong (Priority: P1)

A maintainer running the Integration suite, or the verification of any implementation, sees the advice test pass whenever the runner claims and completes the invocation and the owner can read the simulated advice, however loaded the runner is.

**Why this priority**: The full verification of #201 stopped partly because of this test, and a retry meets the same race.

**Independent Test**: Run the test repeatedly, alone and with the Integration suite under CPU load; every run passes. Then make the runner never complete the invocation (probe) and confirm the test still fails with its advice assertion after the limit.

**Acceptance Scenarios**:

1. **Given** a loaded runner, **When** the simulated runner needs longer than 30 seconds to claim and complete the invocation, **Then** the test keeps waiting up to a generous limit and passes as soon as the advice is readable.
2. **Given** the invocation never completes, **When** the limit is reached, **Then** the test fails with the same advice assertions as today and does not hang beyond the limit.

### Edge Cases

- The runner's cancellation is tied to the limit of the wait plus a small margin, so the runner is not stopped before the test stops waiting.
- Other tests of `SimulatedAgentWorkloadTests` (such as the production-refusal test) keep their behavior.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The advice test MUST wait for the awaited condition (invocation succeeded with advice) up to a generous bounded limit, longer than 30 seconds, and return as soon as it holds; the runner's lifetime MUST cover that limit with a small margin.
- **FR-002**: The test MUST keep every assertion, including that the advice is not null and equals the simulated advice of the specialist; a wait that never succeeds MUST still fail with those assertions after the limit.
- **FR-003**: The change MUST stay in `SimulatedAgentWorkloadTests.cs` of the Integration test project; no production code, option default, other test, or assertion changes.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The test passes in 100% of at least 10 consecutive runs alone and once with CPU load applied.
- **SC-002**: A probe that stops the runner from completing the invocation makes the test fail on the advice assertion and within twice the limit (verified during implementation, probe removed afterward).
- **SC-003**: No file outside the Integration test project's `SimulatedAgentWorkloadTests.cs` and this spec folder changes, and no assertion is textually removed.
- **SC-004** (post-merge evidence, observed when #201 retries and not verified by a task of this spec): the merge verification of #201 passes this test.

## Assumptions

- **Cause**: The test belongs to Agent Orchestration direct invocations and races real time; the diagnosis of #201 (round 3) reported `advice null` at the test's line 105.
- **Dependencies**: [Analyst Manager Deterministic Tests](../20261020-072206-deterministic-manager-tests/spec.md) (implementation #201) depends on this spec, together with the two specs named above, which cover the other two tests that failed in its verification.
- **Out of scope**: Production code and defaults, the other two failing tests, other flaky tests, and skipping or retrying whole tests.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
