---

description: "Task list for Deterministic Telemetry Capture in the Password Recovery Secret Test"
---

# Tasks: Deterministic Telemetry Capture in the Password Recovery Secret Test

**Input**: Design documents from `specs/20261019-102401-fix-recovery-telemetry-scope/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The change is a test; verification is running it (SC-001 to SC-004).

**Organization**: One user story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1)
- Every task names its exact file paths

---

## Phase 1: User Story 1 - The secret-leak test fails only when a secret leaks (Priority: P1) 🎯 MVP

**Goal**: The audit test records only telemetry of its own host and still fails when any checked secret leaks.

**Independent Test**: Run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~SelfServicePasswordRecoveryLimitsTests"` repeatedly and with the whole suite; probe a deliberate leak.

- [x] T001 [US1] Scope the telemetry capture of `AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/SelfServicePasswordRecoveryLimitsTests.cs` as plan.md Design describes: `ShouldListenTo` and `InstrumentPublished` accept only the host's and its clients' instrumentation scopes and never `Npgsql*`, and always filter process-wide framework telemetry by the host under test's address or its own requests' traces; leave the secret list, comparison, audit and log checks, counts, and failure messages unchanged (FR-001 to FR-005). Verified by the filtered test run passing.
- [x] T002 [US1] Verify the fix without committing any probe: run the test class at least 20 times alone and the whole `SocAlytics.Platform.Integration.Tests` project at least once with `dotnet test`; temporarily make the host under test put each checked secret into an activity tag and a metric tag and confirm the test fails with `telemetry contains a secret`, then remove the probe; also confirm with a temporary second host in the process that emits a checked secret in an ASP.NET Core activity tag and a metric tag that this telemetry does not enter the checked text, then remove it; confirm `git diff` shows only `SelfServicePasswordRecoveryLimitsTests.cs` changed and the assertions textually unchanged (SC-001 to SC-004)

---

## Dependencies & Execution Order

T002 depends on T001.

## Notes

- Never skip, loosen, or shorten any secret assertion; never change production code or other tests
