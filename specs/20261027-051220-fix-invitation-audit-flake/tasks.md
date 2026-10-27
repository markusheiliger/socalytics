---

description: "Task list for removing the flakiness of the member invitation audit trail test"
---

# Tasks: Remove the Flakiness of the Member Invitation Audit Trail Test

**Input**: Design documents from `/specs/20261027-051220-fix-invitation-audit-flake/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md

**Tests**: No new test tasks. The feature fixes one existing test; its validation is the repeated runs, load runs, and probes of [quickstart.md](quickstart.md). A running Docker-compatible container runtime is required.

**Organization**: The spec has one user story (US1), so there is one story phase. The only changed file is `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationSafetyTests.cs`; the platform code and the shared harness stay unchanged unless the reproduction shows otherwise (plan.md, Structure Decision). Probe edits are temporary and are removed before the task that uses them ends.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies); this list has none
- **[Story]**: Which user story this task belongs to (US1)
- Include exact file paths in descriptions

## Phase 1: Setup

**Purpose**: Establish a working baseline before any change.

- [ ] T001 Restore and build the Integration test project with `dotnet restore src/platform/Tests/SocAlytics.Platform.Integration.Tests` and `dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-restore`, and confirm that `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded"` runs against the Docker runtime (no file change)

---

## Phase 2: Foundational

**Purpose**: Evidence of the cause (FR-001) that must exist before the fix.

- [ ] T002 Reproduce the extra `invitation.issued` event described in `specs/20261027-051220-fix-invitation-audit-flake/research.md` R1 with a test-only temporary probe in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationSafetyTests.cs` that, after creating `audit-c` and before revoking it, calls the delivery once exactly as the worker's iteration does (`await h.DeliverAsync(ct)`, or a scoped `EmailDeliveryProcessor.ProcessDueAsync`; no platform change and no retry loop in the shipped test), then reads `socalytics.security_audit_event` rows of `event_type = 'invitation.issued'` grouped by `resource_id`, and confirm that the third event belongs to the invitation of `audit-c` and carries the same `details->>'maskedAddress'` as `audit-a`; remove the probe afterward and keep the observed counts for the code comment of T003. If a different cause shows (a message of `audit-a` prepared twice), stop and report it so the plan is amended; if nothing reproduces, keep the fix of T003 and state in its comment that the failure was not reproduced and that #304 (`3` instead of `2`, filter by masked address only) supports it

**Checkpoint**: The cause is evidenced or the non-reproduction is recorded.

---

## Phase 3: User Story 1 - The audit trail test fails only when behavior is wrong (Priority: P1)

**Goal**: `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded` counts exactly the links issued to the first invitee, whatever the host's background delivery worker does with the other invitations, and still fails when behavior is wrong.

**Independent Test**: Run the test 30 times alone and 10 times under CPU load with exactly two `invitation.issued` events for the first invitee, and confirm that each of the three probes makes the unchanged assertions fail.

### Implementation for User Story 1

- [ ] T003 [US1] In `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationSafetyTests.cs`, in `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded`, create `audit-a` with an explicit address whose masked form differs from the one of every `EmailApiHarness.NewAddress()` address (`m***@e***.test`), for example `$"audit-a-{Guid.NewGuid():N}@invitee.test"` (masked `a***@i***.test`), passed as the `address` argument of `InviteAsync`; keep `audit-b` on the first invitee's address and `audit-c` on the generated address, and add a short comment next to the address naming the evidenced cause from T002 (generated addresses all mask to the same value, so the legitimate link the host's background delivery worker issues to `audit-c` was counted with the first invitee's) and, if applicable, that the failure was not reproduced. Do not remove, loosen, or reorder any assertion, add a retry, a fixed delay, or a longer timeout, and do not change `EmailApiHarness` or the platform code
- [ ] T004 [US1] Rebuild and run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~MemberInvitationSafetyTests"` once to confirm the class passes with the change of T003, then repeat the filtered `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded` run at least 30 times in a row alone (SC-001); every run passes (no file change)
- [ ] T005 [US1] Repeat the filtered `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded` run at least 10 times in a row while one busy-loop process per CPU core runs for the duration (stop the load processes afterward); every run passes with exactly two `invitation.issued` events for the first invitee (SC-001) (no file change)
- [ ] T006 [US1] Run the three probes of `quickstart.md` step 4 one after the other against `src/platform/SocAlytics.Platform.Infrastructure/Email/` code (a message prepared and audited by two overlapping delivery runs for the first invitee, probed by a temporary edit that records the `InvitationAuditEvents.Issued` event twice in `EmailDeliveryProcessor.PrepareInvitationAsync` for the first invitee; a link issued without its audit event, probed by a temporary edit in `EmailDeliveryProcessor.PrepareInvitationAsync` that skips the `InvitationAuditEvents.Issued` audit record for the first invitee, expecting the `invitation.issued` count assertion to fail; and a secret written into an audit event, probed by a temporary edit in `EmailDeliveryProcessor.PrepareInvitationAsync` that adds the invitee's normalized address to the `Details` of that audit event, expecting the 'audit contains a secret' assertion to fail), confirm each makes the test fail on its unchanged assertions without hanging (SC-002), and revert every probe with `git diff` showing no remaining change under `src/platform/SocAlytics.Platform.Infrastructure/`

**Checkpoint**: The test is deterministic and still detects wrong behavior.

---

## Phase 4: Polish & Cross-Cutting Concerns

- [ ] T007 Run the whole Integration test project once with `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build` and confirm the invitation, password recovery, and email delivery tests pass unchanged (SC-004), which is also the evidence that the existing guarantees of spec Acceptance Scenarios 2 and 3 are preserved (no file change)
- [ ] T008 Check with `git diff` that only `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationSafetyTests.cs` and files under `specs/20261027-051220-fix-invitation-audit-flake/` changed, that no assertion line is removed from the test (SC-003), that `specs/20261025-113505-deterministic-fact-payload-telemetry-test` is untouched (FR-007), and run `node .github/scripts/check-markdown.mjs` (no file change)

The plan's Architecture Updates section is `None` (the change alters no documented behavior), so there is no `docs/architecture/` task.

---

## Dependencies & Execution Order

- Phase 1 (T001) has no dependencies; Phase 2 (T002) needs T001 and blocks the fix.
- US1: T003 needs T002; T004 needs T003; T005 needs T004; T006 needs T005; T007 needs T006; T008 needs T007.
- All tasks run in sequence: they share the one test file or the one Docker runtime and the build output, so none is marked `[P]`.

## Parallel Example

None. Every task touches the same test file, the same build output, or the same container runtime.

## Implementation Strategy

- **MVP**: US1 is the whole feature; deliver T001 to T006, then the full-project and diff checks of Phase 4.
- Probes (T002, T006) are temporary and never remain in the final change.
- If T002 shows a cause other than the colliding masked address, stop and amend the plan through the Spec Kit flow instead of widening the change.
