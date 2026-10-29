# Tasks: Deterministic Retryable Failure of the Simulated Agent Workload

**Input**: Design documents from `/specs/20261027-135353-deterministic-fail-retryable-test/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md

**Tests**: No new test tasks except a focused test for a platform fix, which the plan requires only if the evidence shows a platform defect (added by T007 together with the fix). The existing test `FailRetryableEndsWithAttemptsExhausted` is the test under repair; its assertions stay unchanged.

**Organization**: One user story (US1). The cause is deliberately not decided in advance (FR-001): the tasks establish it with evidence, then fix it where it lives.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel; consecutive `[P]` tasks under one heading run in parallel, each from the same commit (this spec has none)
- **[Story]**: Which user story this task belongs to (US1)
- Paths are relative to the repository root

## Phase 1: Setup

The environment setup and verification already restore, build, and test the solution, so this phase has no task of its own.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Temporary failure capture for diagnosing the intermittent failure. Report each task's results in its task summary; record the established cause in T007's task summary, the code comment next to the fix, and the implementation pull request comments.

**⚠️ CRITICAL**: The capture added here only reports on failure and MUST be removed again in T007; no probe or capture remains in the final change.

- [x] T003 Add failure capture to `RunToEndAsync` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs` that, when the run fails, also dumps the `agent_attempts` rows (`attempt_number`, `state`, `end_reason`, `lease_expires_at`), the `agent_lineage_entries` kinds in order, the outbox rows of the invocation, and the `AGENT_WORK` consumer info, next to the existing last-read, attempt, and runner-log message; report the baseline (one run of `FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests` with `--no-build`, all four tests passing) in the task summary

---

## Phase 3: User Story 1 - The retryable-failure test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: `FailRetryableEndsWithAttemptsExhausted` passes whenever three retryable failures exhaust the attempts of an invocation, however loaded the runner, NATS, and the host are, and fails only when that behavior is broken.

**Independent Test**: The test passes in 100% of at least 20 consecutive runs alone and at least 5 consecutive runs of its class without synthetic CPU load (SC-001); CPU-loaded runs are optional supplemental evidence. A probe that stops redelivery after the second attempt and a probe that makes the third attempt succeed each make it fail within the existing limits (SC-002, manual local evidence run by a person, not verified by a task).

### Establish the cause (FR-001)

- [x] T004 [US1] Run `FullyQualifiedName~FailRetryableEndsWithAttemptsExhausted` 20 times in a row alone against `src/platform/Tests/SocAlytics.Platform.Integration.Tests` (one shell loop of `dotnet test --no-build --filter …` inside this task; SC-001 is local evidence, and in CI the repeat count may be reduced to what fits the job timeout) and report every failure with its captured evidence, or the pass count, in the task summary — verified without changes by the assessed verification at fd5e5345 (no change by this task)
- [x] T005 [US1] Run the class filter `FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests` at least 5 times in a row without synthetic CPU load against `src/platform/Tests/SocAlytics.Platform.Integration.Tests` (one shell loop of `dotnet test --no-build --filter …` inside this task; SC-001 is local evidence, and in CI the repeat count may be reduced to what fits the job timeout); CPU-loaded runs may be reported as optional supplemental evidence. Report every failure with its captured evidence, or the pass count, in the task summary — already done by a push to the implementation branch (verified; no change by this task)

### Remove the cause where it lives (FR-002 to FR-005)

- [x] T007 [US1] Classify the cause against the candidate table in `specs/20261027-135353-deterministic-fail-retryable-test/research.md` (candidates 1 to 5) from the evidence of T004 and T005 and from `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs`, `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Support/AgentOrchestrationFixture.cs`, and `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/FailAgentAttempt.cs`, `ClaimAgentInvocation.cs`, and `MonitorAgentAttempts.cs`; when no captured run failed, prove the suspected candidate by temporarily delaying the suspected boundary in the working tree, running the test, and reverting the delay before the task ends; report the classified cause (candidate number, where it lives, and the evidence) in the task summary and the implementation pull request comments. Then fix it in exactly the file where it lives, by awaiting an observable condition or controlling the racing input, with a code comment next to the fix naming the established cause and its evidence (as `RunToEndAsync` already does for fail-once and stall). That file is one of: the test or `RunToEndAsync` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs`, `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Support/AgentOrchestrationFixture.cs`, or `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs`. Only if the evidence shows a platform defect, fix it in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/` together with a focused test for the corrected behavior under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/`, keeping the Agent Orchestration semantics documented in `docs/architecture/` unchanged. Otherwise note in the task summary that no platform change was needed. Remove the failure capture of T003 in the same task, keeping only the fix and its comment. Add no retry, fixed sleep, skip, or longer limit (the 3 minute bounded wait, 2 s lease, 2 s fetch expiry, and 200 ms retry delay stay). The assertions `failed`, `attempts-exhausted`, and exactly 3 attempts stay, and no exact count is replaced by a wait until counts match. The helper behavior for `[simulate:fail-once]`, `[simulate:fail-permanent]`, and `[simulate:stall]` stays unchanged and the changes of the fail-once and stall specs are not repeated.

### Verify the fix (SC-001, SC-002)

- [x] T009 [US1] Repeat the evidence runs after the fix, each as one shell loop of `dotnet test --no-build --filter …` inside this task: `FullyQualifiedName~FailRetryableEndsWithAttemptsExhausted` 20 consecutive times alone and the class filter `FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests` at least 5 consecutive times without synthetic CPU load (SC-001 is local evidence; in CI the repeat count may be reduced to what fits the job timeout), with every run passing; CPU-loaded runs are optional supplemental evidence. Report the results in the task summary — verified without changes by the assessed verification at 4887cb85 (no change by this task)

> **SC-002 probe evidence is manual**: the two probes (stop the redelivery after the second attempt; make the third attempt succeed), each applied temporarily to `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs` and reverted, are manual local evidence that a person runs outside the automated implementation (see `quickstart.md`, section 4). No task runs them and they are not claimed as verified.

**Checkpoint**: User Story 1 is complete when T009 passes after the fix and no probe is left in the diff; the SC-002 probes are manual evidence and do not gate it.

---

## Phase 4: Polish & Cross-Cutting Concerns

- [ ] T013 Run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SimulatedAgentWorkload"` after rebuilding, and confirm `SimulatedAgentWorkloadTests` and `FailOnceSucceedsOnTheSecondAttempt`, `FailPermanentEndsWithExecutionFailed`, and `StallExpiresTheFirstAttemptAndSucceedsOnTheSecond` pass unchanged, and report the result in the task summary
- [ ] T014 Run `node .github/scripts/check-markdown.mjs` and confirm with `git diff --name-only origin/<base>...HEAD` (`<base>` is the pull request's base branch) that it lists only files under `src/platform/Tests/`, optionally `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/`, and `specs/20261027-135353-deterministic-fail-retryable-test/`, with no `docs/architecture/` change (the plan's Architecture Updates section is `None`, so no documentation task is required)

---

## Dependencies & Execution Order

Task IDs are intentionally not contiguous (T001, T002, T006, T008, T010, T011, and T012 are unused); no task was lost.

- T003 before the evidence runs T004 and T005 (it adds the failure capture).
- T004 and T005 run sequentially (they share the Docker containers and the test project); T007 needs both (it classifies the cause from their evidence, fixes it, and removes the T003 capture); T009 needs T007.
- T006 is unused: classifying the cause is part of T007, because a classification alone changes no file and a task without a change cannot be completed.
- T010 is unused: the SC-002 probes are manual local evidence that a person runs, not a task.
- Phase 4 needs T009.

### Parallel Opportunities

None: every task edits or runs against the same test file, runner, or Docker-backed containers, so all tasks run sequentially.

## Implementation Strategy

- **MVP**: User Story 1 is the whole feature; it is complete at the T009 checkpoint.
- Evidence first (T003 to T005, reported in task summaries), then the classification and the smallest fix at the evidenced home (T007), then proof with repeated runs (T009); the two SC-002 probes are manual local evidence outside the automation, and the neighbor and repository checks (Phase 4).
- SC-004 (the verification of #360 reports no flaky test) is post-merge evidence and is not verified by a task of this spec.
