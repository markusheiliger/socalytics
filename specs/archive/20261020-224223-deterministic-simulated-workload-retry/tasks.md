---

description: "Task list for Deterministic Fail-Once Retry of the Simulated Agent Workload"
---

# Tasks: Deterministic Fail-Once Retry of the Simulated Agent Workload

**Input**: Design documents from `specs/archive/20261020-224223-deterministic-simulated-workload-retry/`

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

### Remove the cause of the stalled claim (a notification stolen through the shared queue)

- [x] T005 [US1] Provide a per-test work queue in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Support/AgentOrchestrationFixture.cs`: the two earlier attempts at the investigation evidenced that the stall is a stolen ready notification (`NatsContainerFixture` is an assembly fixture, so every test shares the stream `AGENT_WORK` and the durable consumer `agent-workload`; a runner of another concurrently running test fetches the notification, claims it against its own database, gets `404 not-found`, and acknowledges it), so `StartAsync` sets `Messaging:Nats:AgentWorkStream`, `Messaging:Nats:AgentWorkSubject` (a unique, non-overlapping subject such as `agents.invocations.ready.<unique>`), and `Messaging:Nats:AgentWorkConsumer` to names unique per fixture instance (before the `settings` callback, so a test can still override them) and exposes them as `AgentWorkStream` and `AgentWorkConsumer` properties; keep every other setting, test, and the default names used elsewhere unchanged (FR-002 to FR-005)
- [x] T006 [US1] Make the runner of `FailOnceSucceedsOnTheSecondAttempt` and every other runner-driving test use that queue: in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs` and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadTests.cs`, set `Stream` and `Consumer` of the `SimulatedAgentWorkloadOptions` to the fixture's `AgentWorkStream` and `AgentWorkConsumer`, with no retry, skip, or longer fixed limit; keep every assertion and the documented claim, fencing, lease, retry, and outbox semantics; platform code and the runner change only if a run proves the isolation insufficient (FR-002 to FR-005)

### Verification (not tasks)

T003 and T004 were tasks whose only outcome is evidence and no committed change. The implementation workflow accepts a task only when it changes files and fails one that changes nothing ("the agent made no changes"), so both attempts at T003 ended in the attempt limit although every run passed. They are no longer tasks; the checks below are made by the phase verification and the convergence, and their evidence is the one T003's two attempts already recorded in the pull request comments (`FailOnceSucceedsOnTheSecondAttempt` 20 of 20 alone; the two classes 5 of 5 under four CPU burners; a probe failing the second attempt makes the test fail on its `attempts` assertion; no assertion removed; the changed source files are the four of `plan.md`).

- SC-002 (the probe that makes the second attempt fail) is satisfied by the recorded evidence of T003's two attempts, in which the probe made the test fail on its `attempts` assertion; as the substitute check, `/speckit-converge` confirms that no assertion was removed or weakened, so the test still fails when the second attempt fails.
- SC-001 to SC-003: the phase verification runs the Integration suite, and `/speckit-converge` compares the changed files and the removed assertions with `plan.md` § Source Code and FR-003.
- SC-003 and the plan's Architecture Updates (`None`): `/speckit-gha-drift` checks `docs/architecture/` against the implementation before the convergence; nothing there mentions this test or the simulated workload timing.

---

## Dependencies & Execution Order

T002 depends on T001; T005 depends on T002 (the failure after T002 is the evidence that the first fix did not remove the stalled claim; the cause was evidenced by the attempts at the investigation that T005 formerly asked for); T006 depends on T005 (it uses the properties T005 adds). T005 and T006 have the next free IDs; T003 and T004 are retired as tasks (see Verification above) and their IDs are not reused.

## Notes

- Never skip, loosen, or remove an assertion; never add a retry or a longer fixed limit as the fix
