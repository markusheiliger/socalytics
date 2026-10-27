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

T001 is retired as a task (see Retired verification tasks); the baseline is the first step of T003.

---

## Phase 2: Foundational

**Purpose**: Evidence of the cause (FR-001) that must exist before the fix.

T002 is retired as a task (see Retired verification tasks); the reproduction is the first step of T003.

**Checkpoint**: The cause is evidenced or the non-reproduction is recorded, as the code comment of T003 (see the Phase 3 checkpoint).

---

## Phase 3: User Story 1 - The audit trail test fails only when behavior is wrong (Priority: P1)

**Goal**: `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded` counts exactly the links issued to the first invitee, whatever the host's background delivery worker does with the other invitations, and still fails when behavior is wrong.

**Independent Test**: Run the test 30 times alone and 10 times under CPU load with exactly two `invitation.issued` events for the first invitee, and confirm that each of the three probes makes the unchanged assertions fail.

### Implementation for User Story 1

- [ ] T003 [US1] First restore, build, and run the filtered `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded` test against the Docker runtime (`dotnet restore`, `dotnet build --no-restore`, `dotnet test --no-build --filter "FullyQualifiedName~AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded"`), then reproduce the extra `invitation.issued` event of `research.md` R1 with a temporary test-only probe in the same test file (after creating `audit-c` and before revoking it, call `await h.DeliverAsync(ct)` once, as the worker's iteration does, and read the `invitation.issued` rows of `socalytics.security_audit_event` grouped by `resource_id`; the third event belongs to the invitation of `audit-c` and carries the masked address of `audit-a`), remove the probe, and if a different cause shows (a message of `audit-a` prepared twice) stop and report it; then, in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationSafetyTests.cs`, in `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded`, create `audit-a` with an explicit address whose masked form differs from the one of every `EmailApiHarness.NewAddress()` address (`m***@e***.test`), for example `$"audit-a-{Guid.NewGuid():N}@invitee.test"` (masked `a***@i***.test`), passed as the `address` argument of `InviteAsync`; keep `audit-b` on the first invitee's address and `audit-c` on the generated address, and add a short comment next to the address naming the evidenced cause of the probe (generated addresses all mask to the same value, so the legitimate link the host's background delivery worker issues to `audit-c` was counted with the first invitee's) and, if applicable, that the failure was not reproduced. Do not remove, loosen, or reorder any assertion, add a retry, a fixed delay, or a longer timeout, and do not change `EmailApiHarness` or the platform code

**Checkpoint**: The test is deterministic and still detects wrong behavior. The evidence for the cause (FR-001) is the code comment T003 adds next to the address (the evidenced cause, or that the failure was not reproduced); the committed diff contains no probe.

---

## Phase 4: Polish & Cross-Cutting Concerns

No tasks remain: T007 and T008 are retired (see Retired verification tasks).

The plan's Architecture Updates section is `None` (the change alters no documented behavior), so there is no `docs/architecture/` task.

## Retired verification tasks

T001, T002, and T004 to T008 were tasks whose only outcome is evidence and no committed change. The implementation workflow accepts a task only when its attempt changes files and fails one that changes nothing ("the agent made no changes"), so both attempts at T001 ended in the attempt limit although the baseline passed (restore and build clean, the filtered test passed 2 of 2). They are no longer tasks and their IDs are not reused. T001 and T002 move into the first steps of T003; the other checks are made by the phase verification and `/speckit-converge`:

- **Convergence checks** (the instruction to `/speckit-converge`, which performs them, reports each result in the pull request, and appends an unchecked task for every check that fails or that it did not run, so the evidence is either produced or visibly missing as an open task): SC-001 (30 runs alone and 10 runs under CPU load, each with exactly two `invitation.issued` events for the first invitee) and SC-002 (the three probes of `quickstart.md` step 4 make the unchanged assertions fail without hanging), following `quickstart.md`; probes stay temporary and are reverted.
- SC-003 and FR-007 (no assertion line removed from the test, the changed files limited to the test file and this spec folder, the other spec folder untouched) and the Markdown check are made by `/speckit-converge` and the phase and finalize verification.
- SC-004 (the invitation, password recovery, and email delivery tests pass unchanged, so Acceptance Scenarios 2 and 3 are preserved) is the phase verification of the whole Integration test project.

---

## Dependencies & Execution Order

- T003 is the only task and has no dependencies; the retired tasks keep their IDs and are not reused.

## Parallel Example

None. The only task is T003.

## Implementation Strategy

- **MVP**: US1 is the whole feature; deliver T003, then the convergence makes the repeated-run, load, and probe checks.
- Probes are temporary and never remain in the final change.
- If the probe of T003 shows a cause other than the colliding masked address, stop and amend the plan through the Spec Kit flow instead of widening the change.
