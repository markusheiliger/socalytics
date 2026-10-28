---

description: "Task list for Deterministic Evidence Part State Test"
---

# Tasks: Deterministic Evidence Part State Test

**Input**: Design documents from `/specs/20261028-005033-deterministic-evidence-part-state-test/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md

**Tests**: No new test tasks, because the feature repairs an existing test. The existing evidence tests are the verification, run repeatedly and with temporary probes (SC-001, SC-002). A focused test is added only if the evidence shows a platform defect (T006).

**Organization**: One user story (US1). Evidence comes first (FR-001); the fix goes where the evidenced cause lives (FR-002).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (disjoint files, no dependencies); this feature has none
- **[Story]**: Which user story this task belongs to (US1)
- Include exact file paths in descriptions

## Path Conventions

- Evidence tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/`
- Scenario helper: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/Support/EvidenceScenario.cs`
- Evidence record: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md`, one section per measuring or confirming task, created by T001 and appended by later tasks (an agent may not change anything under `specs/` except the tick of its own task, so the evidence lives outside the spec folder)
- Spec folder: `specs/20261028-005033-deterministic-evidence-part-state-test/`

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: A restored and built baseline to measure against

- [x] T001 Run `dotnet restore src/platform/SocAlytics.Platform.slnx` and `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, then confirm that Docker and the pinned `ffmpeg` and `ffprobe` are available as listed in `specs/20261028-005033-deterministic-evidence-part-state-test/quickstart.md`, and write the commands, exit codes, and tool versions as the section `## T001` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish the cause with evidence before any fix (FR-001)

**⚠️ CRITICAL**: No fix may be written until the cause is recorded

- [x] T002 Run `EvidencePartStateTests.Never_materialized_segment_is_materializing_then_available_with_a_link_after_the_worker_ran` at least 20 times alone and the filter `FullyQualifiedName~Analysis.Evidence` at least 5 times under CPU load (as defined in quickstart.md step 3), from `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidencePartStateTests.cs`, capturing for any failure the segment rows (state, lease owner, demanded), the `MaterializeSegmentsAsync` outcomes, and `Factory.Logs`, and write the run counts, outcomes, and failure captures as the section `## T002` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md`
- [x] T003 Add a temporary probe in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidencePartStateTests.cs` that runs one segment worker iteration between the first resolution and the demanded-count query and, separately, between the count and `MaterializeSegmentsAsync` (no segment row exists before the first resolution), by calling `MaterializeSegmentHandler.ProcessDueAsync` from a second scope with a distinct lease owner (as `MaterializeSegmentsAsync` already does) or by enabling the real worker through `extraSettings` (`Analysis:Workers:Enabled=true` with a short `Segments:Materialization:PollInterval`), so no platform visibility change is needed, then run the test to show whether the worker reproduces the failure (expected signature: the row is leased or claimed, the explicit call finds nothing due, and the second resolution reads `materializing`), and note the rate
- [x] T004 Append as the section `## T004` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md` the runs, the failure captures and probe results, and the confirmed cause (or the refuted candidate and the cause found among the other candidates of its table), then remove the probe of T003 from `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidencePartStateTests.cs`

**Checkpoint**: The cause is established and recorded; the fix location is decided

---

## Phase 3: User Story 1 - The part state test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: The test passes whenever a never-materialized segment is reported as `materializing` with one demanded segment and then as `available` with a link after the explicit materialization, however loaded the runner is, and fails only when that behavior is broken.

**Independent Test**: 20 consecutive passing runs alone and 5 under CPU load; a worker probe no longer changes the outcome; probes that break demand recording or part states still fail the test on its unchanged assertions.

### Implementation for User Story 1

- [x] T005 [US1] Apply the fix where T004 locates the cause. A test-side or helper-side cause (ordering, shared state, or the RustFS or ffmpeg dependency) is fixed by controlling the racing input or by an observable-condition wait in `EvidencePartStateTests.cs` or `EvidenceScenario.cs`, with no assertion change. If the cause is the default-on background worker (expected), in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/Support/EvidenceScenario.cs` make `Settings` set `Analysis:Workers:Enabled` to `false` before the caller's `extraSettings` are applied in `StartAsync` (so a caller can still override), and add a code comment next to it stating the evidenced cause (FR-001); no retry, fixed sleep, longer limit, or changed assertion (FR-002, FR-003)
- [x] T006 [US1] Decide from the section `## T004` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md` whether the cause is a platform defect, and write the decision as the section `## T006` of that file: when T004 confirmed a test-side or helper-side cause (the default-on background worker, fixed by T005), write that no platform defect exists and change no platform file (FR-004); only if T004 shows a platform defect, also fix it in the file named there under `src/platform/SocAlytics.Platform.Application/Segments/` or `src/platform/SocAlytics.Platform.Infrastructure/Workers/` without changing the documented part state, demand recording, or materialization semantics, add a focused test under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/`, and carry a code comment stating the evidenced cause (FR-001)
- [x] T007 [US1] Confirm that `EvidenceScenario.StartAsync` (recording finalization, `CreateRunAsync`, `SeedFactsAsync`) and each evidence test never wait on any other background worker (outbox publisher, recordings-finalized consumer, lease expiry, email delivery, monitors), citing the handler calls they use, and check that every evidence test that relies on background behavior, if any, keeps it explicitly through `extraSettings`, and that every test calling `MaterializeSegmentsAsync` or `MaterializeAllAsync` stays the only source of materialization in its scenario, by reading `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/` (`EvidenceResolutionTests.cs`, `EvidenceAuthorizationTests.cs`, `EvidenceRateLimitTests.cs`, `EvidenceAuditAndLoggingTests.cs`, `EvidencePartStateTests.cs`); change a test file only to add an explicit override, never an assertion (FR-005), and write the cited handler calls and the result of the check as the section `## T007` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md`
- [x] T008 [US1] Re-run the SC-001 evidence from `specs/20261028-005033-deterministic-evidence-part-state-test/quickstart.md` (20 consecutive runs alone, 5 consecutive runs of `FullyQualifiedName~Analysis.Evidence` under the CPU load of quickstart.md step 3, and `FullyQualifiedName~Segments`), then run the SC-002 probes: the before-fix reproduction is the T003 result recorded in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md`; after the fix, repeat the worker-enabled probe through `extraSettings` in a temporary run only as a still-reproduces check, and the default scenario (workers off) passes the same probe-free run 20 times, and a probe that breaks demand recording or reports `available` before materialization makes the test fail on its unchanged assertions; remove every probe from `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/` and the platform code, and write the run counts and probe outcomes as the section `## T008` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md`
- [x] T009 [US1] Append the post-fix runs and probe results as the section `## T009` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md`

**Checkpoint**: User Story 1 is complete and independently verified

---

## Phase 4: Polish & Cross-Cutting Concerns

**Purpose**: Scope and repository checks

- [x] T010 Confirm with `git diff` that no probe remains, no assertion is textually removed or loosened, and only the files of the evidenced cause, the evidence record, plus the spec folder `specs/20261028-005033-deterministic-evidence-part-state-test/` changed, with no `docs/architecture/` change (SC-003), taking the `git diff --stat` before the T010 and T011 sections of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md` are written and excluding that file from the scope comparison (it is expected in the diff as the only evidence record), and write the stat and the result of the check as the section `## T010` of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md`
- [x] T011 Run `node .github/scripts/check-markdown.mjs --files specs/20261028-005033-deterministic-evidence-part-state-test/tasks.md src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/PartStateEvidence.md` and fix any finding, then write the command and its result as the section `## T011` of that file and run the same command once more (its own check is the last step)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies
- **Foundational (Phase 2)**: Depends on Setup; blocks the fix. T002 → T003 → T004 run in order (same test file and the same research record)
- **User Story 1 (Phase 3)**: Depends on T004. T005 and T006 are alternatives chosen by the evidence; T007 follows the fix; T008 and T009 follow T007
- **Polish (Phase 4)**: Depends on T009

### Parallel Opportunities

None: every task changes or measures the same test, the same helper, or the same research record, so all tasks run in sequence.

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Build the baseline (T001).
2. Establish and record the cause (T002–T004).
3. Fix where the cause lives (T005 or T006) and check the neighbors (T007).
4. Verify with repeated runs and probes (T008–T009), then check scope and Markdown (T010–T011).

The post-merge evidence SC-004 (the verification of #370 reports no flaky evidence test) is observed when #370 retries and is not verified by a task.
