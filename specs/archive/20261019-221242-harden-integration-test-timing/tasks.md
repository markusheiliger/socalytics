---

description: "Task list for Timing Margins for Intermittent Integration Tests"
---

# Tasks: Timing Margins for Intermittent Integration Tests

**Input**: Design documents from `specs/archive/20261019-221242-harden-integration-test-timing/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The change is to tests; verification is running them (SC-001 to SC-004).

**Organization**: One user story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1)
- Every task names its exact file paths

---

## Phase 1: User Story 1 - Integration tests fail only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: The five named test classes pass reliably under load and still fail when the behavior they check is wrong.

**Independent Test**: Run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~<Class>"` repeatedly and with the whole suite.

### Hardening (parallel, disjoint files)

- [x] T001 [P] [US1] Give `SessionLifetime` ample margin and drive the expiry with the factory's fake clock instead of `Task.Delay` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/SecretHygieneTests.cs`, keeping every secret and expiry assertion (FR-001, FR-004). Advance the clock only after all steps that need valid time (admin session, presigned grants, re-grant), immediately before the sweep. Verified by running `SecretHygieneTests` alone.
- [x] T002 [P] [US1] Raise NATS client timeouts and bound-retry the timed-out call only in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Messaging/ManagerConsumerTopologyTests.cs`, leaving assertions unchanged (FR-002, FR-004). Verified by running `ManagerConsumerTopologyTests` alone.
- [x] T003 [P] [US1] Raise NATS client timeouts, bound-retry the timed-out call, and raise the condition-poll limits in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/TransportRecoveryTests.cs`, leaving assertions unchanged (FR-002, FR-004). Verified by running `TransportRecoveryTests` alone.
- [x] T004 [P] [US1] Raise the overall wait limit of `WaitForReadyWorkAsync` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PayloadConformanceTests.cs`, still returning as soon as the condition holds (FR-003, FR-004). Verified by running `PayloadConformanceTests` alone.
- [x] T005 [P] [US1] Raise the run and deadline limits of `RunToEndAsync` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs`, still returning as soon as the invocation ends (FR-003, FR-004). The raised limits apply to every test in the class using the helper; their assertions are unchanged. Verified by running `SimulatedAgentWorkloadBehaviorTests` alone.

### Verification

- [x] T006 [US1] Verify without committing any probe: run each of the five classes at least 10 times alone and the whole `SocAlytics.Platform.Integration.Tests` project at least once with `dotnet test`, with CPU load applied during one run; temporarily break the expiry sweep, ready-work publication, and attempt expiry behavior and confirm the matching tests still fail, then remove the probes; confirm `git diff` shows only the five test files (and an optional support helper) changed and no assertion removed (SC-001, SC-002, SC-004)

---

## Dependencies & Execution Order

T006 depends on T001 to T005.

## Notes

- Never skip, loosen, or remove an assertion; never change production code or tests that are not named

## Phase 2: Convergence

- [x] T007 [US1] In `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/SecretHygieneTests.cs`, after `ExpireUploadSessionsHandler.HandleAsync` runs, add an assertion that the pending session (`pendingId`) has `state = 'expired'` and a non-null `expired_at` in `socalytics.recording_upload_sessions`, and that the first (completed) session is not expired, so a sweep that stops expiring sessions fails the test; change no other assertion and verify by running `SecretHygieneTests` alone and once with the expiry sweep temporarily disabled (probe removed afterward) per FR-001, US1/AC1, SC-002 (partial)
