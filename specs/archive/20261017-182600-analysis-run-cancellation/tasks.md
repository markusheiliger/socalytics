---

description: "Task list for Analysis Run Cancellation"
---

# Tasks: Analysis Run Cancellation

**Input**: Design documents from `specs/archive/20261017-182600-analysis-run-cancellation/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (FR-021), so every story has test tasks. Test tasks follow the implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Schema, domain states, contract example, stores, and read models that every story needs

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Schema, domain, and contract example

- [x] T001 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_analysis_run_cancellation.sql`, numbered as the next free number on an up-to-date default branch: add `cancelled_by_account_id uuid NULL REFERENCES socalytics.member_account (id)` and `cancellation_reason text NULL` to `socalytics.analysis_runs` with the constraints "`(cancelled_by_account_id IS NOT NULL) = (state = 'Cancelled')`" and "`cancellation_reason IS NULL OR state = 'Cancelled'`" and a length check of at most 500 characters; replace the state checks so `analysis_workflow_nodes`, `analysis_logical_jobs`, and `analysis_execution_attempts` also allow `'Cancelled'` (data-model.md); never edit an applied migration and add no new table
- [x] T002 [P] Add `Cancelled` to `WorkflowNodeState`, `LogicalJobState`, and `ExecutionAttemptState`, allow the transitions `Requested|Running → Cancelled` (run), `Waiting|Active → Cancelled` (node), `Waiting|Ready|Claimed → Cancelled` (job), and `Active → Cancelled` (attempt) with no outgoing transition from any `Cancelled` state, and add `RunCancellation` with the reason rules ("optional; trimmed; empty → null; length ≤ 500; no control characters except `\n` and `\r\n`") and the idempotency rule ("the current version or current − 1 for a `Cancelled` run") in `src/platform/SocAlytics.Platform.Domain/Analysis/WorkflowNodeState.cs`, `LogicalJobState.cs`, `ExecutionAttemptState.cs`, `AnalysisStateTransitions.cs`, and the new `RunCancellation.cs` (FR-003, FR-004, FR-006, FR-007, FR-011)
- [x] T003 [P] Add the valid example `contracts/analysis/run-state-changed/v1/examples/valid/cancelled.json` (previous state `Running`, state `Cancelled`, no `failureReason`, no reason field) without changing the v1 schema, so `SocAlytics.Platform.Contracts.Tests` validates the cancelled transition shape (FR-005, FR-006)

### Domain tests

- [x] T004 Cover the new transitions (allowed and refused, including every transition out of `Cancelled`), the reason rules, and the idempotency version rule in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Domain/AnalysisValueObjectTests.cs` (FR-003, FR-004, FR-006, FR-007, FR-011)

### Stores and read models

- [x] T005 [P] Add the cancellation store operations and their Dapper implementations: transition a locked run to `Cancelled` with `terminated_at`, `cancelled_by_account_id`, and `cancellation_reason` (`IAnalysisRunStore` / `AnalysisRunStore.cs`); list and transition the run's unfinished nodes and logical jobs to `Cancelled` in id order (`ILogicalJobStore` / `LogicalJobStore.cs`, plus the node update next to the existing node transitions); list the run's `Active` attempts and close them as `Cancelled` with close reason `run-cancelled` (`IExecutionAttemptStore` / `ExecutionAttemptStore.cs`); set the run's `analysis.ready-work` outbox rows in `pending` or `failed` to `discarded` (`IPublicationReconciliationStore` / `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Messaging/PublicationReconciliationStore.cs`); interfaces under `src/platform/SocAlytics.Platform.Application/Analysis/Stores/`, implementations under `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/` (FR-005, FR-008, FR-010, FR-011)
- [x] T006 [P] Extend the run read models with `CancelledBy` (`MemberId`, `AccountName` joined from `member_account.account_name` at read time, never copied into the run), `CancellationReason`, and a `Cancelled` job count per node in `src/platform/SocAlytics.Platform.Application/Analysis/Queries/AnalysisRunViews.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/AnalysisRunQueries.cs`, and count `Cancelled` jobs and attempts in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/AnalysisOperationalQueries.cs` (FR-011, FR-016)

**Checkpoint**: Schema, states, stores, and reads support cancellation; no behavior is exposed yet

---

## Phase 2: User Story 1 - Cancel an unfinished analysis run (Priority: P1) 🎯 MVP

**Goal**: Club Admins and Coaches cancel `Requested` or `Running` runs through `cancelAnalysisRun` with `If-Match` and an optional reason; the run records who, when, and why, with one audit event and one run-state-changed notification; repeats are idempotent and terminal runs are refused.

**Independent Test**: Through the member API, cancel a `Requested` and a `Running` run as a Coach of the owning team; verify state, member, time, reason, audit event, notification, idempotent repeat, and refusals for Viewers (403), other teams (404), missing (428) or outdated (412) versions, invalid reasons (400), and completed runs (409 `analysis-run-terminal`).

### Implementation for User Story 1

- [x] T007 [US1] Add the audit event constants `analysis.run-cancelled` / action `cancel-analysis-run` to `src/platform/SocAlytics.Platform.Application/Analysis/Results/AnalysisAuditEvents.cs`, implement `CancelAnalysisRunCommand` and `CancelAnalysisRunHandler` in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/CancelAnalysisRun.cs` (one `IUnitOfWork`; authorize with `AuthorizeTeamResourceAsync(new("analysis-run", runId), TeamPermission.Write)` mapping `Forbidden` to 403 and other denials to not found; lock the run; validate the reason through `RunCancellation`; return the run unchanged for an idempotent repeat; refuse other terminal states with `Conflict("analysis-run-terminal")` carrying the state before the version check; check the version; transition the run; record the audit event through `IAuditTrail.RecordAsync` without the reason; enqueue one `analysis.run-state-changed` message through the existing schema-validated outbox path used by `RunReadinessCoordinator`), and register the handler in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (FR-001–FR-007)
- [x] T008 [US1] Map `POST /analysis/runs/{runId:guid}/cancellation` as `cancelAnalysisRun` (tag `Analysis runs`) on `MapMemberApi()` with `IfMatchHeader.Parse`, an optional JSON body `{ reason }`, `ProblemResults` for 400, 401, 403, 404, 409, 412, and 428, and a `200` `AnalysisRunStatusDto`; add the additive fields `cancelledBy { memberId, accountName }` and `cancellationReason` to `AnalysisRunSummaryDto` and `AnalysisRunStatusDto` and `cancelled` to `AnalysisJobCountsDto` in `src/platform/SocAlytics.Platform.Api/Analysis/AnalysisRunEndpoints.cs` (contracts/cancel-analysis-run-api.md; FR-001, FR-016)
- [x] T009 [US1] Refresh the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` and confirm `OpenApiSnapshotTests` and `OpenApiContractTests` in `src/platform/Tests/SocAlytics.Platform.Host.Tests/` pass with the new operation and additive fields (FR-001, FR-016)

### Tests for User Story 1

- [x] T010 [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCancellationTests.cs`: Club Admin and Coach cancel `Requested` and `Running` runs (state, `terminatedAt`, `cancelledBy` with account name, reason, incremented version in the response and in `getAnalysisRun` and `listMatchAnalysisRuns`); idempotent repeats with the pre-cancellation and the current version (no second audit event or outbox message); concurrent cancellations with the same version (one audit event, one notification); refusals for Viewer (403), member of another team (404), missing (428) and outdated (412) `If-Match`, over-long or control-character reasons (400), and `Completed`, `PartiallyCompleted`, and `Failed` runs (409 `analysis-run-terminal`) leaving the run unchanged; exactly one schema-valid `analysis.run-state-changed` outbox row without the reason and one `analysis.run-cancelled` audit event per cancellation; a cancellation racing the run's own terminal transition keeps the first committed state (FR-001–FR-007, FR-016, FR-021; SC-005)

**Checkpoint**: User Story 1 is fully functional and testable through the API

---

## Phase 3: User Story 2 - Cancelled work stops and can never be accepted (Priority: P1)

**Goal**: The cancellation transaction cancels unfinished nodes and jobs, closes active attempts, and discards pending ready-work; every later claim and attempt call is refused as obsolete; workers leave cancelled runs alone; the Manager stops its container as best effort; accepted results stay unchanged.

**Independent Test**: Cancel a run with one claimed attempt and further ready and waiting jobs; no ready-work is published, queued claims answer `claim-obsolete`, heartbeat, result grants, upstream read, and completion answer `attempt-obsolete`, workers change nothing, accepted results are byte-for-byte unchanged, and with the simulated runtime the Manager removes the attempt's container and data.

### Cancellation side effects and worker guards

- [x] T011 [P] [US2] Extend `CancelAnalysisRunHandler` in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/CancelAnalysisRun.cs` to cancel, inside the same transaction and after the run lock, the run's unfinished nodes (`Waiting`, `Active`) and logical jobs (`Waiting`, `Ready`, `Claimed`) in id order, close every `Active` attempt as `Cancelled` with close reason `run-cancelled` without consuming attempt budget or requeueing, discard the run's `pending` or `failed` `analysis.ready-work` outbox rows, and record the fenced attempts through the existing `AnalysisMetrics` with reason `run-cancelled` (FR-005, FR-008, FR-010, FR-011, FR-012)
- [x] T012 [P] [US2] Make `RunReadinessCoordinator` in `src/platform/SocAlytics.Platform.Application/Analysis/RunReadinessCoordinator.cs` return without changes for runs that are not `Requested` or `Running`, so no readiness, publication, node completion, or terminal evaluation follows for a cancelled run, including from a completion committed before the cancellation (FR-007, FR-013)
- [x] T013 [P] [US2] Restrict publication reconciliation to `Ready` jobs of `Requested` or `Running` runs and never recreate or republish ready-work of cancelled runs in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/ReconcileAnalysisPublication.cs` and the query it uses in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Messaging/PublicationReconciliationStore.cs` (FR-008, FR-013)

### Tests for User Story 2

- [x] T014 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCancellationFencingTests.cs`: after cancelling a run with ready, waiting, and claimed jobs, no ready-work message is published (outbox rows `discarded`, NATS receives none) and reconciliation recreates none; claims for queued messages answer `claim-obsolete` and create no attempt; heartbeat, result grants, upstream read, and completion of the closed attempt answer `attempt-obsolete`; the Job Monitor (`ExpireStaleAttempts`), readiness, and reconciliation workers change nothing, and a redelivered recordings-finalized notification for the run's recording-set version reports the existing run without changing it; nodes and unfinished jobs report `Cancelled` with `cancelled` job counts; attempt budgets are unchanged; a completion committed before the cancellation stays accepted with no further readiness; results accepted before the cancellation stay byte-for-byte unchanged and readable through the existing reads; other runs of the same match are unaffected (FR-007–FR-013, FR-015, FR-021; SC-002, SC-003, SC-006)
- [x] T015 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/RunCancellationEndToEndTests.cs` using `Support/EndToEndHarness.cs`: with the simulated runtime holding an active attempt of a running run, a Coach cancels the run through the member API; the Manager's next heartbeat is refused, the Manager stops the container and deletes the attempt directory within two heartbeat intervals, reports nothing, and no result of the run is accepted afterwards (FR-010, FR-014, FR-021; SC-004)

**Checkpoint**: User Stories 1 and 2 work; cancellation is enforced end to end

---

## Phase 4: User Story 3 - Cancel from the match page in the Web UI (Priority: P2)

**Goal**: Club Admins and Coaches cancel `Requested` or `Running` runs from the match page's run list through a confirmation dialog with an optional reason; everyone who may read the match sees cancelled runs with member, time, and reason.

**Independent Test**: In the simulated browser project and against the platform, a Coach cancels a running run with a reason and sees it as cancelled without a reload; a Viewer sees no action; a conflict shows the existing message and the refreshed list.

### Adapter and dialog

- [x] T016 [P] [US3] Add `version`, `cancelledBy?: { memberId, accountName }`, and `cancellationReason?` to `AnalysisRunSummary` in `src/clients/web/src/platform/models/recordings.ts`, map them in `toAnalysisRun`, and add `cancel(runId, version, reason?)` that sends `If-Match` and `{ reason }` with the existing `withIfMatch` and `withJson` helpers and maps the `AnalysisRunStatusDto` response in `src/clients/web/src/platform/adapters/analysisRunAdapter.ts`, over the Kiota client that `npm run build` regenerates from the refreshed snapshot, and update existing unit-test fixtures that construct `AnalysisRunSummary` (contracts/web-ui-cancel-action.md; FR-016, FR-018, FR-020)
- [x] T017 [P] [US3] Add `CancelRunDialog` in `src/clients/web/src/features/lineage/CancelRunDialog.tsx` (consequence text, optional multiline reason with a counter and a 500-character limit, dismiss as default focus, `Escape` to dismiss, focus return, disabled confirmation and announced progress while sending) and its translation keys in `src/clients/web/src/i18n/en/lineage.json` (FR-018, FR-020)

### Run list integration

- [x] T018 [US3] Show the cancel action for `Requested` and `Running` runs only when `canEditTeamMatches(member, teamId)`, open `CancelRunDialog`, call `cancel` with the run's version, update the run in the `RUNS_KEY` cache and refetch, show refusals through the existing `FailureAlert` and refetch, and display cancelled runs as `Cancelled · by <accountName> · <end time> · <reason>` with the reason rendered as text in `src/clients/web/src/features/lineage/AnalysisRunStates.tsx` (with the team id passed from `src/clients/web/src/features/matches/MatchPage.tsx` if not already available) and the display keys in `src/clients/web/src/i18n/en/lineage.json` (FR-017–FR-020)

### Tests for User Story 3

- [x] T019 [P] [US3] Add unit tests for the adapter's `If-Match`, body, and mapping in `src/clients/web/tests/unit/platform/adapters/analysisRunAdapter.test.ts`, and for action visibility by role and state, dialog behavior, reason limit, refusals, and the cancelled display in the new `src/clients/web/tests/unit/features/lineage/runs/runCancellation.test.tsx` (FR-017–FR-021)
- [x] T020 [P] [US3] Add the cancel route and cancelled-run data to `src/clients/web/tests/e2e/support/simulatedApi.ts` and `src/clients/web/tests/e2e/simulated/run-cancellation.spec.ts`: a Coach cancels a running run through the dialog with a reason and sees the cancelled state without a reload; a Viewer sees no action; dismissing changes nothing; a 412 or 409 shows the conflict message and refreshes the list; keyboard flow and axe checks pass (FR-017–FR-021; SC-001)
- [x] T021 [P] [US3] Add `src/clients/web/tests/e2e/platform/run-cancellation.spec.ts` and a chained `platform-cancellation` project after `platform-upload` in `src/clients/web/playwright.config.ts` that cancels the run created by the upload flow on the running platform and sees it as cancelled with the reason (FR-021)

**Checkpoint**: All user stories work independently

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Architecture truth at merge and end-to-end validation

- [x] T022 Make `docs/architecture/` describe the merged state: turn the decided-not-implemented cancellation statements in `docs/architecture/job-processing.md` (Run State And Recovery), `docs/architecture/analyst-runtime-and-recovery.md` (Execution Attempts, Heartbeats, And Recovery), and `docs/architecture/client-applications.md` (Web UI) into present tense, add run cancellation to the development and browser-test evidence in `docs/architecture/platform-implementation.md`, and correct every other statement the feature makes false, re-reading `docs/architecture/`, `AGENTS.md` "Current State", `src/platform/README.md`, `src/clients/README.md`, and the root `README.md` at implementation time; the rewrites listed under Architecture Updates in plan.md are examples, not the whole job; then run `node .github/scripts/check-markdown.mjs`
- [x] T023 Run the validation in `specs/archive/20261017-182600-analysis-run-cancellation/quickstart.md` (platform restore, build, integration, host, contract, and end-to-end filters; Web UI `npm ci`, `npm run build`, `npm test`, `npm run test:e2e -- --project simulated`, and the platform chain) and fix any failure in the files of the task that caused it

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: No dependencies; blocks all user stories. T004 needs T002; T005 and T006 need T001 and T002.
- **User Story 1 (Phase 2)**: Depends on Phase 1. T008 needs T007; T009 needs T008; T010 needs T007–T009.
- **User Story 2 (Phase 3)**: Depends on Phase 1 and on T007 (the handler it extends). T014 and T015 need T011–T013.
- **User Story 3 (Phase 4)**: Depends on T008 and T009 (operation and snapshot for the regenerated client). T018 needs T016 and T017; T019–T021 need T018.
- **Polish (Phase 5)**: Depends on all stories.

### User Story Dependencies

- **US1 (P1)**: Independent after Phase 1; the MVP.
- **US2 (P1)**: Extends the US1 handler; independently testable with its own tests.
- **US3 (P2)**: Uses the US1 operation; does not need US2 to be demonstrated.

### Parallel Opportunities

- Phase 1: T001, T002, T003 (migration, domain, contract example); then T005 and T006 (stores and read models).
- Phase 3: T011, T012, T013 (handler side effects, readiness guard, reconciliation guard); then T014 and T015 (integration and end-to-end tests).
- Phase 4: T016 and T017 (adapter and dialog); then T019, T020, T021 (unit, simulated, and platform browser tests).

---

## Parallel Example: User Story 2

```text
Task: "T011 Extend CancelAnalysisRunHandler in .../Analysis/Commands/CancelAnalysisRun.cs"
Task: "T012 Guard RunReadinessCoordinator in .../Analysis/RunReadinessCoordinator.cs"
Task: "T013 Guard publication reconciliation in .../Commands/ReconcileAnalysisPublication.cs and .../Messaging/PublicationReconciliationStore.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Foundational.
2. Complete Phase 2: User Story 1.
3. **Stop and validate**: cancel runs through the API with `RunCancellationTests`.

### Incremental Delivery

1. Foundational → states, stores, and reads ready.
2. US1 → member cancellation with audit and notification (MVP).
3. US2 → enforcement through fencing, publication, and workers, with end-to-end evidence.
4. US3 → Web UI cancel action.
5. Polish → architecture truth and quickstart validation.

---

## Notes

- `[P]` tasks under one heading run in parallel from the same commit; their files are disjoint.
- The migration number is assigned at implementation time as the next free number; renumber before merge if it was taken.
- No new table, so `PersistedTableClassifications` stays unchanged; `analysis_runs` keeps its `version` trigger.
- Never refresh the OpenAPI snapshot in the verification environment; T009 refreshes it in development and commits it.

---

## Phase 6: Convergence

- [x] T024 Record the run's match in the `analysis.run-cancelled` audit event by passing `run.MatchId` from `CancelAnalysisRunHandler` in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/CancelAnalysisRun.cs` to `AnalysisAuditEvents.RunCancelled` in `src/platform/SocAlytics.Platform.Application/Analysis/Results/AnalysisAuditEvents.cs` (a match-id detail next to the run id), and assert the match id in the audit-event checks of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCancellationTests.cs` per FR-005 (partial)
