# Feature Specification: Timing Margins for Intermittent Integration Tests

**Feature Branch**: `20261019-221242-harden-integration-test-timing`

**Created**: 2026-10-19

**Status**: Draft

**Fix for**: #194 (implementation of `specs/20261018-101517-agent-catalog-registry`)

**Input**: User description: "Harden timing-sensitive tests in SocAlytics.Platform.Integration.Tests that fail intermittently on loaded runners: SecretHygieneTests (SessionLifetime of 5 seconds expires before the re-grant), ManagerConsumerTopologyTests and TransportRecoveryTests (NATS no-response and publish timeouts), SimulatedAgentWorkloadBehaviorTests.StallExpiresTheFirstAttemptAndSucceedsOnTheSecond and PayloadConformanceTests (wait for the condition with a generous timeout). Never weaken an assertion."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Integration tests fail only when behavior is wrong (Priority: P1)

A maintainer running the Integration suite, or the verification of any implementation, sees the five named test classes pass whenever the product behaves correctly, however loaded the runner is. Today they fail at random with timeouts: a 5-second upload session expires before a re-grant, NATS calls get no response or time out, and fixed short waits give up before a ready-work message or a stalled attempt appears.

**Why this priority**: Four full verifications of #194 failed in different, unrelated tests of earlier features, and two bare retries left the branch unchanged. Any other implementation will stop the same way.

**Independent Test**: Run each named test class repeatedly, alone and with the whole Integration suite, with CPU load applied; confirm every run passes. Then break the behavior each test checks (for example make sessions expire before the re-grant is no longer expected, or never publish ready work) and confirm the test still fails.

**Acceptance Scenarios**:

1. **Given** a loaded runner, **When** `SecretHygieneTests` creates an upload session, uploads a part, and re-grants, **Then** the session has not expired at the re-grant, and the test still asserts that a second, unused session is expired by the sweep.
2. **Given** a loaded runner, **When** the NATS-based tests of `ManagerConsumerTopologyTests` and `TransportRecoveryTests` publish and fetch, **Then** a slow response does not fail the test, while a response that never arrives still does after a generous limit.
3. **Given** a loaded runner, **When** `SimulatedAgentWorkloadBehaviorTests.StallExpiresTheFirstAttemptAndSucceedsOnTheSecond` and `PayloadConformanceTests` wait for an attempt, a state, or a ready-work message, **Then** they wait on the condition up to a generous limit and pass as soon as it holds.
4. **Given** any of these tests, **When** the behavior under test is wrong, **Then** the same assertion fails as today.

### Edge Cases

- A wait that never succeeds must still fail with a clear message and not hang beyond its limit.
- Where the test time is controlled by the fake clock, the expiry assertion is driven by the clock, not by real sleeping.
- Tests not named here keep their behavior.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `SecretHygieneTests` MUST NOT depend on a real-time race between the upload `SessionLifetime` and the setup steps: the first session's lifetime MUST have ample margin (or be controlled by the factory's fake clock) and the second session MUST still be shown to expire through the sweep, keeping every secret and expiry assertion.
- **FR-002**: The NATS publish, fetch, and request calls of `ManagerConsumerTopologyTests` and `TransportRecoveryTests` MUST tolerate load by using larger client timeouts or bounded retries of the call that timed out (the check itself unchanged), failing after a generous overall limit.
- **FR-003**: Fixed short waits in `SimulatedAgentWorkloadBehaviorTests` and `PayloadConformanceTests` MUST become condition waits with a generous overall limit that return as soon as the condition holds.
- **FR-004**: No assertion, expected value, or secret list MUST be removed, loosened, or skipped; every limit only raises the time allowed for a correct outcome.
- **FR-005**: The change MUST be limited to the five named test files and shared test support in the Integration test project when a helper is needed; no production code, product option default, or other test's assertion changes.

### Key Entities

- **Timing margin**: A real-time limit, interval, or lifetime a test relies on to observe a condition.
- **Condition wait**: A bounded poll that returns when an observable condition holds and fails with a message when its limit passes.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Each of the five test classes passes in 100% of at least 10 consecutive runs alone, and the whole Integration suite passes in a run, with CPU load applied during at least one of them.
- **SC-002**: A deliberate probe that breaks the behavior each hardened test checks (session expiry by the sweep, ready-work publication, attempt expiry) makes that test fail in 100% of probes (verified during implementation, probe removed afterward).
- **SC-003** (post-merge evidence, observed when #194 retries and not verified by a task of this spec): The phase verification of #194 passes the Integration suite without a retry.
- **SC-004**: No file outside the Integration test project and this spec folder changes, and no assertion is textually removed.

## Assumptions

- **Cause**: The failing tests belong to earlier features on the base branch and rely on tight real-time margins: a 5-second upload `SessionLifetime` against a container-backed storage upload, NATS calls with default short timeouts, and fixed waits for ready work and attempt state. On loaded runners the Integration run takes 12 to 18 minutes and the failing set moves each run (diagnosis of #194; runs 39317850704, 39310929196, 39295003683). The defect is in the tests, not in the platform or the agent catalog.
- **Smallest fix**: The plan picks the mechanism per test (longer lifetime plus the fake clock, larger NATS timeouts or retry of the timed-out call, condition waits). The requirements decide acceptance.
- **Dependencies**: The owning features are the recordings upload, transport, and agent orchestration specs on the base branch (see [Recording Lineage and Upload](../archive/20261005-130702-recording-lineage-upload/spec.md)). [Agent Catalog and Registry](../20261018-101517-agent-catalog-registry/spec.md), whose implementation is #194, depends on this spec and waits for it.
- **Out of scope**: Production code and defaults, the telemetry capture test of [Deterministic Telemetry Capture in the Password Recovery Secret Test](../20261019-102401-fix-recovery-telemetry-scope/spec.md), other flaky tests not named, and skipping or retrying whole tests.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
