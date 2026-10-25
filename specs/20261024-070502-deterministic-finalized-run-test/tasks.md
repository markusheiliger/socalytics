---

description: "Task list for Deterministic Finalized Recording Set Run Test"
---

# Tasks: Deterministic Finalized Recording Set Run Test

**Input**: Design documents from `/specs/20261024-070502-deterministic-finalized-run-test/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md

**Tests**: The feature is itself a test fix. No separate test tasks are added; the evidence tasks run the fixed test, the other tests of its class, and the probes.

**Organization**: One user story (P1). All production and test code changes are confined to `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs`; all evidence is recorded in `specs/20261024-070502-deterministic-finalized-run-test/research.md`. Because every task touches one of these two files, no task is marked `[P]`. Every task, including a verification-only task, leaves a file change (its evidence appended to `research.md`), because the implementation automation rejects a task that changes nothing; temporary edits of the test file are reverted and are never that change.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (disjoint files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1)
- Include exact file paths in descriptions

## Path Conventions

- Test file: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs`
- Evidence: `specs/20261024-070502-deterministic-finalized-run-test/research.md` (section `## Evidence`)
- Commands run from the repository root; a running Docker-compatible runtime is required (see `quickstart.md`).

## Phase 1: Setup (Baseline)

**Purpose**: Establish the starting state of the unchanged test before anything is edited.

- [ ] T001 Build the project with `dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests` and run the unchanged class once with `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~RunCreationTests"`; append the build result and the pass count under a new `### T001 Baseline` heading in the `## Evidence` section of `specs/20261024-070502-deterministic-finalized-run-test/research.md` (the only file change; the automation rejects a task without one).
- [ ] T002 Run the unchanged `FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun` repeatedly (`--filter "FullyQualifiedName~FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun"`), at least 30 times alone and at least 10 times with one busy-loop process per core, and append the pass and fail counts and any failing assertion line under a new `### T002 Repeated runs of the unchanged test` heading in the `## Evidence` section of `specs/20261024-070502-deterministic-finalized-run-test/research.md` (FR-001; the only file change).

---

## Phase 2: Foundational (Establish the Cause Before Fixing It)

**Purpose**: FR-001 requires evidence of the cause before the fix. Blocks the user story.

**⚠️ CRITICAL**: The temporary edit of T003 MUST be reverted before T005 starts; never commit it.

- [ ] T003 Demonstrate the race on the unchanged test: temporarily delay the publisher's `published`-state commit from the test side only (decorate `IUnitOfWork` and its scope through the test's `configureServices` hook in `FinalizationSetup`, and also decorate `IOutboxPublicationStore` so that `MarkPublishedAsync` sets a flag on a per-scope object shared with the decorated `IUnitOfWork` scope, whose `CommitAsync` awaits a fixed 2-second delay only when that flag is set, leaving the consumer's run creation undelayed, per `quickstart.md` step 4; no competing row lock, because the publisher claims rows with `FOR UPDATE SKIP LOCKED`; edit in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs`), run the test, confirm it fails on the immediate `published` count (`ShouldBe(1)` after the run exists), then revert the temporary edit completely (`git diff` shows no change to that file) and append the output under a new `### T003 Race demonstration` heading in the `## Evidence` section of `specs/20261024-070502-deterministic-finalized-run-test/research.md` (that note is the only change that stays).
- [ ] T004 Rule leftover state in or out (research Decision 3): read how `FinalizationSetup` provisions the database and the NATS topology in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs` and its helpers, and confirm whether the stream (`NatsOptions.EventsStream`) and the durable consumer (`AnalysisNotificationOptions.ConsumerName`) are per test or shared through `NatsContainerFixture` (the `NatsFaultInjection` collection serializes the class), and that `NumPending == 0` and `NumAckPending == 0` for that consumer at the start of each test; if state is shared or leftover state is evidenced, add isolation or a consumer baseline (wait only on the sequences of the duplicates and on `NumAckPending` of this consumer) in the test file, and append the finding under a new `### T004 Isolation` heading in the `## Evidence` section of `specs/20261024-070502-deterministic-finalized-run-test/research.md`, even when no test change is needed.

**Checkpoint**: The cause is evidenced (T002, T003) and isolation is ruled in or out (T004).

---

## Phase 3: User Story 1 - The finalized recording set test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: The test passes whenever a finalized recording set flows through publication to exactly one run and one receipt even with duplicates, however loaded the runner is, and fails only when that behavior is broken.

**Independent Test**: Run the test repeatedly alone and under CPU load: every run passes; with the race deliberately widened it still passes; each of two probes makes it fail on its assertions.

### Implementation for User Story 1

- [ ] T005 [US1] In `FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs`, replace the immediate `published` count with `(await WaitAsync(async () => await s.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'matches.recordings-finalized' AND state = 'published'", ct) == 1, ct)).ShouldBeTrue();` followed by the unchanged assertion `(await s.ScalarAsync(...)).ShouldBe(1)` on the same query (FR-003, research Decision 1), and add a code comment next to it naming the evidenced cause: the publisher commits the `published` state once after the whole batch, so the consumer can create the run first.
- [ ] T006 [US1] In `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs`, make the private `PublishAsync` helper return the broker acknowledgement's stream sequence (`ack.Seq`) without changing its behavior (the call sites of the other tests, such as the schema-invalid test, stay unchanged and must compile and behave unchanged), and add one private helper that uses `WaitAsync` to poll the durable consumer (`GetConsumerAsync` for the `NatsOptions.EventsStream` stream and `AnalysisNotificationOptions.ConsumerName`, refreshed per poll) until `NumPending == 0`, `NumAckPending == 0`, `AckFloor.ConsumerSeq == Delivered.ConsumerSeq`, and the highest duplicate stream sequence is at or below `AckFloor.StreamSeq` (research Decision 2); the helper fails the test with a clear message when the bounded wait fails (FR-002, FR-004).
- [ ] T007 [US1] In `FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs`, remove `await Task.Delay(TimeSpan.FromSeconds(4), ct);`, capture the sequences of the two duplicate `PublishAsync` calls, await the helper of T006 with the higher sequence, and only then keep the unchanged assertions `analysis_runs` count `ShouldBe(1)` and `analysis_notification_receipts` count `ShouldBe(1)` (FR-004, FR-005; no `Task.Delay` or other fixed wait remains in this test), and add a short code comment next to the consumer-settled wait stating that the absence of a second run or receipt is proven by observing both duplicates acknowledged, not by elapsed time.
- [ ] T008 [US1] Run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~RunCreationTests"` (builds first) and confirm every test of the class passes with its assertions intact (SC-002, spec acceptance scenario 4), fixing any compile or test failure in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs`, and append the result under a new `### T008 Class run` heading in the `## Evidence` section of `specs/20261024-070502-deterministic-finalized-run-test/research.md`.
- [ ] T009 [US1] Run the fixed `FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun` at least 30 consecutive times alone and at least 10 consecutive times with one busy-loop process per core, and the whole `RunCreationTests` class in the same style (at least 30 runs alone and at least 10 under load) (SC-001, SC-002); also run the whole class in a different test order (or each other test of the class immediately before the named test), and one run in which the named test is made to fail mid-test by a temporary edit (reverted afterward, `git diff` clean) followed by another test of the class, which must still pass (spec edge cases on leftover state and test order); append the counts under a new `### T009 Repeated runs of the fixed test` heading in the `## Evidence` section of `specs/20261024-070502-deterministic-finalized-run-test/research.md`; all runs without a temporary failure must pass.
- [ ] T010 [US1] Repeat the deliberate widening of T003 against the fixed test (temporary edit of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs`, the same `IUnitOfWork` commit-delay decorator, the same `IOutboxPublicationStore` flag decorator, and the same 2-second delay as in T003), confirm the fixed test now passes under it, then revert the temporary edit completely and append the output under a new `### T010 Widening against the fixed test` heading in the `## Evidence` section of `specs/20261024-070502-deterministic-finalized-run-test/research.md` (FR-001).
- [ ] T011 [US1] Run the two probes of `quickstart.md` step 5 one after the other, each as a temporary change reverted afterward: a consumer that creates a second run for a duplicate, and a consumer that writes a second receipt (temporary edits in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs` using the test's `configureServices` hook, or in the covered consumer if the hook cannot reach it); confirm the test fails on its run assertion or its receipt assertion respectively and does not hang, then verify with `git status` that every probe change is gone and append the output under a new `### T011 Probes` heading in the `## Evidence` section of `specs/20261024-070502-deterministic-finalized-run-test/research.md` (SC-003).

**Checkpoint**: User Story 1 is complete and independently verified.

---

## Phase 4: Polish & Cross-Cutting Concerns

**Purpose**: Record the evidence and confirm the change stays within scope.

- [ ] T012 Replace the placeholder text of the `## Evidence` section in `specs/20261024-070502-deterministic-finalized-run-test/research.md` and consolidate the sub-sections appended by T001 to T004 and T008 to T011, with the evidence of T001, T002, T003, T004, T009, T010, and T011 (baseline and repeated-run counts alone and under load, the evidenced cause, the widening before and after, the isolation finding, and the two probes), and correct any statement of Decisions 1 to 3 or the findings that the evidence contradicts, and align the code comments of T005 and T007 in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs` with the evidenced cause (FR-001, SC-004); state that passing results are development evidence only.
- [ ] T013 Confirm scope with `git diff --stat` and `git diff`: only `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs` and files in `specs/20261024-070502-deterministic-finalized-run-test/` changed, no assertion is textually removed, and no `Task.Delay`, retry, skip, or longer timeout was added (SC-004, FR-002, FR-005, FR-006); fix `RunCreationTests.cs` if the diff shows otherwise.
- [ ] T014 Run `node .github/scripts/check-markdown.mjs --files specs/20261024-070502-deterministic-finalized-run-test/research.md specs/20261024-070502-deterministic-finalized-run-test/tasks.md` and fix any finding in those files.

`docs/architecture/` needs no task: the plan's `## Architecture Updates` is `None` because no production code, contract, or documented behavior changes.

---

## Dependencies & Execution Order

- Phase 1 (T001, T002) before Phase 2 (T003, T004): the baseline shows the unchanged behavior; T003 must be reverted before T005.
- Phase 2 blocks User Story 1. T005, T006, and T007 edit the same file in order; T008 to T011 verify in order (T010 and T011 revert their temporary edits before the next task).
- Phase 4 follows User Story 1: T012 needs the results of T001 to T004 and T009 to T011.
- SC-005 (the verification of #273 reports no flaky test) is post-merge evidence and has no task.

### Parallel Opportunities

None: each task edits `RunCreationTests.cs` or `research.md`, or runs a sequence that depends on the state of the working tree, so none is marked `[P]`.

## Implementation Strategy

- **MVP**: User Story 1 is the whole feature: wait for the `published` state (T005), replace the fixed sleep with an observable consumer-settled condition (T006, T007), then prove it (T008 to T011) and record the evidence (T012).
- Make no production change unless T003 or T004 shows the cause elsewhere; if so, change only the file where the evidence puts the cause, keep the documented behavior (FR-006), and add the file to T013's scope check.
