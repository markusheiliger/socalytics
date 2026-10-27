# Feature Specification: Remove the Flakiness of the Member Invitation Audit Trail Test

**Feature Branch**: `20261027-051220-fix-invitation-audit-flake`

**Created**: 2026-10-27

**Status**: Draft

**Fix for**: #184, #304, and #345 (verifications that hit the flake); the stopped spec `specs/20261025-113505-deterministic-fact-payload-telemetry-test` depends on this spec.

**Input**: User description: "Make the integration test `SocAlytics.Platform.Integration.Tests.IdentityAccess.MemberInvitationSafetyTests.AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded` deterministic. It failed and then passed on its one rerun in the verifications of three implementation pull requests (#184, #304, #345); in #304 the assertion `(await EventsAsync(h, \"invitation.issued\", ct, masked: addressA!.Masked)).ShouldBe(2)` saw 3 events instead of 2, so one invitation link was issued, and its `invitation.issued` audit event recorded, one time more than the test's steps (one invitation plus one resend) account for. Find and remove the root cause of this extra, timing-dependent issuance (for example a delivery run triggered explicitly by the test racing with the host's background email delivery worker, a lease that expires and lets the same outbox message be prepared and audited again, or an ordering gap between the transactional claim, the audit record, and the send), in the test harness or in the code it covers, so that exactly one `invitation.issued` event is recorded per issued link on every run. Do not weaken, skip, or delete the test or any assertion, and do not add retries or longer timeouts as the fix. Verify with repeated runs."

## Problem

The test walks one member invitation through its life: a Club Admin creates an invitation, resends it, the first link is refused as superseded, a weak password is refused, the second link is accepted, a reuse of it is refused, a duplicate invitation is refused, and a third invitation is revoked and then refused twice. It then checks that the security audit trail holds exactly one event per step and that no secret is recorded.

One issued link is recorded as one `invitation.issued` audit event. The test's steps issue exactly two links to the first invitee (the invitation and one resend), yet in the verification of #304 the trail held three. The test therefore passes when the platform behaves correctly and fails when more invitation.issued events carry the first invitee's masked address than the steps account for (a link issued once more for the same invitation, or a link issued to another invitee whose address masks to the same value). It failed and passed on its single rerun in three unrelated implementations, so the cause is timing, order, or shared state that neither the test nor the platform controls, and it is not caused by those features. It holds back the stopped implementation of `specs/20261025-113505-deterministic-fact-payload-telemetry-test`, whose verification keeps hitting it.

Candidate causes the implementation must examine by reproduction and remove only where the evidence supports them:

- two email delivery runs that overlap for one test host: the test's explicit delivery run and the host's own background delivery worker (which polls on a short interval but runs on a frozen test clock), or two workers of one host, each preparing the same queued invitation message;
- a delivery lease that lapses while a message is prepared or sent, so a later run prepares and audits the same outbox message again;
- an ordering gap between the transactional claim of a message, the issuing of its link, the audit record, the commit, and the send, so that a message can be prepared more than once, or a link issued and audited without a message that is sent exactly once;
- a message of an earlier step (the invitation or the resend) that is still queued or retried when the next step queues its own, so a step's link is issued by more than one run;
- load on the runner that widens any of these windows;
- the test counts `invitation.issued` by masked address, and every harness-generated address masks to the same value, so a legitimate link issued by the host's background delivery worker to another invitee (audit-c) is counted with the first invitee's.

The implementation first reproduces the extra issuance, identifies the cause that produces it, and removes it.

If the extra issuance cannot be reproduced, the implementation hardens only the candidate causes that the recorded failure in #304 and the code paths named above support, records that evidence in a code comment, and states that the failure was not reproduced; it does not harden candidates without such support.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The audit trail test fails only when behavior is wrong (Priority: P1)

A maintainer running the integration tests, or the verification of any implementation pull request, sees the invitation audit trail test pass whenever the platform records one `invitation.issued` event per issued link and one event per other step, however loaded the runner is and however the delivery runs are timed, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, which then passed only on a rerun, and the stopped implementation that depends on this spec keeps being held back by it; the person who asked wants the cause removed, not rerun.

**Independent Test**: Run the test repeatedly alone, in parallel with other tests of its class, and under CPU load; every run passes with exactly two `invitation.issued` events for the first invitee. Then break the behavior with a probe (for example a message that is prepared twice, or a link issued without an audit event) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** one invitation and one resend for the same invitee, **When** delivery runs in any timing, including a delivery run the test triggers while the host's own delivery is active, **Then** exactly two links are issued and exactly two events are recorded for that invitee's address.
2. **Given** a queued invitation message, **When** more than one delivery run could prepare it, **Then** at most one run issues its link, records its audit event, and sends its email (an existing guarantee that this spec preserves and does not change).
3. **Given** a delivery lease that runs out while a message is being processed, **When** a later run starts, **Then** the message is not issued and audited a second time, and a link is never audited without having been issued (an existing guarantee that this spec preserves and does not change).
4. **Given** a loaded runner, **When** the test runs, **Then** it passes without a retry, a fixed delay, or a longer timeout, and continues each step only once the awaited state exists.
5. **Given** the behavior is wrong (a link issued twice for one message, a missing audit event, or a secret in the audit trail), **When** the test ends, **Then** its unchanged assertions fail and it does not hang.
6. **Given** the cause is in code shared with other tests or with the platform, **When** the other invitation, password recovery, and email delivery tests run, **Then** they keep their assertions and pass at least as reliably.

### Edge Cases

- A resend issued while the first message is still queued or in flight MUST yield one link and one event per message, not one per delivery run.
- A message that is discarded because its invitation is no longer open MUST NOT issue a link or record `invitation.issued`.
- A send failure that is retried MUST keep the documented behavior: each attempt may issue a fresh token so that an earlier link stops working, and the audit trail and the documented one-event-per-issued-link rule MUST stay consistent for it.
- The test's explicit delivery run MUST NOT depend on how long the host's background worker takes, and MUST NOT make the host's background worker skip work it is meant to do.
- A fix in the platform MUST NOT change the documented results, concurrency, audit event names and details, or delivery guarantees of member invitations and email delivery.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the extra `invitation.issued` event with evidence (repeated runs alone, in parallel, and under CPU load, and the audit and delivery records of a failing run, or the recorded failure in #304 if it cannot be reproduced) and record it in a code comment next to the fix; the commit message, written by whoever commits, repeats it. If the failure cannot be reproduced, the implementation MUST harden only the candidate causes that the recorded failure and the code paths support, record that evidence, and state that the failure was not reproduced.
- **FR-002**: Exactly one `invitation.issued` audit event MUST be recorded per issued invitation link on every run, and one link MUST be issued per prepared outbox message regardless of how many delivery runs overlap or how long a lease lasts (an existing guarantee that this spec preserves and does not change).
- **FR-003**: The cause MUST be removed where it lives (the test harness, the test, or the delivery and invitation code it covers) by controlling the racing input, serializing or de-duplicating the preparation of one message, or making issuance and its audit record idempotent per outbox message, or making the test's own data unambiguous, for example a masked address unique per counted invitee, not by retrying, skipping, adding fixed delays, or lengthening a timeout.
- **FR-004**: Every assertion of the test MUST remain, and no assertion, expected value, or step MAY be removed, loosened, or skipped.
- **FR-005**: The documented behavior of member invitations and email delivery in `docs/architecture/` MUST NOT change, including audit event names and details, link single-use and supersession semantics, and the rule that no secret is recorded.
- **FR-006**: A change to shared harness or processor code MUST keep every other test that uses it behaviorally unchanged and deterministic.
- **FR-007**: The change MUST NOT touch the stopped spec `specs/20261025-113505-deterministic-fact-payload-telemetry-test` or its test, and MUST NOT depend on it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001** (convergence check, reported in the pull request): The test passes in 100% of at least 30 consecutive runs alone and in at least 10 consecutive runs under CPU load (one busy-loop process per core for the duration), each with exactly two `invitation.issued` events for the first invitee.
- **SC-002** (convergence check, reported in the pull request): Each of three probes (a message prepared and audited by two overlapping delivery runs, a link issued without its audit event, and a secret written into an audit event) makes the test fail on its assertions; the probes are removed afterward.
- **SC-003**: The code comment next to the fix names the evidenced cause, the change touches only the files the cause lives in, and no assertion is textually removed.
- **SC-004**: The other tests of the invitation, password recovery, and email delivery areas pass unchanged in one full run of the Integration test project.
- **SC-005** (post-merge evidence, not verified by a task of this spec): the verification of the stopped implementation of `specs/20261025-113505-deterministic-fact-payload-telemetry-test` reports no flaky `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded`.

## Assumptions

- **Cause**: The member invitations feature (Identity Access functional area) is merged and behaves correctly when the test passes. The cause is unknown; every candidate above is a hypothesis to confirm or reject by reproduction (FR-001).
- **Scope**: The test lives in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationSafetyTests.cs`; the code it covers is the invitation audit events and the invitation branch of the email delivery processor in `src/platform/SocAlytics.Platform.Infrastructure/Email/`, and its harness is the integration-test email API harness.
- **Dependencies**: The stopped spec `specs/20261025-113505-deterministic-fact-payload-telemetry-test` depends on this spec.
- **Assertion coverage**: The test asserts issuance only for the first invitee; double issuance for other invitees of this test is not asserted (the guarantee comes from the processor's claim transaction, R2) and no new assertion is added.
- **Out of scope**: Other flaky tests, production email relay values, and production readiness.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
