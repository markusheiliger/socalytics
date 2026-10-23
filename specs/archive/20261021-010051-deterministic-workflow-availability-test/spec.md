# Feature Specification: Deterministic Pending-Notification Recovery Test

**Feature Branch**: `20261021-010051-deterministic-workflow-availability-test`

**Created**: 2026-10-21

**Status**: Draft

**Fix for**: #184 (implementation of `specs/20261017-202624-member-invitations`, twin #154)

**Input**: User description: "`WorkflowAvailabilityTests.Pending_notifications_wait_while_unresolvable_and_produce_one_run_after_recovery` in SocAlytics.Platform.Integration.Tests/Analysis failed and then passed on its one rerun in the verifications of three implementation pull requests (#184, #199, #208), so it is flaky. Find and remove the cause of the flakiness (timing, ordering, shared state, external dependencies) in the test or in the code it covers, without weakening, skipping, or deleting the test or its assertions, and without adding retries or longer timeouts as the fix. The member invitations spec (`specs/20261017-202624-member-invitations`) depends on this spec."

## Clarifications

### Session 2026-10-21

- Q: May the fix change production code if the cause lives there? → A: Yes, but only where the evidence puts the cause, and without weakening the documented behavior of workflow availability, notification redelivery, or run creation. Test-only causes are fixed in the test project.
- Q: Are longer limits or retries acceptable? → A: No. The requester excludes them; the test must wait on the awaited condition or control the racing input.
- Q: Does the spec cover the other tests of `WorkflowAvailabilityTests`? → A: Only through shared helpers or code they use. Their assertions are not otherwise in scope.
- Q: Are the timing margins of `SecretHygieneTests` and the other tests of [Timing Margins for Intermittent Integration Tests](../20261019-221242-harden-integration-test-timing/spec.md) in scope? → A: No; that spec owns them and this spec must not repeat it.

## Problem

The test starts the platform with the analysis workers enabled and a resolver whose switch makes the default workflow unresolvable. It finalizes one recording set, waits until the outbox published the finalization event, sleeps a fixed four seconds, asserts that no receipt and no run exist and that the workflow health check is unhealthy, then flips the switch and waits for exactly one run, a healthy check, and exactly one receipt. It passed only on its rerun in three verifications.

The path of the test contains several sources of nondeterminism that the implementation must examine and remove where they cause the failure:

- the fixed four-second sleep stands in for "the consumer has seen and postponed the notification at least once", so the "nothing happened" assertions prove little or race with startup on a loaded runner;
- the availability status is cached for the recheck interval and refreshed on read, so the health check, the consumer worker, and the handler can observe the switch at different moments;
- the NATS server and its streams are shared by every test of the `NatsFaultInjection` collection, so the consumer of this test may fetch notifications left by an earlier test whose recording sets do not exist in this test's database, adding a receipt or consuming time before the expected single run;
- the receipt and run counts are read as independent statements after the run appears, while the consumer settles the notification in its own transaction;
- redelivery of a postponed notification depends on a one-second delay and a two-second fetch expiry measured in real time.

The implementation first reproduces the failure and identifies which of these (or another cause) produces it, using evidence such as repeated runs under load and the platform and consumer logs, and then removes that cause.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The pending-notification test fails only when behavior is wrong (Priority: P1)

A maintainer running the Integration suite, or the verification of any implementation, sees the test pass whenever a notification stays pending while the default workflow is unresolvable and produces exactly one run with one receipt after recovery, however loaded the runner is, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, which then pass only on a rerun and report a flaky test that another feature did not cause.

**Independent Test**: Run the test repeatedly, alone and with the Integration suite under CPU load; every run passes. Then break the behavior with a probe (for example create the run while the workflow is unresolvable, or create two runs after recovery) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** a loaded runner, **When** the finalization event is published while the workflow is unresolvable, **Then** the test observes that the consumer postponed the notification (no receipt, no run, unhealthy check) before it recovers the workflow, without relying on a fixed sleep.
2. **Given** the workflow becomes resolvable, **When** the consumer redelivers the pending notification, **Then** the test passes as soon as exactly one run and exactly one receipt exist and the check is healthy.
3. **Given** earlier tests of the same collection published notifications, **When** this test runs, **Then** it consumes and counts only its own notification.
4. **Given** the behavior is wrong, **When** the test ends, **Then** the unchanged assertions fail and the test does not hang.

### Edge Cases

- The assertions "no receipt and no run exist while unresolvable" MUST hold at a point that is provably after the consumer has handled the notification at least once, not merely after a delay.
- The "exactly one" counts stay exact: the fix must not accept a different count or wait until counts match.
- A late redelivery or duplicate notification must neither add a run nor hide a missing run from the test.
- Reads of the receipt and run counts happen only after the awaited state has been reached, so no settle is in flight.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (repeated runs with and without CPU load, and the logs of the failing runs) and record it in a code comment next to the fix and in the commit message.
- **FR-002**: The cause MUST be removed where it lives (the test, its shared support, the NATS test fixture, or platform code) by controlling the racing input, isolating shared state, or waiting on the awaited condition, not by retrying the test, skipping, or lengthening a fixed limit.
- **FR-003**: Every assertion of the test (no receipt, no run, unhealthy check while unresolvable; exactly one run, healthy check, exactly one receipt after recovery) MUST remain, and no assertion, expected value, or test of the class MAY be removed, loosened, or skipped.
- **FR-004**: Production behavior MUST NOT be weakened: any platform change MUST keep the postponement, redelivery, idempotency, and single-run semantics of the analysis notification workflow documented in `docs/architecture/`.
- **FR-005**: A change to shared code MUST keep the other tests that use it behaviorally unchanged, including the other tests of `WorkflowAvailabilityTests` and the `NatsFaultInjection` collection.
- **FR-006**: The change MUST NOT repeat the scope of [Timing Margins for Intermittent Integration Tests](../20261019-221242-harden-integration-test-timing/spec.md).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The test passes in 100% of at least 20 consecutive runs alone and in at least 5 consecutive runs of its class under CPU load.
- **SC-002**: A probe that creates a run while the workflow is unresolvable, and a probe that creates two runs after recovery, each make the test fail on its assertions; the probes are removed afterward.
- **SC-003**: The code comment and commit message name the evidenced cause and carry the verification note; the change touches only the files that cause lives in (no file in this spec folder); no assertion is textually removed.
- **SC-004** (post-merge evidence, observed when #184 retries and not verified by a task of this spec): the verification of #184 reports no flaky `WorkflowAvailabilityTests` test.

## Assumptions

- **Cause**: The test belongs to the analysis workflow availability behavior (the merged durable analysis workflow specs under `specs/archive/`); it races real time and shared NATS state between the test, the consumer worker, and the cached availability status. The concrete cause is established during implementation (FR-001).
- **Dependencies**: [Member Invitations](../../20261017-202624-member-invitations/spec.md) (implementation #184, twin #154) depends on this spec. Related specs that cover other tests and must not be repeated: [Timing Margins for Intermittent Integration Tests](../20261019-221242-harden-integration-test-timing/spec.md), [Deterministic Fail-Once Retry of the Simulated Agent Workload](../20261020-224223-deterministic-simulated-workload-retry/spec.md), and [Analyst Manager Deterministic Tests](../../20261020-072206-deterministic-manager-tests/spec.md).
- **Out of scope**: Other flaky tests, the default values of the notification options, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
