---

description: "Task list for Deterministic Stall Expiry of the Simulated Agent Workload"
---

# Tasks: Deterministic Stall Expiry of the Simulated Agent Workload

**Input**: Design documents from `/specs/archive/20261023-150804-deterministic-stall-expiry-test/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md (no `contracts/`: the feature adds no external interface)

**Tests**: No new test tasks are requested. The feature repairs one existing test; its proof is the repeated runs and probes of User Story 1. A platform change, only if the evidence requires one, gets its own focused test (FR-004).

**Organization**: One user story (the only one in spec.md) and one task. An implementation task may change only code and tests outside `specs/` and `.github/` (and tick its own box in `tasks.md`): it cannot write evidence into this spec folder, and a task that leaves no file change fails the implementation run. The evidence therefore lives where an agent may write it: in a code comment next to the fix, in the agent's final summary, and in the commit message of the landed task.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies); this spec has none
- **[Story]**: Which user story the task belongs to (US1)
- Include exact file paths in descriptions

## Path Conventions

- Test: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs` (helper `RunToEndAsync`, test `StallExpiresTheFirstAttemptAndSucceedsOnTheSecond`)
- Runner: `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs`
- Spec folder: `specs/archive/20261023-150804-deterministic-stall-expiry-test/` (read-only for the implementation task)

## Phase 1: User Story 1 - The stall test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: The stall test passes whenever the platform expires the stalled first attempt and the second attempt succeeds, however loaded the runner and host are, and fails only when that behavior is broken.

**Independent Test**: 20 consecutive passes alone and 5 consecutive passes of its class under CPU load; a probe that never expires the first attempt and a probe that makes the second attempt fail each make the test fail within the existing limits.

### Implementation for User Story 1

- [x] T001 [US1] Establish the cause, remove it, and prove the result, in this order, changing only code and tests (never files under `specs/`, so no `research.md` section is written; the result is a change of the file the cause lives in):
  1. Build: `dotnet restore src/platform/SocAlytics.Platform.slnx` and `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`; confirm a running Docker-compatible runtime; run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"` once as the baseline.
  2. Cause (FR-001): reproduce the failure of `StallExpiresTheFirstAttemptAndSucceedsOnTheSecond` by running `--filter "FullyQualifiedName~StallExpiresTheFirstAttemptAndSucceedsOnTheSecond"` at least 20 times alone and `--filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"` at least 5 times under CPU load (one `yes > /dev/null` per core, stopped afterwards), capturing the runner logger and platform log of every failing run; compare against the candidate causes 1 to 5 in `research.md`; if no run fails, widen the load (more burners, a pause injected after the claim in a temporary probe) until the race is shown, and remove the probe. Decide from the evidence (a) whether a runner-side stall event is required (the expected outcome is yes, because `ClaimAgentInvocation` writes the `active` row before the runner handles the claim response; "not required" only when the evidence shows the `active` row and its `lease_expires_at` prove the stall is in effect) and (b) whether the cause is in platform code.
  3. Fix (FR-002) in the file the evidence names, within the shapes of plan.md Design step 2: when (a) requires it, expose the stall as a minimal observable event in `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs` (raised at the `behavior == Behavior.Stall && attemptNumber == 1` branch once the runner has read the claim response and decided to stall for attempt 1; the acknowledgement is not part of the condition; the runner's behavior for all other paths and for `SimulatedAgentWorkloadTests` stays unchanged); in `RunToEndAsync` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs` replace the blind `f.Factory.Time.Advance(TimeSpan.FromSeconds(3))` after "exactly one attempt row" with a bounded wait on observable conditions (the stall is in effect: the runner event when (a) added it, otherwise the first `agent_attempts` row being `active` with `lease_expires_at` recorded, which is allowed only when the evidence shows that row proves the stall is in effect), then advance the fake clock once by `lease_expires_at` (read from the first attempt row) minus the fake clock's current time plus a fixed margin of 100 ms, then wait until that attempt is `expired` (`end_reason` `lease-expired`) and check that exactly one attempt row has that reason before waiting for the end of the invocation; the wait is bounded by the helper's existing deadline and uses no fixed sleep, retry, or longer limit; the `fail-once`, `fail-retryable`, and `fail-permanent` paths (`expireFirstAttempt` false) never advance the clock and keep their helper behavior, and no assertion changes. Only when (b) shows a real defect in the platform's claim, lease, or expiry operations, fix it in the platform file the evidence names, keeping the semantics documented in `docs/architecture/intelligence-and-agents.md`, and add a focused test for it under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/`. Record the evidenced cause (the confirmed cause, the excluded candidates, the failure rate observed, the file it lives in, and decisions (a) and (b)) in a code comment next to the fix, and repeat it in the agent's final summary so the commit message and pull request quote it.
  4. Prove the fix (SC-001): rebuild, then run the stall test 20 consecutive times alone and `SimulatedAgentWorkloadBehaviorTests` 5 consecutive times under CPU load, all passing.
  5. Prove the test still fails when behavior is wrong (SC-002): temporarily make the first attempt never expire (for example skip the clock advance) and confirm the stall test fails within the existing limits on the bounded wait instead of passing or hanging; then temporarily make the second attempt end as failed or not succeeded (for example the runner reports a permanent failure on attempt 2) and confirm the test fails on the `succeeded` assertion, not on the helper's 3-minute deadline; remove both probes and confirm `git diff` on `SimulatedAgentWorkloadBehaviorTests.cs` and `SimulatedAgentWorkloadRunner.cs` shows no probe.
  6. Check the neighbours (FR-005): run `--filter "FullyQualifiedName~SimulatedAgentWorkloadTests"` and `--filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"` once more; confirm `FailOnceSucceedsOnTheSecondAttempt`, `FailRetryableEndsWithAttemptsExhausted`, and `FailPermanentEndsWithExecutionFailed` pass; confirm with `git diff` that no assertion (`succeeded`, exactly two attempts, at least one checkpoint) or test of the class was removed, loosened, or skipped and that the fail-once, fail-retryable, and fail-permanent paths of `RunToEndAsync` are textually unchanged (how FR-005 and the exclusion of the change of #209 are observed); when a platform file changed, also run `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build` and the architecture test project, and confirm every statement of `docs/architecture/intelligence-and-agents.md` stays true (the plan's `## Architecture Updates` is `None`); confirm `git diff` touches only the files the evidenced cause lives in (SC-003).

**Checkpoint**: User Story 1 is complete and independently verified

---

## Dependencies & Execution Order

- T001 is the only task; its steps run in the order listed because they share the Docker-backed test environment and the same files (the probes).
- SC-004 (the verification of #184 reports no flaky test) is post-merge evidence and has no task.

### Parallel Opportunities

None: the feature is one task.

## Implementation Strategy

### MVP First

User Story 1 is the whole feature: T001 delivers it.

### Incremental Delivery

1. Establish the cause with evidence before changing code (T001 step 2).
2. Fix the smallest place the cause lives (T001 step 3).
3. Prove stability, failure sensitivity, and unchanged neighbours (T001 steps 4 to 6).

## Notes

- Never add retries, fixed sleeps, longer limits, skips, or loosened assertions; a bounded wait on an observable condition is the only allowed waiting.
- The implementation task cannot change anything under `specs/` except the tick of its own box; the Markdown of this folder is validated by the pipeline, not by the task.
- Do not repeat the change of #209 (`specs/archive/20261020-224223-deterministic-simulated-workload-retry`) or `specs/archive/20261019-221242-harden-integration-test-timing`.
- Passing runs are development evidence only, not deployment support or production readiness.

## Phase 2: Convergence

- [x] T002 [US1] Complete the evidenced cause in the comment above `if (expireFirstAttempt)` in `RunToEndAsync` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs`, which currently says the cause was found only by reading the code and was not reproduced: temporarily restore the old blind step (`f.Factory.Time.Advance(TimeSpan.FromSeconds(3))` once exactly one attempt row exists) together with a temporary pause in `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs` between reading the claim response and the `behavior == Behavior.Stall && attemptNumber == 1` branch, run `StallExpiresTheFirstAttemptAndSucceedsOnTheSecond` at least 10 times to show the failure and its rate, then remove both probes (confirm with `git diff` that the runner change is only the `AttemptStalled` event); rewrite the comment so that it names the confirmed cause (research.md candidates 1 and 3), the excluded candidates 2, 4, and 5 with the reason for each, the failure rates observed with and without the probe, the file the cause lives in, and decisions (a) and (b); change no assertion or code line and repeat the cause in the final summary per FR-001 (partial)
