---

description: "Task list for Deterministic Club Admin Role-Loss Notification Test"
---

# Tasks: Deterministic Club Admin Role-Loss Notification Test

**Input**: Design documents from `specs/20261020-081004-deterministic-role-loss-test/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The change is to a test; verification is running it (SC-001, SC-002, SC-004).

**Organization**: One user story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1)
- Every task names its exact file paths

---

## Phase 1: User Story 1 - The role-loss test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: `AClubAdminWhoLosesTheRoleBeforeTheSendReceivesNothing` passes reliably under load and still fails when the send ignores the lost role.

**Independent Test**: Run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~PendingRequestNotificationTests"` repeatedly.

### Hardening

- [ ] T001 [US1] Start the harness of `AClubAdminWhoLosesTheRoleBeforeTheSendReceivesNothing` with settings that disable the background workers (`Analysis:Workers:Enabled` = `false`, added to a copy of `RegistrySettings`) in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/PendingRequestNotificationTests.cs`, so that only the explicit delivery in `CountAsync` sends after the role is removed; leave the assertion, the other tests, and `RegistrySettings` unchanged (FR-001 to FR-004). Verified by running the test alone.

### Verification

- [ ] T002 [US1] Verify without committing any probe: run `PendingRequestNotificationTests.AClubAdminWhoLosesTheRoleBeforeTheSendReceivesNothing` at least 10 times alone and the whole class once, with CPU load applied during one run; temporarily make the send ignore the lost role and confirm the test fails, then remove the probe; confirm `git diff` shows only `PendingRequestNotificationTests.cs` changed and no assertion removed (SC-001, SC-002, SC-004)

---

## Dependencies & Execution Order

T002 depends on T001.

## Notes

- Never skip, loosen, or remove an assertion; never change production code or tests that are not named
