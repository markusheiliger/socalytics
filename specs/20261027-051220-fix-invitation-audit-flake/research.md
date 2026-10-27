# Research: Invitation Audit Trail Test Flake

## R1: What produces the third `invitation.issued` event?

- **Decision**: The extra event is the legitimate issuance of the link of the third invitation (`audit-c`), counted because its masked address equals the first invitee's. Confirm by reproduction before fixing: run the test with the host's first background delivery iteration delayed until after `audit-c` is created (test-only: after creating `audit-c` and before revoking it, call the delivery once exactly as the worker's iteration does, `await h.DeliverAsync(ct)` or a scoped `EmailDeliveryProcessor.ProcessDueAsync`), and read `socalytics.security_audit_event` for `invitation.issued` grouped by `resource_id`; expect one extra event whose resource is the invitation of `audit-c`. If the reproduction shows a different cause (a message of the first invitee prepared twice), the plan is amended; if nothing reproduces, only the fix below is applied, which the recorded #304 failure (`3` instead of `2`, filter by masked address only) supports, and the comment states that the failure was not reproduced.
- **Rationale**: `EmailAddress.Masked` keeps the first character of the local part and of the first domain label and the last label; `EmailApiHarness.NewAddress()` yields `member-<guid>@example.test` for every invitee, hence always `m***@e***.test`. The test creates `audit-c` and revokes it without delivering it, but the host's `EmailDeliveryWorker` also runs (`PollInterval` one second on the frozen `MutableTimeProvider`, so after its first iteration it never wakes again); that first iteration starts when the database is healthy and on a loaded runner can fall between the creation and the revoke of `audit-c`. A message is eligible while its invitation is pending, so its link is issued and audited, and the test's filter counts it. The test also passes on the rerun because the timing differs, matching the three observed failures that passed on their one rerun.
- **Alternatives considered**: (a) two runs preparing one message of the first invitee: rejected, see R2. (b) A lease that lapses: rejected, the lease is two minutes on a frozen clock and cannot lapse in the test. (c) Counting by invitation id instead of masked address: not chosen, it changes the assertion.

## R2: Can one outbox message be issued and audited twice?

- **Decision**: No platform change.
- **Rationale**: `EmailDeliveryProcessor.ProcessDueAsync` claims messages in one transaction with `FOR UPDATE SKIP LOCKED` and sets `state = 'sending'` with a lease; link issuance (`IssueLinkAsync`) and the audit record happen in that same transaction, which commits once. A second overlapping run skips locked or `sending` rows (`lease_until` is in the future on a frozen clock), so a message is prepared by at most one run per attempt. A retried send intentionally issues a fresh token per attempt, documented behavior that the test does not exercise (Mailpit accepts the message). A lease that lapses after preparation leaves a link issued and audited without a send, and the next poll issues and audits a fresh one; this is the documented per-attempt fresh-token behavior and cannot occur on the test's frozen clock.
- **Alternatives considered**: making issuance idempotent per message in the processor: rejected, it would change the documented per-attempt fresh token and is not supported by the evidence.

## R3: How is the collision removed?

- **Decision**: Give the first invitee (`audit-a`) an address whose masked form differs from that of the harness-generated addresses (for example local part and domain starting with different characters, such as `audit-a-<guid>@invitee.test`, masked `a***@i***.test`), keep every assertion unchanged, and keep `audit-c` on the generated address. Add a code comment naming the evidenced cause.
- **Rationale**: The assertion then counts exactly the links issued to the first invitee (invitation plus one resend), independent of when the background worker delivers the other invitations. No retry, delay, or timeout is involved, and the platform still fails the test when a link of the first invitee is issued twice, audited without issuance, or a secret is recorded (probes in the quickstart).
- **Assertion coverage**: The test asserts issuance only for the first invitee; double issuance for other invitees of this test is not asserted (the guarantee comes from the processor's claim transaction, R2) and no new assertion is added.
- **Alternatives considered**: disabling the background worker for the host (changes the platform composition the test covers and the shared harness); awaiting the background worker before revoking (adds timing dependence); skipping or deleting the assertion (forbidden by FR-004).

## R4: Do other tests share the collision?

- **Decision**: Out of scope unless they fail; only this test is changed (FR-006, spec out-of-scope). The shared harness is unchanged, so other invitation, password recovery, and email delivery tests are unaffected.

## Refined decision: verification-only tasks are retired

- **Decision**: T001, T002, and T004 to T008 are no longer tasks. The baseline (T001) and the reproduction probe (T002) become the first steps of T003, which keeps the one committed change; the repeated runs, load runs, three probes, whole-project run, and diff checks are made by the phase verification and `/speckit-converge`, with evidence in the pull request.
- **Rationale**: The implementation workflow lands a task only when its attempt changes files. T001 changes none, so both attempts ended with "the agent made no changes" although restore, build, and the filtered test passed (2 of 2); no further attempt can succeed. T002 and T004 to T008 are of the same kind.
- **Rejected alternatives**: a placeholder change to satisfy T001 (no purpose); more retries (they fail the same way); relaxing SC-001 to SC-004 (they stay as they are).
- **Evidence**: T001 attempts 1 and 2 both failed with "the agent made no changes" and summaries reporting a clean build and a passing run.

