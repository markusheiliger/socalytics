---

description: "Task list for Deterministic Fact Payload Telemetry Test"
---

# Tasks: Deterministic Fact Payload Telemetry Test

**Input**: Design documents from `/specs/20261025-113505-deterministic-fact-payload-telemetry-test/`

**Prerequisites**: plan.md, spec.md, research.md, quickstart.md (no `data-model.md` and no `contracts/`: no entities and no external interface change)

**Tests**: The feature is a test fix, so its verification tasks run the existing Integration tests, deliberate reproductions, and probes. No new test project or test class is added.

**Organization**: One user story (US1, P1). Every task changes the platform's Integration test project (`src/platform/Tests/SocAlytics.Platform.Integration.Tests`); checked T001 predates the move of the evidence record and wrote to `research.md` (verification-only tasks append their result to the evidence record, a block comment in `AnalysisTelemetryCollection.cs`, so each task leaves a change; the implementation never edits files under `specs/`, which the task validator protects); production code is changed only temporarily by probes and restored.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story the task belongs to (US1)
- All tasks are sequential: they share the one named test, the evidence record, and the process-wide analysis meter.

## Path Conventions

- Test project: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/`
- Spec folder: `specs/20261025-113505-deterministic-fact-payload-telemetry-test/`

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Confirm the build baseline before any change

- [x] T001 Restore and build the Integration test project from the repository root with `dotnet restore src/platform/SocAlytics.Platform.slnx` and `dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-restore`, and confirm that Docker is available, then record the outcome (restore and build result, warning and error counts, Docker server version) as a `### Build baseline` subsection of the `## Evidence` section of `specs/20261025-113505-deterministic-fact-payload-telemetry-test/research.md`, so the task leaves a committed change

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The non-parallel collection that every listening test class joins

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T002 Create `AnalysisTelemetryCollection` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs`: a `public sealed class` in the namespace `SocAlytics.Platform.Integration.Tests.Analysis.Support` with `[CollectionDefinition(Name, DisableParallelization = true)]` and `public const string Name = "AnalysisTelemetry"`, following `IdentityAccess/Support/TimingCollection.cs`; no other member and no fixture

**Checkpoint**: The collection exists; no test uses it yet.

---

## Phase 3: User Story 1 - The payload telemetry test fails only when its behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: `FactPayloadTelemetryTests.MetricsAndAuditRecordsCarryThePayloadReasonCodeAndNeverAPayloadValue` passes whenever the rejection of an invalid or undeclared fact payload is counted once with the bare reason code and free of payload values, no matter which other tests run at the same time, and fails only when that behavior is broken.

**Independent Test**: Run the named test 30 times alone and 10 times with the whole analysis group; every run passes. Each of the three probes makes it fail on its assertions.

- [x] T003 [US1] Reproduce the cause with the unchanged named test (FR-001): add a temporary `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ConcurrentRejectionProbe.cs` holding a probe test method in a different parallel collection (it must not join `AnalysisTelemetry`) that raises `AnalysisMetrics.CompletionRejected("result-payload-invalid")` (from `src/platform/SocAlytics.Platform.Application/Telemetry/AnalysisMetrics.cs`) in a loop for a bounded duration (for example 60 s) and stops when its test ends, without touching `FactPayloadTelemetryTests.cs`; the probe is inert (returns at once) unless the environment variable `SOCALYTICS_PROBE_CONCURRENT_REJECTION=1` is set, so it cannot disturb other runs while it stays in the tree; run the named test together with the probe with that variable set only for this run (`--filter "FullyQualifiedName~FactPayloadTelemetryTests|FullyQualifiedName~ConcurrentRejectionProbe"`) so the unchanged test, running in parallel with the probe, sees the foreign rejection in its window, and append the old test's failure message (the failing assertion of exactly one measurement or no other rejection outcome) and the confirmation of the rejection path to the evidence record (a block comment headed `Evidence record` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs`, created by this task if absent; no file under `specs/` is edited); keep the probe file for T005 and re-read the rejection path in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/CompleteAttempt.cs` to confirm it counts the rejection before it returns the response (no late-metric cause)
- [x] T004 [US1] Join the collection in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/FactPayloadTelemetryTests.cs`: add `[Collection(AnalysisTelemetryCollection.Name)]` and a short code comment that names the evidenced cause (the process-wide meter `SocAlytics.Platform.Analysis` is listened to without scope while test collections run in parallel in one process) and the isolation; change no assertion and add no wait, retry, skip, or timeout
- [x] T005 [US1] Repeat the deliberate reproduction of T003 against the changed test: with `ConcurrentRejectionProbe.cs` still present, `SOCALYTICS_PROBE_CONCURRENT_REJECTION=1` set only for this run, and the same filter (`--filter "FullyQualifiedName~FactPayloadTelemetryTests|FullyQualifiedName~ConcurrentRejectionProbe"`), xUnit runs the non-parallel `AnalysisTelemetry` collection only after the probe collection has finished, so the changed named test passes; append the new test's pass to the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs`; then delete `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ConcurrentRejectionProbe.cs`
- [x] T006 [US1] Check every other listener of the analysis meter for the defect (FR-005): read the assertions of `Analysis/Results/ValidatedAcceptanceTests.cs`, `Analysis/AnalysisTelemetryTests.cs`, `Analysis/RunSupersessionTests.cs`, `Analysis/PartialCoverageReadinessTests.cs`, and `Registry/MinimumVersionEnforcementTests.cs` (all under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/`), classify each as defective (it asserts a count or an absence that a foreign measurement of its own instrument can break) or not, and append the classification with the reasons to the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs` (a short block comment, so the task leaves a change even when no class is defective); the classification of a class that listens to the analysis meter covers every instrument it asserts on, including the messaging meter in `AnalysisTelemetryTests`, and only classes that do not listen to the analysis meter at all (listeners of agent orchestration or agent catalog meters) are out of scope
- [x] T007 [US1] Add `[Collection(AnalysisTelemetryCollection.Name)]` (with the `using` of `SocAlytics.Platform.Integration.Tests.Analysis.Support`) only to the test classes that T006 classified as defective, in their files under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/` and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/MinimumVersionEnforcementTests.cs`; classes without the defect keep their code, and no assertion is removed, loosened, or skipped
- [x] T008 [US1] Run the three probes of SC-003 one after another and restore the code after each: in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/CompleteAttempt.cs` count the rejection a second time, count it with a different outcome, and add the payload value as a metric tag; the named test must fail on its own assertions each time, append one line per probe with the failing assertion to the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs`, and `git diff` must then show no change under `src/platform/SocAlytics.Platform.Application/`
- [ ] T009 [US1] Run the 30 consecutive runs of the named test alone (SC-001) with `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~FactPayloadTelemetryTests"`, one after another in a single shell loop that prints only one pass or fail line per run and stops at the first failure (every run passes), and append the count and the result to the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs`; the earlier attempt that ran all 70 runs in this one task exceeded the 90-minute limit of a task attempt twice, so the runs are split over T009, T013, T014, and T015, and a task must not run more than its own share
- [ ] T013 [US1] Run runs 1 to 5 of the 10 consecutive runs of SC-001 and SC-002 together with the other analysis tests in one run with `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SocAlytics.Platform.Integration.Tests.Analysis|FullyQualifiedName~MinimumVersionEnforcementTests"`, one after another in a single shell loop that prints only one pass or fail line per run and stops at the first failure (every run passes; the isolated collection runs after the parallel collections; the deliberate reproduction of T003 and T005 is the proof of isolation; every class changed by T007 must be in the filter, so extend it when T007 changes a class outside those namespaces and names), and append the count and the results to the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs`
- [ ] T014 [US1] Run runs 6 to 10 of the 10 consecutive runs of SC-001 and SC-002 with the same command, filter, and loop as T013 (every run passes), and append the count and the results to the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs` so that it states 10 runs in total
- [ ] T015 [US1] Run the rest of the project once with `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build` so that the unchanged tests still pass (the whole project in one run; every test passes), and append the count and the result to the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs`

**Checkpoint**: User Story 1 is complete and independently verified.

---

## Phase 4: Polish & Cross-Cutting Concerns

**Purpose**: Evidence, documentation, and final checks

- [ ] T010 Update `docs/architecture/` only if a statement there becomes false: the plan's Architecture Updates section says `None` (the change only schedules integration tests and changes no narrative, design decision, or implementation-status statement), so confirm that no architecture file describes the scheduling of the analysis telemetry tests and append the confirmation to the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs`
- [ ] T011 Consolidate and check the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs` (FR-001, SC-004), which T003, T005, T006, T008, T009, T013, T014, T015, and T010 already filled: confirm that it holds the code path of the rejection counting, the parallel execution of the test project, the deliberate reproduction with the old and the new result (T003 and T005), the per-class defect check (T006), the run counts (T009, T013, T014, T015), the probe results (T008), and the architecture confirmation (T010), the build baseline (copied from the outcome T001 recorded in its task attempt comment or `research.md`, because the code-side record did not exist then), add what is missing from the recorded evidence without recreating it, and state in its text that SC-005 is observed after merge when #294 retries and is not verified here
- [ ] T012 Confirm with `git diff --stat` that the change touches only the test files named above and no file under `specs/`, that no assertion is textually removed, that no sleep, retry, skip, or timeout was added, and (FR-006) that the only changes to test scheduling are the new `AnalysisTelemetryCollection` and the `[Collection(AnalysisTelemetryCollection.Name)]` attributes on the classes T006 classified as defective, with no assembly-level `CollectionBehavior`, no `xunit.runner.json` parallelization setting, and no other project-wide change added, and append the confirmation to the evidence record in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/AnalysisTelemetryCollection.cs`

---

## Dependencies & Execution Order

- Phase 1 (T001) → Phase 2 (T002) → Phase 3 (T003 → T004 → T005 → T006 → T007 → T008 → T009 → T013 → T014 → T015) → Phase 4 (T010 → T011 → T012).
- T003 must run before T004: it needs the unchanged test.
- T007 depends on the classification of T006; T009, T013, T014, and T015 depend on T004, T005, T007, and T008 (probes removed) and run in that order, each within one task attempt (a task attempt is limited to 90 minutes, so the 70 runs of SC-001 and SC-002 are split).
- No task is marked `[P]`: the tasks share the named test, the process-wide meter, and the one evidence record file, so they run one after another.

## Parallel Example

None: the single story is a sequential investigation, fix, and verification chain.

## Implementation Strategy

- **MVP**: Phases 1 to 3 (US1) are the whole feature; Phase 4 completes the evidence and checks.
- **Order**: Show the cause (T003) before fixing it (T004), prove the fix (T005), extend it only where the defect is confirmed (T006, T007), prove that the assertions still bite (T008), and then measure stability in four bounded tasks (T009, T013, T014, T015).
- **Guardrails**: No retries, sleeps, longer timeouts, skips, or loosened assertions; metric names, tags, and values stay as documented (FR-003, FR-004); only the listening tests leave the parallel phase (FR-006).
