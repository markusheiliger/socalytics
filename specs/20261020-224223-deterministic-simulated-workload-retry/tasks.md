---

description: "Task list for Deterministic Fail-Once Retry of the Simulated Agent Workload"
---

# Tasks: Deterministic Fail-Once Retry of the Simulated Agent Workload

**Input**: Design documents from `specs/20261020-224223-deterministic-simulated-workload-retry/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md)

**Tests**: The change is to a test and its support code; verification is running it (SC-001 to SC-003).

**Organization**: One user story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1)
- Every task names its exact file paths

---

## Phase 1: User Story 1 - The fail-once test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: `FailOnceSucceedsOnTheSecondAttempt` passes reliably under load and still fails when the retry behavior is broken.

**Independent Test**: Run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"` repeatedly.

### Establish the cause

- [x] T001 [US1] Reproduce the intermittent failure of `FailOnceSucceedsOnTheSecondAttempt` by running `SimulatedAgentWorkloadBehaviorTests` repeatedly alone and with CPU load, capturing the runner and platform logs of failing runs, and identify which race of `plan.md` § Design (or another) produces it; record the evidenced cause in the pull request description without committing probe code or logs (FR-001)

### Remove the cause

- [ ] T002 [US1] Remove the cause T001 evidenced, where it lives, in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs` and, only if the cause lives in the runner, `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs`, by controlling the racing input or waiting on the awaited condition, with no retry, skip, or longer fixed limit; keep every assertion and the behavior of the other tests that use the helper or runner (FR-002 to FR-005)

### Verification

- [ ] T003 [US1] Verify without committing any probe: run `FailOnceSucceedsOnTheSecondAttempt` at least 20 times alone and `SimulatedAgentWorkloadBehaviorTests` plus `SimulatedAgentWorkloadTests` at least 5 times with CPU load; temporarily make the second attempt fail and confirm the test fails on its assertions, then remove the probe; confirm `git diff` shows only the files of T002 changed and no assertion removed (SC-001 to SC-003)
- [ ] T004 [US1] Confirm `docs/architecture/` needs no change: the plan's Architecture Updates section is `None`, so read `docs/architecture/README.md` and the Agent Orchestration narrative, confirm that no statement mentions this test or the simulated workload timing, and change nothing unless T002 changed platform code, in which case correct every `docs/architecture/` statement it made false (SC-003)

---

## Dependencies & Execution Order

T002 depends on T001; T003 depends on T002; T004 depends on T002.

## Notes

- Never skip, loosen, or remove an assertion; never add a retry or a longer fixed limit as the fix
