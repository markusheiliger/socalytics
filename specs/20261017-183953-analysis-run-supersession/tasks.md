---

description: "Task list for Analysis Run Supersession"
---

# Tasks: Analysis Run Supersession

**Input**: Design documents from `specs/20261017-183953-analysis-run-supersession/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (FR-022), so every story has test tasks. Test tasks follow the implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story. The Analysis Run Cancellation feature (`specs/archive/20261017-182600-analysis-run-cancellation/`) must be merged first; its `CancelAnalysisRunHandler`, cancellation store operations, read fields, and run-list cancel action are the starting point.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Schema, version order, cancellation cause, stores, and read models that every story needs

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Schema and domain types

- [x] T001 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_analysis_run_supersession.sql`, numbered as the next free number on an up-to-date default branch after the cancellation feature's migration (data-model.md): add `recording_set_finalized_at timestamptz` to `socalytics.analysis_runs`, backfill it from `recording_set_versions.finalized_at`, then set it `NOT NULL`; add `cancellation_cause text NULL` backfilled to `member` for existing `Cancelled` runs; replace the cancellation feature's checks by "`cancellation_cause IN ('member', 'superseded')` and `(cancellation_cause IS NOT NULL) = (state = 'Cancelled')`", "`(cancelled_by_account_id IS NOT NULL) = coalesce(cancellation_cause = 'member', false)`", and "`cancellation_reason IS NULL OR cancellation_cause = 'member'`" (keeping the 500-character length check); add `uq_analysis_runs_id_match UNIQUE (id, match_id)` and `ix_analysis_runs_match_order` on `(match_id, recording_set_finalized_at DESC, recording_set_version_id DESC, created_at DESC, id DESC)`; create the insert-only `socalytics.analysis_run_supersessions` (`run_id uuid PRIMARY KEY`, `match_id uuid NOT NULL`, `superseding_run_id uuid NOT NULL`, `superseded_at timestamptz NOT NULL`, `previous_state text NOT NULL` with a check over the run states, foreign keys `(run_id, match_id)` and `(superseding_run_id, match_id)` to `analysis_runs (id, match_id)`, `CHECK (run_id <> superseding_run_id)`, index on `superseding_run_id`); classify `analysis_run_supersessions` as `TableClassification.Immutable` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`; add `RecordingSetFinalizedAt` to `NewAnalysisRun` and `AnalysisRunRecord` in `src/platform/SocAlytics.Platform.Application/Analysis/Stores/IAnalysisRunStore.cs` and write and read it in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/AnalysisRunStore.cs`, make the cancellation feature's member-cancellation write in `AnalysisRunStore.cs` set `cancellation_cause = 'member'`, pass `lineage.FinalizedAt` in both `NewAnalysisRun` constructions of `src/platform/SocAlytics.Platform.Application/Analysis/Commands/HandleRecordingsFinalizedNotification.cs`, and update every test fixture under `src/platform/Tests/` that inserts `analysis_runs` rows or constructs `NewAnalysisRun` (FR-001, FR-005, FR-009, FR-010)
- [x] T002 [P] Add `CancellationCause` (`Member`, `Superseded` with persisted names `member`, `superseded`) in `src/platform/SocAlytics.Platform.Domain/Analysis/CancellationCause.cs` and `RecordingSetVersionOrder` in `src/platform/SocAlytics.Platform.Domain/Analysis/RecordingSetVersionOrder.cs`: the version key "`(recording_set_finalized_at, recording_set_version_id)`", "same version is never older", and the run order "`(version key, created_at, id)` descending" that selects the current run and the runs older than it (data-model.md; FR-001, FR-004, FR-016)

### Domain tests

- [x] T003 Cover the version order (finalization time first, the version id as tie-break for equal times, equal versions never older), current-run selection (newest run of the latest version, newest created run within a version, none for no runs), and the cancellation cause names in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Domain/AnalysisValueObjectTests.cs` (FR-001, FR-004, FR-016)

### Stores and read models

- [x] T004 [P] Add `LockMatchRunsAsync(matchId)` (`SELECT pg_advisory_xact_lock(hashtextextended('analysis-match-runs:' || @matchId::text, 0))`) and a cause parameter on the cancellation feature's run-cancellation write (cause `superseded` writes no member and no reason) to `src/platform/SocAlytics.Platform.Application/Analysis/Stores/IAnalysisRunStore.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/AnalysisRunStore.cs`; add `IRunSupersessionStore` in `src/platform/SocAlytics.Platform.Application/Analysis/Stores/IRunSupersessionStore.cs` with its Dapper implementation `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/RunSupersessionStore.cs` (list a match's runs with state, version key, `created_at`, and supersession row; insert a supersession row; `ListMatchesNeedingSupersessionAsync(after, batch)` returning matches with a run that has no supersession row and an older version than the match's current run), and register it in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` (research R3, R4, R10; FR-002, FR-005, FR-013, FR-014)
- [x] T005 [P] Extend the run read models with `IsCurrent`, `SupersededBy` (`RunId`, `RecordingSetVersionId`, `RecordingSetFinalizedAt` of the superseding run), `SupersededAt`, and `CancellationCause` in `src/platform/SocAlytics.Platform.Application/Analysis/Queries/AnalysisRunViews.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/AnalysisRunQueries.cs`, joining `analysis_run_supersessions` and the superseding run and computing `IsCurrent` as "the first run of its match in the order `recording_set_finalized_at DESC, recording_set_version_id DESC, created_at DESC, id DESC`", for both the run status and the match's run list (research R11; FR-015, FR-016)

**Checkpoint**: Schema, order, stores, and reads support supersession; no behavior changes yet

---

## Phase 2: User Story 1 - A newer recording set stops the outdated analysis (Priority: P1) 🎯 MVP

**Goal**: Creating the run of a newer recording-set version supersedes the match's older runs in the same transaction; active ones are cancelled with cause `superseded` through the cancellation effects, with one system audit event and one run-state-changed notification.

**Independent Test**: With a running run of version A holding an active simulated attempt, finalize version B; A becomes `Cancelled` with cause superseded and superseding run B, no member is recorded, its work is fenced and discarded, its accepted results are unchanged, and exactly one audit event and one notification exist for A's transition.

### Cancellation effects

- [x] T006 [US1] Factor the in-transaction effects of the cancellation feature's `CancelAnalysisRunHandler` (run transition with time, cause, optional member and reason; unfinished node and job transitions; closing active attempts with `run-cancelled`; discarding the run's `pending` or `failed` `analysis.ready-work` rows; the one schema-validated `analysis.run-state-changed` message; fenced-attempt metrics) into the internal service `RunCancellationEffects` in `src/platform/SocAlytics.Platform.Application/Analysis/RunCancellationEffects.cs`, make `CancelAnalysisRunHandler` in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/CancelAnalysisRun.cs` call it with cause `member` and otherwise behave unchanged, and register the service in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; the cancellation feature's `RunCancellationTests` and `RunCancellationFencingTests` must stay green (research R5; FR-007, FR-009)

### Supersession rule

- [x] T007 [US1] Add the audit event `analysis.run-superseded` / action `supersede-analysis-run` (outcome succeeded, resource `analysis-run`, team id, details `runId`, `matchId`, `supersedingRunId`, `previousState`, `ActorOverride` `AuditActorKind.System`) to `src/platform/SocAlytics.Platform.Application/Analysis/Results/AnalysisAuditEvents.cs`, the counter `socalytics.analysis.runs_superseded` with tag `previous` = `active` or `ended` to `src/platform/SocAlytics.Platform.Application/Telemetry/AnalysisMetrics.cs`, and `RunSupersession.ApplyAsync(matchId)` in `src/platform/SocAlytics.Platform.Application/Analysis/RunSupersession.cs` registered in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`: within the caller's unit of work and after `LockMatchRunsAsync`, determine the current run, and for every run without a supersession row whose version is older than the current run's version, in id order, lock it, cancel it through `RunCancellationEffects` with cause `superseded` and record the audit event when it is `Requested` or `Running`, insert the supersession row with the current run, `now`, and the previous state, and count it; never touch the current run, runs of its version, runs of other matches, or runs that already have a row (data-model.md "Supersession rule"; FR-002, FR-004, FR-005, FR-007, FR-008, FR-010, FR-011, FR-014a)

### Run creation

- [x] T008 [US1] In `src/platform/SocAlytics.Platform.Application/Analysis/Commands/HandleRecordingsFinalizedNotification.cs`, take `LockMatchRunsAsync(lineage.MatchId)` before `FindRunIdAsync`, call `RunSupersession.ApplyAsync` after inserting the run in both the valid and the `Failed` (invalid workflow) branch, and call `RunReadinessCoordinator.ApplyAsync` only when the new run is still `Requested`, so a run of an older version than an existing run is created already superseded and `Cancelled` with no ready-work ever written; the receipt outcomes stay unchanged (research R9; FR-002, FR-003, FR-006, FR-013)

### Tests for User Story 1

- [x] T009 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunSupersessionTests.cs`: finalizing version B of a match with a `Requested` run and with a `Running` run of version A (with ready, waiting, and claimed jobs and an active attempt) cancels A in the same transaction that creates B's run (`cancellation_cause = 'superseded'`, no member, no reason, `terminated_at` equal to the supersession time, supersession row naming B), discards A's ready-work, answers `claim-obsolete` for A's queued claims and `attempt-obsolete` for its heartbeat, result grants, upstream read, and completion, writes exactly one `analysis.run-superseded` audit event attributed to the system and one schema-valid `analysis.run-state-changed` outbox row, and adds `runs_superseded` with `previous = active`; A's previously accepted results stay byte-for-byte unchanged and are never returned by B's reads or staged for B's jobs; a newer run created as `Failed` still supersedes; runs of another match and of the same team are unchanged (FR-002, FR-003, FR-007, FR-008, FR-014a, FR-017, FR-022; SC-001, SC-002, SC-005)
- [x] T010 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/RunSupersessionEndToEndTests.cs` using `Support/EndToEndHarness.cs`: with the simulated runtime holding an active attempt of a running run, a Coach finalizes a new recording-set version through the member API; the older run becomes `Cancelled` with cause superseded, the Manager's next heartbeat is refused, the Manager stops the container and deletes the attempt directory within two heartbeat intervals, reports nothing, and no result of the older run is accepted afterwards (FR-007, FR-022; SC-007)

**Checkpoint**: User Story 1 works; outdated analysis stops when a newer recording set's run is created

---

## Phase 3: User Story 2 - Reads tell the current run from superseded history (Priority: P1)

**Goal**: The run status and the match's run list state the current run, the superseding run, the supersession time, and the cancellation cause; ended runs keep their state and version when superseded.

**Independent Test**: For a match with completed runs of versions A and B, A reports `supersededBy` B and `isCurrent: false`, B `isCurrent: true` without `supersededBy`; A's state, version, end time, and results are unchanged; no audit event or notification exists for A's supersession.

### API

- [ ] T011 [US2] Add the additive fields `isCurrent`, `supersededBy { runId, recordingSetVersionId, recordingSetFinalizedAt }`, `supersededAt`, and `cancellationCause` (`member`, `superseded`, or null) to `AnalysisRunSummaryDto` and `AnalysisRunStatusDto` in `src/platform/SocAlytics.Platform.Api/Analysis/AnalysisRunEndpoints.cs`, keeping both operations' authorization unchanged (contracts/analysis-run-reads-api.md; FR-015, FR-016, FR-018)
- [ ] T012 [US2] Refresh the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` and confirm `OpenApiSnapshotTests` and `OpenApiContractTests` in `src/platform/Tests/SocAlytics.Platform.Host.Tests/` pass with the additive fields (FR-015)

### Tests for User Story 2

- [ ] T013 [US2] Extend `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunSupersessionTests.cs`: runs of version A that are `Completed`, `PartiallyCompleted`, `Failed`, or `Cancelled` by a member keep state, end time, failure or cancellation details, `version`, and results when B's run is created, gain only the supersession row, report `supersededBy`, `supersededAt`, and `isCurrent: false` through `getAnalysisRun` and `listMatchAnalysisRuns`, add `runs_superseded` with `previous = ended`, and produce no audit event and no outbox message; a member-cancelled run reports `cancellationCause: "member"` with its member and reason plus the superseded fields, a superseded cancellation reports `"superseded"` without `cancelledBy`; exactly one run of a match with runs is `isCurrent`, a match without runs has none; the cancellation feature's idempotent cancel answer works for a superseded member-cancelled run because its `version` did not change; superseded runs' results stay readable through the existing reads; members without a role on the owning team get not found (FR-010, FR-015–FR-018, FR-022; SC-004, SC-005)

**Checkpoint**: User Stories 1 and 2 work; reads identify current and superseded runs

---

## Phase 4: User Story 3 - Supersession stays correct under duplicates, reordering, and races (Priority: P1)

**Goal**: Reconciliation re-applies supersession, and every delivery order, duplicate, concurrent finalization, and concurrent member cancellation ends in the same supersession records with one audit event and notification per transition.

**Independent Test**: Deliver the events of versions A, B, and C in the orders A-B-C, C-B-A, and B-A-C with duplicates and a concurrent member cancellation; the runs of A and B end superseded, C's run is current, and each transition produced one audit event and one notification.

### Reconciliation

- [ ] T014 [US3] In `src/platform/SocAlytics.Platform.Application/Analysis/Commands/ReconcileAnalysisPublication.cs`, before the ready-work pass, list matches through `IRunSupersessionStore.ListMatchesNeedingSupersessionAsync` in batches of `AnalysisReconciliationOptions.BatchSize` and apply `RunSupersession.ApplyAsync` per match in its own unit of work after `LockMatchRunsAsync`; add `RunsSuperseded` to `ReconciliationResult`, the detail `runsSuperseded` to the `analysis.publication-reconciled` audit event in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/RunAnalysisReconciliation.cs`, and the count to the log message in `src/platform/SocAlytics.Platform.Infrastructure/Workers/PublicationReconciliationWorker.cs` (research R10; FR-014)

### Tests for User Story 3

- [ ] T015 [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunSupersessionOrderingTests.cs`: recordings-finalized events of versions A, B, and C of one match delivered in the orders A-B-C, C-B-A, and B-A-C, each with duplicates, end with identical supersession rows (A and B superseded, C current) and run states; a late older run is created `Cancelled` with cause superseded, superseded by the current run, with no `analysis.ready-work` row and one notification; an already superseded run keeps its first superseding run when a newer version arrives; duplicates change nothing and add no audit event or notification; concurrent finalizations of two versions of one match serialize and leave the older run superseded; a member cancellation racing a supersession of the same active run, in both commit orders, yields exactly one cancellation record (member, then supersession row only; or superseded, then the cancellation feature's idempotent answer or `412`) with one audit event for the transition and one notification; two runs of the same version never supersede each other; versions with equal finalization times follow the id tie-break; runs inserted without supersession rows (as before this feature) are superseded by the reconciliation with the match's current run, active ones cancelled, and a second reconciliation changes nothing (FR-004, FR-005, FR-006, FR-011–FR-014, FR-022; SC-003)

**Checkpoint**: User Stories 1–3 work; supersession is idempotent and race-safe

---

## Phase 5: User Story 4 - See superseded and current runs on the match page (Priority: P2)

**Goal**: The match page's run list marks the current run and every superseded run with the superseding run's recording set and the supersession time, and shows a superseded cancellation without a member.

**Independent Test**: In the simulated browser project and against the platform, finalizing a second recording set while the first run runs turns the first run into a superseded run referencing the second set's run, and marks the second run as current, for Coaches and Viewers.

### Model and adapter

- [ ] T016 [US4] Add `isCurrent: boolean`, `supersededBy?: { runId, recordingSetVersionId, recordingSetFinalizedAt: Date }`, `supersededAt?: Date`, and `cancellationCause?: 'member' | 'superseded'` to `AnalysisRunSummary` in `src/clients/web/src/platform/models/recordings.ts`, map them in `src/clients/web/src/platform/adapters/analysisRunAdapter.ts` for `listByMatch` and the cancellation feature's `cancel` over the Kiota client that `npm run build` regenerates from the refreshed snapshot, and update existing unit-test fixtures that construct `AnalysisRunSummary` (contracts/web-ui-supersession-markers.md; FR-019, FR-021)

### Run list markers

- [ ] T017 [US4] In `src/clients/web/src/features/lineage/AnalysisRunStates.tsx`, add to each run line the text marker "Current run" for `isCurrent` and "Superseded by the run of the recording set finalized {time} · {supersession time}" for `supersededBy` (times through the existing `formatKickoff`), show a run with cause superseded as "Cancelled (superseded)" without member, reason, or cancel action, keep the cancellation feature's member details for cause member alongside the superseded marker, keep every run listed in its recording set's group without hiding or collapsing, and keep the markers inside the line's existing `role="status"` semantics, never color alone; add the keys to `src/clients/web/src/i18n/en/lineage.json` (contracts/web-ui-supersession-markers.md; FR-019–FR-021)

### Tests for User Story 4

- [ ] T018 [P] [US4] Add unit tests for the mapping of the new fields in `src/clients/web/tests/unit/platform/adapters/analysisRunAdapter.test.ts` and for the current marker, the superseded marker, the superseded cancellation without member or action, the member cancellation plus superseded marker, and runs without markers in the new `src/clients/web/tests/unit/features/lineage/runs/runSupersession.test.tsx` (FR-019–FR-021)
- [ ] T019 [P] [US4] Add superseded and current run data to `src/clients/web/tests/e2e/support/simulatedApi.ts` and `src/clients/web/tests/e2e/simulated/run-supersession.spec.ts`: a Coach and a Viewer see the current and superseded markers and the superseded cancellation without a member; a refresh while a run is unfinished updates the markers without a reload; axe checks pass (FR-019–FR-021; SC-006)
- [ ] T020 [P] [US4] Add `src/clients/web/tests/e2e/platform/run-supersession.spec.ts` and a chained `platform-supersession` project after the cancellation feature's `platform-cancellation` project in `src/clients/web/playwright.config.ts` that finalizes a second recording set for the upload flow's match on the running platform and sees the earlier run marked as superseded (with its member cancellation details) and the new run marked as current (FR-019, FR-020, FR-022)

**Checkpoint**: All user stories work independently

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Architecture truth at merge and end-to-end validation

- [ ] T021 Make `docs/architecture/` describe the merged state: turn the decided-not-implemented supersession statements in `docs/architecture/job-processing.md` (Run State And Recovery), `docs/architecture/match-data-pipeline.md` (Upload and Recording-Lineage Foundation), `docs/architecture/analysts-models-and-hardware.md` (Capability-Scoped Result Access), and `docs/architecture/client-applications.md` (Web UI) into present tense, add run supersession to the development, end-to-end, and browser-test evidence in `docs/architecture/platform-implementation.md`, keep `POL-005` in `docs/architecture/security-and-data-governance.md` unresolved, and correct every other statement the feature makes false, re-reading `docs/architecture/`, `AGENTS.md` "Current State", `src/platform/README.md`, `src/clients/README.md`, and the root `README.md` at implementation time; the rewrites listed under Architecture Updates in plan.md are examples, not the whole job; then run `node .github/scripts/check-markdown.mjs`
- [ ] T022 Run the validation in `specs/20261017-183953-analysis-run-supersession/quickstart.md` (platform restore, build, integration, structure, host, and end-to-end filters and the full solution test; Web UI `npm ci`, `npm run build`, `npm test`, `npm run test:e2e -- --project simulated`, and the platform chain) and fix any failure in the files of the task that caused it

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: Depends on the merged cancellation feature; blocks all user stories. T003 needs T002; T004 and T005 need T001 and T002.
- **User Story 1 (Phase 2)**: Depends on Phase 1. T007 needs T006; T008 needs T007; T009 and T010 need T008.
- **User Story 2 (Phase 3)**: Depends on Phase 1 and on T008 (supersession rows exist only once run creation supersedes). T012 needs T011; T013 needs T011 and T012.
- **User Story 3 (Phase 4)**: Depends on T007 and T008. T015 needs T014.
- **User Story 4 (Phase 5)**: Depends on T011 and T012 (fields and snapshot for the regenerated client). T017 needs T016; T018–T020 need T017.
- **Polish (Phase 6)**: Depends on all stories.

### User Story Dependencies

- **US1 (P1)**: Independent after Phase 1; the MVP.
- **US2 (P1)**: Reads the supersession rows that US1 writes; independently testable with its own tests.
- **US3 (P1)**: Hardens the US1 rule and adds reconciliation; independently testable.
- **US4 (P2)**: Uses the US2 read fields; does not need US3 to be demonstrated.

### Parallel Opportunities

- Phase 1: T001 and T002 (migration with insert path, and domain types); then T004 and T005 (stores and read models).
- Phase 2: T009 and T010 (integration and end-to-end tests).
- Phase 5: T018, T019, T020 (unit, simulated, and platform browser tests).

---

## Parallel Example: User Story 4

```text
Task: "T018 Unit tests in tests/unit/platform/adapters/analysisRunAdapter.test.ts and tests/unit/features/lineage/runs/runSupersession.test.tsx"
Task: "T019 Simulated browser test in tests/e2e/support/simulatedApi.ts and tests/e2e/simulated/run-supersession.spec.ts"
Task: "T020 Platform browser test in tests/e2e/platform/run-supersession.spec.ts and playwright.config.ts"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Foundational.
2. Complete Phase 2: User Story 1.
3. **Stop and validate**: finalize a newer recording set and see the older active run cancelled as superseded with `RunSupersessionTests` and `RunSupersessionEndToEndTests`.

### Incremental Delivery

1. Foundational → order, cause, supersession table, stores, and reads ready.
2. US1 → active older runs stop on revision (MVP).
3. US2 → reads state current and superseded runs.
4. US3 → reordering, duplicates, races, and reconciliation proven.
5. US4 → Web UI markers.
6. Polish → architecture truth and quickstart validation.

---

## Notes

- `[P]` tasks under one heading run in parallel from the same commit; their files are disjoint.
- The migration number is assigned at implementation time as the next free number after the cancellation feature's migration; renumber before merge if it was taken.
- `analysis_run_supersessions` is insert-only and classified `Immutable`; it never advances a run's `version`.
- The lock order is always the match advisory lock, then run locks in id order; member cancellation and readiness never take the match lock.
- Never refresh the OpenAPI snapshot in the verification environment; T012 refreshes it in development and commits it.
