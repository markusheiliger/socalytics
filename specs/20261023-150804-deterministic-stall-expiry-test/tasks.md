---

description: "Task list for Deterministic Stall Expiry of the Simulated Agent Workload"
---

# Tasks: Deterministic Stall Expiry of the Simulated Agent Workload

**Input**: Design documents from `/specs/20261023-150804-deterministic-stall-expiry-test/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md (no `contracts/`: the feature adds no external interface)

**Tests**: No new test tasks are requested. The feature repairs one existing test; its proof is the repeated runs and probes of User Story 1. A platform change, only if the evidence requires one, gets its own focused test (FR-004).

**Organization**: One user story (the only one in spec.md). Every task is sequential: the files overlap and each step depends on the evidence of the previous one.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies); this spec has none
- **[Story]**: Which user story the task belongs to (US1)
- Include exact file paths in descriptions

## Path Conventions

- Test: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs` (helper `RunToEndAsync`, test `StallExpiresTheFirstAttemptAndSucceedsOnTheSecond`)
- Runner: `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs`
- Spec folder: `specs/20261023-150804-deterministic-stall-expiry-test/`

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: A buildable baseline and a way to run the test repeatedly

- [ ] T001 Restore and build the platform with `dotnet restore src/platform/SocAlytics.Platform.slnx` and `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, then confirm a running Docker-compatible runtime and run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"` once as the baseline, and record the outcome (build result, runtime check, tests passed and duration) in a new `## Baseline` section of `specs/20261023-150804-deterministic-stall-expiry-test/research.md`, so the task leaves a verifiable file change (a task without a change fails the implementation run)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish the cause with evidence before any code change (FR-001)

**⚠️ CRITICAL**: No fix task may start until the cause is recorded

- [ ] T002 Reproduce the failure of `StallExpiresTheFirstAttemptAndSucceedsOnTheSecond`: run `--filter "FullyQualifiedName~StallExpiresTheFirstAttemptAndSucceedsOnTheSecond"` at least 20 times alone and `--filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"` at least 5 times under CPU load (one `yes > /dev/null` per core, stopped afterwards), capturing the runner logger and platform log of every failing run; compare against the candidate causes 1 to 5 in `specs/20261023-150804-deterministic-stall-expiry-test/research.md` and append an `## Evidenced cause` section to that file naming the confirmed cause, the excluded candidates, the failure rate observed, and the file the cause lives in, and stating two explicit decisions: (a) whether a runner-side stall event is required, decided from the evidence of the runs and not assumed (the expected outcome is yes, because `ClaimAgentInvocation` writes the `active` row before the runner handles the claim response; record "not required" only when the evidence shows the `active` row and its `lease_expires_at` prove the stall is in effect) and (b) whether the cause is in platform code (the section is the text the pull request records); if no run fails, widen the load (more burners, a pause injected after the claim in a temporary probe) until the race is shown, and remove the probe

**Checkpoint**: The cause and the file it lives in are recorded; User Story 1 is unblocked

---

## Phase 3: User Story 1 - The stall test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: The stall test passes whenever the platform expires the stalled first attempt and the second attempt succeeds, however loaded the runner and host are, and fails only when that behavior is broken.

**Independent Test**: 20 consecutive passes alone and 5 consecutive passes of its class under CPU load; a probe that never expires the first attempt and a probe that makes the second attempt fail each make the test fail within the existing limits.

### Implementation for User Story 1

- [ ] T003 [US1] Runs before T004. Only if `research.md` shows the `active` row and its `lease_expires_at` are not enough to observe that the stall is in effect (`ClaimAgentInvocation` moves the attempt from Claimed to Active before the claim response reaches the runner), expose it as a minimal observable event in `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs` (raised at the `behavior == Behavior.Stall && attemptNumber == 1` branch once the runner has read the claim response and decided to stall for attempt 1; the acknowledgement of the notification is not part of the condition) and let T004 consume it in `RunToEndAsync` in `SimulatedAgentWorkloadBehaviorTests.cs`; otherwise record "not needed" in the `## Evidenced cause` section of `research.md` and change nothing else; the runner's behavior for all other paths and for `SimulatedAgentWorkloadTests` stays unchanged
- [ ] T004 [US1] Remove the evidenced cause where `research.md` (`## Evidenced cause`) says it lives, within the shapes of plan.md Design step 2: in `RunToEndAsync` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs` replace the blind `f.Factory.Time.Advance(TimeSpan.FromSeconds(3))` after "exactly one attempt row" with a bounded wait on observable conditions (the stall is in effect: the runner has read the claim response and decided to stall for attempt 1, the first `agent_attempts` row is `active`, and its `lease_expires_at` is recorded; the acknowledgement is not part of the condition; the observation is the runner event of T003 when T003 added it, otherwise the row alone, which is allowed only when T002 records evidence that the row proves the stall is in effect), then advance the fake clock once by `lease_expires_at` (read from the first attempt row) minus the fake clock's current time plus a fixed margin of 100 ms (the second attempt's lease is computed from the advanced fake clock at its claim, so it is not overshot), then wait until that attempt is `expired` (`end_reason` `lease-expired`) and check that exactly one attempt row has that reason before waiting for the end of the invocation; the wait is bounded by the helper's existing deadline and uses no fixed sleep, retry, or longer limit; the `fail-once`, `fail-retryable`, and `fail-permanent` paths (`expireFirstAttempt` false) never advance the clock and keep their helper behavior, and no assertion changes
- [ ] T005 [US1] Only if `research.md` shows a real defect in the platform's claim, lease, or expiry operations, fix it in the platform file named there, keeping the claim, fencing, lease, expiry, retry, and outbox semantics documented in `docs/architecture/intelligence-and-agents.md`, and add a focused test for it under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/`; otherwise record "no platform change" in the `## Evidenced cause` section of `research.md` and change nothing else
- [ ] T006 [US1] Prove the fix (SC-001): rebuild with `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, run `--filter "FullyQualifiedName~StallExpiresTheFirstAttemptAndSucceedsOnTheSecond"` 20 consecutive times alone and `--filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"` 5 consecutive times under CPU load, all passing, and record the counts in `research.md` under an `## Evidence after the fix` section
- [ ] T007 [US1] Prove the test still fails when behavior is wrong (SC-002): temporarily make the first attempt never expire (for example skip the clock advance), run the stall test, and confirm it fails within the existing limits on the bounded wait instead of passing or hanging; then temporarily make the second attempt end as failed or not succeeded (for example the runner reports a permanent failure on attempt 2) and confirm the test fails on the `succeeded` assertion, not on the helper's 3-minute deadline; remove both probes and confirm `git diff` on `SimulatedAgentWorkloadBehaviorTests.cs` and `SimulatedAgentWorkloadRunner.cs` shows no probe; record both outcomes in `research.md` under `## Evidence after the fix`
- [ ] T008 [US1] Check the neighbours (FR-005): run `--filter "FullyQualifiedName~SimulatedAgentWorkloadTests"` and `--filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"` once more, confirm `FailOnceSucceedsOnTheSecondAttempt`, `FailRetryableEndsWithAttemptsExhausted`, and `FailPermanentEndsWithExecutionFailed` pass with unchanged assertions, and confirm with `git diff` that no assertion (`succeeded`, exactly two attempts, at least one checkpoint) or test of the class was removed, loosened, or skipped; confirm with `git diff` of `RunToEndAsync` that the fail-once, fail-retryable, and fail-permanent paths are textually unchanged and only the stall (`expireFirstAttempt`) branch is touched, which is how FR-005 and the exclusion of the change of #209 are observed; when T005 changed platform code, also run `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build` and the architecture test project (plus the focused Integration tests) and confirm they pass; no file changes unless a regression is found, which is fixed in the file that caused it

**Checkpoint**: User Story 1 is complete and independently verified

---

## Phase 4: Polish & Cross-Cutting Concerns

**Purpose**: Documentation consistency and final checks

- [ ] T009 Confirm the `## Architecture Updates` section of `specs/20261023-150804-deterministic-stall-expiry-test/plan.md` (`None`) still holds: if T005 changed platform code, verify the change leaves every statement of `docs/architecture/intelligence-and-agents.md` true and, only if a statement became false, correct it there; otherwise change nothing
- [ ] T010 Run `node .github/scripts/check-markdown.mjs --files specs/20261023-150804-deterministic-stall-expiry-test/research.md specs/20261023-150804-deterministic-stall-expiry-test/tasks.md` and fix every finding in the files it reports; confirm the `## Evidenced cause` section of `research.md` is complete and self-contained enough to serve as the pull request text, and state that the pull request description or the final commit message quotes its first paragraph; confirm the `git diff` touches only the files the evidenced cause lives in plus this spec folder (SC-003)

---

## Dependencies & Execution Order

- **Phase 1 (T001)** → **Phase 2 (T002)** → **Phase 3 (T003 → T004 → T005 → T006 → T007 → T008)** → **Phase 4 (T009 → T010)**.
- T003 and T004 read the evidence of T002 (T002 → T003 → T004: T004 consumes the runner event T003 adds, if any); T005 reads it too; T003 and T005 are conditional and may end as a recorded "not needed". T006 to T008 prove the result of T003 to T005 and run in order because they share the Docker-backed test environment and edit the same `research.md` and files (probes).
- SC-004 (the verification of #184 reports no flaky test) is post-merge evidence and has no task.

### Parallel Opportunities

None: every task changes or measures the same test path and the same `research.md`, so no `[P]` group is formed.

## Implementation Strategy

### MVP First

User Story 1 is the whole feature: T001 to T008 deliver it; T009 and T010 close the documentation and diff checks.

### Incremental Delivery

1. Record the evidenced cause (T002) before changing code.
2. Fix the smallest place the cause lives (T004, and T003 or T005 only when the evidence requires them).
3. Prove stability, failure sensitivity, and unchanged neighbours (T006 to T008).
4. Close with the architecture check and Markdown validation (T009, T010).

## Notes

- Never add retries, fixed sleeps, longer limits, skips, or loosened assertions; a bounded wait on an observable condition is the only allowed waiting.
- Do not repeat the change of #209 (`specs/20261020-224223-deterministic-simulated-workload-retry`) or `specs/archive/20261019-221242-harden-integration-test-timing`.
- Passing runs are development evidence only, not deployment support or production readiness.
