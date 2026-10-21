# Feature Specification: Deterministic Club Admin Role-Loss Notification Test

**Feature Branch**: `20261020-081004-deterministic-role-loss-test`

**Created**: 2026-10-20

**Status**: Draft

**Fix for**: #184 (implementation of `specs/20261017-202624-member-invitations`, twin #154)

**Input**: User description: "PendingRequestNotificationTests.AClubAdminWhoLosesTheRoleBeforeTheSendReceivesNothing in SocAlytics.Platform.Integration.Tests/Notifications fails intermittently on loaded runners: it deletes the Club Admin's role right after SubmitAsync and then asserts no message arrived, racing the email delivery worker (expected 0, was 1). Make the test deterministic without weakening its assertion."

## Problem

The test submits an Analyst Manager registration, deletes the Club Admin's role assignment, and asserts that the former Club Admin receives no message. The platform queues the message in the submission transaction and the background email delivery worker, which the test harness polls every second, can send it between the submission and the role deletion. Then the message is already delivered and the test sees one message instead of none (run 39386115269 of #184: expected 0, was 1). The platform behavior (eligibility is checked at send time) is correct; the test depends on winning a race against a worker it does not control.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The role-loss test fails only when behavior is wrong (Priority: P1)

A maintainer running the Integration suite, or the verification of any implementation, sees `AClubAdminWhoLosesTheRoleBeforeTheSendReceivesNothing` pass whenever the product checks eligibility at send time, however loaded the runner is.

**Why this priority**: The full verification of #184 stopped because of this test, and a retry would hit the same race again.

**Independent Test**: Run the test repeatedly, alone and with the Integration suite under CPU load, and confirm every run passes. Then make the send ignore the role (probe) and confirm the test fails.

**Acceptance Scenarios**:

1. **Given** a loaded runner, **When** the test submits a registration, removes the Club Admin's role, and then delivers, **Then** no message reaches the former Club Admin, in every run.
2. **Given** the platform sent to recipients without re-checking the role, **When** the test runs, **Then** it fails on the same assertion as today.

### Edge Cases

- The other tests of `PendingRequestNotificationTests` keep delivering explicitly and keep their behavior.
- The test must still deliver at least once after the role removal, so a wrong send path is observed.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The test MUST control when the outbox message is sent, so that the role removal always happens before any delivery, instead of racing the background email delivery worker.
- **FR-002**: The test MUST keep its assertion (no message for the former Club Admin after delivery ran) and its scenario (the recipient loses the role between submission and send).
- **FR-003**: No assertion, expected value, or other test MAY be removed, loosened, or skipped; the change only affects this one test.
- **FR-004**: The change MUST stay inside the Integration test project (`PendingRequestNotificationTests.cs`, and shared test support only if a helper is needed); no production code, product option default, or other test changes.

### Key Entities

- **Delivery race**: The window between queueing a message and the test's next step in which the background worker may send it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The test passes in 100% of at least 10 consecutive runs alone, and the whole `PendingRequestNotificationTests` class passes, with CPU load applied during at least one run.
- **SC-002**: A deliberate probe that makes the send ignore the lost role makes the test fail (verified during implementation, probe removed afterward).
- **SC-003** (post-merge evidence, observed when #184 retries and not verified by a task of this spec): The phase verification of #184 passes `PendingRequestNotificationTests`.
- **SC-004**: No file outside the Integration test project and this spec folder changes, and no assertion is textually removed.

## Assumptions

- **Cause**: The test belongs to [Email Notifications](../20261013-140140-email-notifications/spec.md) and races the hosted email delivery worker (diagnosis of #184, round 8). The defect is in the test, not in the platform or the member invitations.
- **Smallest fix**: Start the harness for this test with the background workers disabled (`Analysis:Workers:Enabled` = `false`), so that only the test's explicit `DeliverAsync` sends. The submission does not need any worker. The plan may pick another equivalent mechanism; the requirements decide acceptance.
- **Dependencies**: The owning feature is Email Notifications (archived). [Member Invitations](../../20261017-202624-member-invitations/spec.md), whose implementation is #184, depends on this spec and waits for it, together with [Timing Margins for Intermittent Integration Tests](../20261019-221242-harden-integration-test-timing/spec.md), which covers `SecretHygieneTests`; this spec does not repeat that fix.
- **Out of scope**: Production code and defaults, `SecretHygieneTests`, other flaky tests, and skipping or retrying whole tests.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
