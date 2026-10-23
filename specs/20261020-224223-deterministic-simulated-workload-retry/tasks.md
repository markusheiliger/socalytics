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

- [x] T002 [US1] Remove the cause T001 evidenced, where it lives, in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs` and, only if the cause lives in the runner, `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs`, by controlling the racing input or waiting on the awaited condition, with no retry, skip, or longer fixed limit; keep every assertion and the behavior of the other tests that use the helper or runner (FR-002 to FR-005)

### Remove the cause of the stalled claim

- [ ] T005 [US1] Establish why a submitted invocation stays `accepted`/`queued` with 0 attempts, 0 checkpoints, and an empty runner log for the whole wait when `SimulatedAgentWorkloadBehaviorTests` plus `SimulatedAgentWorkloadTests` run with every core busy (four CPU burners on four cores failed 4 of 5 runs, two burners 1 of 5; `FailOnceSucceedsOnTheSecondAttempt` and `RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice` fail together): using the failure message that `RunToEndAsync` now carries and temporary, uncommitted probes, determine at which hop it stops (the outbox row of the ready notification is not published, the notification is not in the JetStream stream or consumer, the runner's fetch or queue-credential step does not return or never reaches the claim, or the claim does not answer) and record the evidenced cause and the runs in the pull request description without committing probe code or logs; also examine the single fixture-startup failure of 1 ms seen in 46 solo runs (FR-001)
- [ ] T006 [US1] Remove the cause T005 evidenced, where it lives, in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs`, `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs`, or, only when the evidence puts it there, the platform code it covers, by controlling the racing input or waiting on the awaited condition, with no retry, skip, or longer fixed limit; keep every assertion, the other tests that use the helper or runner, and the documented claim, fencing, lease, retry, and outbox semantics (FR-002 to FR-005)

### Verification

- [ ] T003 [US1] Verify without committing any probe: run `FailOnceSucceedsOnTheSecondAttempt` at least 20 times alone and `SimulatedAgentWorkloadBehaviorTests` plus `SimulatedAgentWorkloadTests` at least 5 times with CPU load; temporarily make the second attempt fail and confirm the test fails on its assertions, then remove the probe; confirm `git diff` shows only the files of T002 and T006 changed and no assertion removed (SC-001 to SC-003)
- [ ] T004 [US1] Confirm `docs/architecture/` needs no change: the plan's Architecture Updates section is `None`, so read `docs/architecture/README.md` and the Agent Orchestration narrative, confirm that no statement mentions this test or the simulated workload timing, and change nothing unless T002 or T006 changed platform code, in which case correct every `docs/architecture/` statement it made false (SC-003)

---

## Dependencies & Execution Order

T002 depends on T001; T005 depends on T002 (the failure after T002 is the evidence that the first fix did not remove the stalled claim); T006 depends on T005; T003 depends on T006; T004 depends on T006. T005 and T006 have the next free IDs but run before T003.

## Notes

- Never skip, loosen, or remove an assertion; never add a retry or a longer fixed limit as the fix
