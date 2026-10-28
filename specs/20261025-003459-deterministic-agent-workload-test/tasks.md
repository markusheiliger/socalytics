# Tasks: Deterministic Simulated Agent Workload Invocation Test

**Input**: Design documents from `/specs/20261025-003459-deterministic-agent-workload-test/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md

**Tests**: The feature is a test fix. The verification tasks are the repeated runs and probes of the specification; no new test project is added.

**Organization**: The specification has one user story (US1). The cause is unknown until the evidence is gathered, so the
fix tasks name the candidate files of [plan.md](plan.md) and are completed for the cause the evidence shows
(FR-001, FR-002); a candidate the evidence rules out is recorded as ruled out in the evidence comment block and changes nothing.
The evidence of this feature lives in a comment block in `SeedTests.cs` (see T001), because agents cannot change files under `specs/` other than the `tasks.md` tick.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1)
- Paths are relative to the repository root. `T` = `src/platform/Tests/SocAlytics.Platform.Integration.Tests`.

## Phase 1: Setup

- [x] T001 Restore and build the platform with `dotnet restore src/platform/SocAlytics.Platform.slnx` and `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, and run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SeedTests"` once (a running Docker runtime is required), and record the baseline (build result and the pass, fail, and skip counts of `SeedTests`) in the `Baseline:` lines of an `// Evidence:` comment block directly above the named test in `T/AgentOrchestration/Catalog/SeedTests.cs` (created by this task; comments only, no code change); agents cannot change files under `specs/` other than the `tasks.md` tick, so the evidence of this feature lives in this comment block, not in `research.md`.

---

## Phase 2: Foundational (Establish the Cause)

**Purpose**: FR-001 requires the cause to be proved by evidence before any fix. No fix task starts before T004 is done.

- [x] T002 Reproduce on the unchanged test: run `TheSimulatedAgentWorkloadCompletesAnInvocationOfTheSeededVersionWithItsPinnedReferences` 30 times alone and 10 times under CPU load (one busy-loop process per core), as in `quickstart.md` steps 3 and 4, and record the pass and fail counts and the failure messages in the `Reproduction:` lines of the evidence comment block (see T001).
- [x] T003 For each suspected cause (wall-clock versus fake time, start order, polling loop, shared state, covered production code) demonstrate it or rule it out by widening its window with a temporary, uncommitted test-side change (for example holding the first outbox publication or delaying the JetStream topology provisioning in `T/AgentOrchestration/Support/AgentOrchestrationFixture.cs`, starting the runner before the work message exists in `T/AgentOrchestration/Catalog/SeedTests.cs`, and skewing the frozen `MutableTimeProvider` in `T/IdentityAccess/Support/MutableTimeProvider.cs`); read the outbox row (`state`, `attempt_count`, `next_attempt_at`) and the agent attempt row (`state`, `lease_expires_at`, `deadline_at`) when the invocation stays non-terminal, revert every temporary change afterwards, and record per widening, in the `Widening:` lines of the evidence comment block (see T001; the only committed change of this task, comments only), the exact change (file, member, and values, so T009 can apply it again), the observed result of the old test, and the observed outbox and attempt rows; a task that leaves no change in the repository fails, so the record is the deliverable.
- [x] T004 Record the result per suspect ("cause" or "ruled out", with the observed run results of the old test) and the evidenced cause in the `Suspects:` and `Cause:` lines of the evidence comment block (see T001), so the fix tasks below name the files the cause lives in.

**Checkpoint**: The cause is established; the fix tasks apply only the rows of Decision 2 in `research.md` that the evidence confirms.

---

## Phase 3: User Story 1 - The invocation test fails only when behavior is wrong (Priority: P1)

**Goal**: The named test passes whenever the simulated agent workload completes an invocation of the seeded version with its pinned references, however loaded the runner is, and fails only when that behavior is broken.

**Independent Test**: 30 consecutive passes alone and 10 under CPU load of the named test, all five `SeedTests` passing, and each of the three probes (wrong advice text, wrong pinned digest, missing pinned artifact kind) failing on its assertion.

### Implementation for User Story 1

- [x] T005 [US1] Make the wait of the named test in `T/AgentOrchestration/Catalog/SeedTests.cs` bounded by an observable condition: poll the invocation until its `state` is terminal (`succeeded`, `failed`, or `lifecycle-suppressed`) and fail with a message that includes the observed `state` and the `failureReason` field of the invocation read (FR-004), replacing the `DateTime.UtcNow` bound and the bare `invocation.ShouldNotBeNull()` failure; make the poll loop and its delay use the linked token (`run.Token`, the existing `CancelAfter`) or an explicit `[Fact(Timeout = ...)]` as the single time-based safety net, solely so a hung platform cannot block the run: it is not the success condition, and when it fires catch the resulting `OperationCanceledException` and fail with the last observed `state` and the recorded `failureReason`; and keep every assertion (advice text, pinned version, pinned digest, and the four pinned artifact kinds) unchanged.
- [x] T006 [US1] Remove the evidenced cause in the file it lives in (FR-002), choosing only the confirmed rows of Decision 2 in `research.md`: give the fixture one consistent clock so a stored timestamp, token, proof, or lease never depends on a frozen reading (`T/IdentityAccess/Support/MutableTimeProvider.cs` and `T/AgentOrchestration/Support/AgentOrchestrationFixture.cs`), and/or provision the NATS stream and consumer in `T/AgentOrchestration/Support/AgentOrchestrationFixture.cs` before the runner starts so start order no longer matters; add no retry, fixed sleep, skip, or longer timeout, and keep the development values (lease, token lifetime, poll intervals) as they are.
- [x] T007 [US1] Only if the evidence names the runner or the covered production code (`SimulatedAgentWorkloadRunner.cs` in `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/`, or the outbox, `WorkerLoop`, or `AgentAttemptMonitorWorker` in `src/platform/SocAlytics.Platform.Infrastructure/`), apply the minimal change there that keeps the documented behavior of `docs/architecture/` (FR-006) and keeps the workload a development-only simulation; otherwise change nothing and record "ruled out" in the evidence comment block. — not applicable: The evidence names the runner or the covered production code (outbox, WorkerLoop, AgentAttemptMonitorWorker) as the cause (assessed at 0f221fb6; no change by this task)
- [x] T008 [US1] Finalize the evidence comment block next to the fix so it names the evidenced cause and the suspected causes ruled out (FR-001, SC-004), and make sure the other tests of `SeedTests` and every test that uses a changed helper still compile and keep every assertion.
- [x] T009 [US1] Run the verification of the specification and record the results in the `Verification:` lines of the evidence comment block (see T001): the named test 30 times alone and 10 times under CPU load (SC-001); `--filter "FullyQualifiedName~AgentOrchestration"` including `SeedTests`, `SimulatedAgentWorkloadTests`, `SimulatedAgentWorkloadBehaviorTests`, and every test using a changed helper (SC-002); if T006 changes `MutableTimeProvider`, `PlatformApiFactory`, or another shared helper, also the whole `src/platform/Tests/SocAlytics.Platform.Integration.Tests` project (or every test class that references the changed helper, found with grep), with the result recorded in the evidence comment block; `SeedTests` and the AgentOrchestration tests run in a different order and in parallel with another test class, and, if the evidence confirms shared state as a cause, a check that the fixture disposes the runner and the NATS objects when the test fails midway; and, independent of whether the failure reproduced naturally, always re-run the fixed test with the start-order widening of T003 (runner started before the work message and before the consumer exists) and with a forced redelivery of the work message, recording those passes in the evidence comment block; if the failure did not reproduce naturally, also the deliberate reproduction of T003 by applying each widening recorded in the `Widening:` lines again on top of the fix (and reverting it afterwards) and recording that the fixed test passes while the old test failed.
- [x] T010 [US1] Run the three probes of SC-003 one after another in `T/AgentOrchestration/Catalog/SeedTests.cs` (a wrong advice text, a wrong pinned digest, and a missing pinned artifact kind), confirm that the named test fails on the matching assertion each time, record the results in the `Probes:` lines of the evidence comment block, and remove every probe so `git diff` shows no probe.

**Checkpoint**: User Story 1 is complete when T009 and T010 pass; the stopped implementation of #283 can then retry.

---

## Phase 4: Polish and Cross-Cutting Concerns

- [x] T011 Re-read `docs/architecture/platform-implementation.md` ("Test And Observability Baseline") and `docs/architecture/intelligence-and-agents.md` and make every statement the final implementation affects true for the merged state (for example one sentence in "Test And Observability Baseline" if the shared fixture clock now follows real time, or an update of the owning narrative if production code changed); if nothing changes, record in the `Architecture:` line of the evidence comment block that both documents were re-read and need no change (plan.md "Architecture Updates").
- [x] T012 Check that the change touches only the files the cause lives in plus this spec folder, that no assertion was removed and no fixed wait or retry was added (SC-004), and run `node .github/scripts/check-markdown.mjs` for the changed Markdown, and record the result in the `Scope:` line of the evidence comment block (see T001) so the task leaves a committed change.

---

## Dependencies and Execution Order

- Phase 1 (T001) before everything. Phase 2 (T002 to T004) is sequential and blocks Phase 3, because the fix is chosen by the evidence.
- Phase 3: T005 and T006 first (T006 may be a no-op for a ruled-out candidate; both change `SeedTests.cs` or fixture support and run sequentially), then T007, T008, then T009 and T010 (T010 after T009 so the probes never disturb the repeated runs).
- Phase 4 after Phase 3: T011 before T012.

### Parallel Opportunities

None: every task touches the evidence comment block, the same test files, or depends on the evidence of the previous one, so all tasks run sequentially.

## Implementation Strategy

- **MVP**: User Story 1 is the whole feature; deliver Phases 1 to 3, then Phase 4.
- Establish the cause first (Phase 2), fix only what the evidence confirms, prove the result with repeated runs and probes, and keep results as development evidence only.
