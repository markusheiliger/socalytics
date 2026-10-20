---

description: "Task list for Bounded Wait for the Simulated Agent Workload Advice Test"
---

# Tasks: Bounded Wait for the Simulated Agent Workload Advice Test

**Input**: Design documents from `specs/20261020-144627-deterministic-integration-races/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The change is to a test; verification is running it (SC-001 to SC-003).

**Organization**: One user story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1)
- Every task names its exact file paths

---

## Phase 1: User Story 1 - The advice test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: `RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice` passes reliably under load and still fails when the invocation never completes.

**Independent Test**: Run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~SimulatedAgentWorkloadTests"` repeatedly.

### Hardening

- [x] T001 [US1] Replace the 30-second runner cancellation and polling deadline of `RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice` with one generous limit (120 seconds) for the polling deadline and the same limit plus a small margin (for example 5 seconds) for the runner's `CancelAfter`, in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadTests.cs`, so the runner outlives the wait and the test returns as soon as the advice is readable; leave every assertion and the other tests unchanged (FR-001 to FR-003)

### Verification

- [ ] T002 [US1] Verify without committing any probe: run `SimulatedAgentWorkloadTests.RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice` at least 10 times alone and once with CPU load applied; temporarily keep the runner from completing the invocation and confirm the test fails on the advice assertion within twice the limit, then remove the probe; confirm `git diff` shows only `SimulatedAgentWorkloadTests.cs` changed and no assertion removed (SC-001 to SC-003)
- [ ] T003 [US1] Update `docs/architecture/` only if a statement it makes became false; the plan's Architecture Updates section is `None`, so confirm by reading `docs/architecture/README.md` that no architecture statement mentions this test and change nothing (SC-003)

---

## Dependencies & Execution Order

T002 depends on T001.

## Notes

- Never skip, loosen, or remove an assertion; never change production code or tests that are not named
