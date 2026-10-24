---

description: "Task list for Explicit Analysis Run Request"
---

# Tasks: Explicit Analysis Run Request

**Input**: Design documents from `/specs/20261023-015644-explicit-analysis-rerun/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/request-analysis-run.md, quickstart.md

**Tests**: Integration and Host (OpenAPI snapshot) tests are part of the plan (Constitution Principle IV), so test tasks are included. The existing architecture tests are run in T022; no new architecture test is added. No Web UI control, no new JSON Schema, and no new project are added.

**Organization**: Tasks are grouped by user story. All paths are relative to the repository root; `PLATFORM` means `src/platform` and `ITESTS` means `src/platform/Tests/SocAlytics.Platform.Integration.Tests`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies on each other)
- **[Story]**: Which user story this task belongs to (US1 to US4)

## Phase 1: Setup (Persistence Foundation)

**Purpose**: The migration and the table classification that every later phase builds on.

### Migration and classification

- [x] T001 [P] Create the forward-only migration `PLATFORM/SocAlytics.Platform.Infrastructure/Persistence/Migrations/0032_analysis_explicit_requests.sql` (use the next free number on an up-to-date default branch and rename if 0032 is taken): create `socalytics.analysis_run_requests` exactly as in data-model.md (`idempotency_key` text primary key of 1 to 255 visible ASCII characters; `match_id` references `socalytics.match (id)`; `requested_by` references `socalytics.member_account (id)`; `request_id` uuid not null unique referencing `analysis_notification_receipts (message_id)`; `outcome` in `run-created`, `run-exists`, `rejected`; `rejection_category` null unless `outcome = 'rejected'`, then one of `no-finalized-recording-set`, `not-configured`, `invalid-definition`; `recording_set_version_id` null only for `no-finalized-recording-set`; `run_id` null exactly when `outcome = 'rejected'`; `current_run_id` references `analysis_runs (id)`; `created_at` timestamptz not null), attach the `reject_immutable_change` trigger on update and delete, `REVOKE UPDATE, DELETE` from `socalytics_app`, and drop and re-add `ck_analysis_notification_receipts_type` (`recordings-finalized`, `analysis-requested`) and `ck_analysis_notification_receipts_category` (existing `schema-invalid`, `lineage-not-found`, `lineage-mismatch`, `scope-mismatch` plus `no-finalized-recording-set`, `not-configured`, `invalid-definition`); no `version` column, no other index, trigger, or grant change
- [x] T002 [P] Classify `analysis_run_requests` as an immutable record (no `version` column) in `ITESTS/Structure/PersistedTableClassifications.cs`

**Checkpoint**: The schema exists; `dotnet build src/platform/SocAlytics.Platform.slnx` succeeds.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The shared run creation, the current-recording-set lookup, the idempotency store, and the audit actions that all user stories need.

**CRITICAL**: No user story work can begin until this phase is complete.

### Shared run creation

- [x] T003 Extract the run-creation body of `HandleRecordingsFinalizedNotificationHandler.ExecuteAsync` after the lineage checks (lock the match's runs with `LockMatchRunsAsync`, find an existing run by recording-set version, workflow, and workflow version, map the snapshot, validate the graph, insert the run or a `Failed` run, plan jobs, request segments, insert jobs, apply supersession, start readiness) into the new `AnalysisRunCreator.CreateOrFindAsync(lineage, definition, ct)` returning `(Guid RunId, bool Created)` in `PLATFORM/SocAlytics.Platform.Application/Analysis/AnalysisRunCreator.cs`; change `PLATFORM/SocAlytics.Platform.Application/Analysis/Commands/HandleRecordingsFinalizedNotification.cs` to keep its receipt, lineage-mismatch, and workflow-resolution handling and call the creator with unchanged behavior; register the creator in `PLATFORM/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; then run the existing analysis run-creation, supersession, and finalization-consumer tests in `ITESTS/Analysis` to prove behavior is unchanged

### Lookup, store, audit, and schema tests

- [x] T004 [P] Add `GetCurrentForMatchAsync(matchId)` to `PLATFORM/SocAlytics.Platform.Application/Recordings/IRecordingSetLookup.cs` and implement it in `PLATFORM/SocAlytics.Platform.Infrastructure/Recordings/RecordingSetLookup.cs`: the `RecordingSetLineage` of the match's latest finalized set (`ORDER BY finalized_at DESC, id DESC LIMIT 1`, then the same member join as `GetAsync`), `null` when none; update any test double of the interface so it still compiles; its lineage (members, timeline mapping, `finalized_at`) is verified in T010 to equal the one the finalization path builds for the same recording-set version
- [x] T005 [P] Create `IAnalysisRunRequestStore` (find a record by idempotency key; insert the record of a decided request in the caller's unit of work, losing on the primary key to a concurrent duplicate) in `PLATFORM/SocAlytics.Platform.Application/Analysis/Stores/IAnalysisRunRequestStore.cs`, implement it with Dapper in `PLATFORM/SocAlytics.Platform.Infrastructure/Persistence/Analysis/AnalysisRunRequestStore.cs`, and register it in `PLATFORM/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`
- [x] T006 [P] Create the `analysis.run.request` audit constants (event type `analysis.run.request`, action `request-analysis-run`, resource `analysis-run`, details `matchId`, `recordingSetVersionId`, `outcome`) mirroring `RecordingAuditActions` in `PLATFORM/SocAlytics.Platform.Application/Analysis/AnalysisAuditActions.cs` (extend the file if it exists)
- [x] T007 [P] Extend `ITESTS/Analysis/AnalysisSchemaTests.cs` for the new message type `analysis-requested`, the three new rejection categories, the `analysis_run_requests` constraints (outcome, run and category nullability rules), and its immutability (update and delete refused)

**Checkpoint**: Foundation ready; the existing analysis tests and `AnalysisSchemaTests` pass.

---

## Phase 3: User Story 1 - Repeat the analysis of a finalized match (Priority: P1) 🎯 MVP

**Goal**: A Club Admin or Coach of the owning team asks for a run of the match's current finalized recording-set version and gets `202` with the run identity and status link; the run is created by the shared run creation and older runs are superseded.

**Independent Test**: With a finalized match whose latest run used an earlier workflow version, change the default workflow, have a Coach request analysis, and see a new run for the same recording-set version and new workflow version, readable through the returned link, the old run superseded by it, and one security audit event.

- [x] T008 [US1] Implement the `RequestAnalysisRun` command, result, and handler (the creation path: authorize `TeamPermission.Write` through `IAccessAuthorizer` and `ITeamScopeSource` with 401, 403, 404, and `season-archived` 409 refusals; validate the `Idempotency-Key` of 1 to 255 characters from `U+0021` to `U+007E`; read the current finalized set with `GetCurrentForMatchAsync`; resolve the default workflow as the finalization path does; call `AnalysisRunCreator`; in one unit of work write the run, the `analysis-requested` receipt with a fresh version 7 `message_id = request_id` and outcome `run-created`, the `analysis_run_requests` record, and exactly one `analysis.run.request` audit event) in `PLATFORM/SocAlytics.Platform.Application/Analysis/Commands/RequestAnalysisRun.cs`, and register the handler in `PLATFORM/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`
- [x] T009 [US1] Add the `POST /api/v1/matches/{matchId}/analysis-runs` route (`requestAnalysisRun`, tag `Analysis runs`, `MapMemberApi()`, required `Idempotency-Key` header, no body, no `If-Match`) with `AnalysisRunRequestAcceptedDto` (`runId`, `created`, `state`, `currentRunId`, `recordingSetVersionId`, `workflowId`, `workflowVersion`, `statusLink`), `202 Accepted` with `Location: /api/v1/analysis/runs/{runId}`, and `ProblemResults` mapping in `PLATFORM/SocAlytics.Platform.Api/Analysis/AnalysisRunEndpoints.cs`
- [x] T010 [US1] Create `ITESTS/Analysis/ExplicitAnalysisRequestTests.cs` with the User Story 1 scenarios: Club Admin and Coach get `202` with a new run in state `Requested` or later for the current recording-set version and resolved workflow version; snapshot, complete graph, segment requests, and readiness equal those of the finalization path for the same inputs; older runs are superseded with the new run as successor; exactly one `analysis.run.request` audit event; a single request on the standard test fixture (one match with one finalized recording set and the seeded default workflow) is answered synchronously within a generous fixed ceiling of 10 seconds (SC-001); a Viewer gets 403, a member without a role gets 404, an unauthenticated caller gets 401, and none of these creates a run, receipt, or audit event; a request with a valid session but a missing or invalid anti-forgery token is refused and creates no run, receipt, request record, or audit event (FR-002); a finalized set followed by an unfinalized later revision is analyzed as the finalized version; a newer finalized version is used instead of an older one; a request before the finalization notification is processed creates the run and the later notification returns it as run-exists; rejection of a segment request records the same per-job reason as the finalization path

**Checkpoint**: User Story 1 works and passes independently (the MVP).

---

## Phase 4: User Story 2 - A repeated request never duplicates a run (Priority: P1)

**Goal**: Existing runs are returned instead of duplicated, same-key replays return the recorded answer, and concurrent requests resolve to one run.

**Independent Test**: Request twice without a registry change (same run, one run exists), replay a key (identical answer, no second audit event), and run two simultaneous requests (one run, both answers name it).

- [ ] T011 [US2] Extend `PLATFORM/SocAlytics.Platform.Application/Analysis/Commands/RequestAnalysisRun.cs` with: same-key replay by the same member for the same match returning the recorded answer rebuilt from the `analysis_run_requests` row (originally recorded `created` and `currentRunId`, the run's state read live, no evaluation, no audit event); `409 idempotency-key-reused` for another member or match; the existing-run path (an existing run of the recording-set version, workflow, and workflow version in any state is returned with `created: false`, outcome `run-exists`, no run created, no supersession, no segment requests, `currentRunId` computed with `IRunSupersessionStore.ListMatchRunsAsync` and `RecordingSetVersionOrder.SelectCurrent` and set only when it differs from the returned run); the existing-run path writes the `analysis-requested` receipt with outcome `run-exists`, the request record, and exactly one `analysis.run.request` audit event with outcome `run-exists` in the same unit of work; a concurrent duplicate that loses on the primary key rolls back and replays the winner's record
- [ ] T012 [US2] Extend `ITESTS/Analysis/ExplicitAnalysisRequestTests.cs` with the User Story 2 scenarios: a second request returns the same run with `created: false` and records exactly one audit event with outcome `run-exists`, and a same-key replay of that answer records none; a same-key replay returns the originally recorded run, `created` flag, and `currentRunId` (the run's state is read at replay time; the assertion compares those fields, not the whole body) even after the default workflow changed and records no further audit event; key reuse for another match or member is `409 idempotency-key-reused` and changes nothing; two simultaneous requests and a race with the finalization path yield exactly one run (SC-002); a changed workflow version creates a new run and supersedes the earlier one; a cancelled or failed run for the unchanged workflow version is returned with its state; a superseded run returned after the default workflow was changed back carries the match's current run in `currentRunId`

**Checkpoint**: User Stories 1 and 2 both work independently.

---

## Phase 5: User Story 3 - Requests that cannot start an analysis are refused clearly (Priority: P1)

**Goal**: Impossible requests are refused with a named problem, recorded as `rejected` where required, and create or change no run.

**Independent Test**: Request analysis for a match without a finalized set, while the default workflow is unresolvable, without a key, and for an unknown match; each is refused with its own problem and nothing changes.

- [ ] T013 [US3] Extend `PLATFORM/SocAlytics.Platform.Application/Analysis/Commands/RequestAnalysisRun.cs` with the refusal paths: no finalized set records a `rejected` receipt and request record with category `no-finalized-recording-set` and refuses `409 no-finalized-recording-set`; an unresolvable default workflow (`not-configured`, `invalid-definition`) records `rejected` with that category and refuses `409 workflow-unresolvable` with the category in the problem extension; a Registry or lineage read failure refuses `503 analysis-dependency-unavailable` with no receipt and no request record; a replayed `rejected` key rebuilds the same problem from the stored `rejection_category`; a graph that fails run-creation validation ends as the `Failed` run of the shared creator and is answered with that run
- [ ] T014 [US3] Map the problem codes `no-finalized-recording-set`, `workflow-unresolvable`, `idempotency-key-reused`, and `analysis-dependency-unavailable` (retryable) and the `400` violation on `Idempotency-Key` through `OperationFailure` and `ProblemResults` in `PLATFORM/SocAlytics.Platform.Api/Analysis/AnalysisRunEndpoints.cs`
- [ ] T015 [US3] Extend `ITESTS/Analysis/ExplicitAnalysisRequestTests.cs` with the User Story 3 scenarios: no finalized set and a match with uploaded but not finalized recordings are refused with a `rejected` receipt; no, retired, or unapproved default workflow is refused with the category and a `rejected` receipt; missing or malformed key is `400` with no receipt; unknown match is `404`; an unavailable Registry is `503` with no receipt and a safe repeat with the same key; a replayed `rejected` key returns the original refusal after the cause was fixed while a new key succeeds; a failing graph validation yields a `Failed` run with supersession as in the finalization path; a request for a match of an archived season is refused `409 season-archived` and creates no run, receipt, request record, or audit event; no refusal creates or changes a run or records an `analysis.run.request` audit event, including the recorded `rejected` outcomes (SC-003)

**Checkpoint**: User Stories 1 to 3 work independently.

---

## Phase 6: User Story 4 - The request is durable and visible to operations (Priority: P2)

**Goal**: Explicit-request receipts appear in the operations summary and receipt list with their message type, interrupted requests resolve to one outcome, and reconciliation treats their runs like any other.

**Independent Test**: Make one creating, one existing-run, and one rejected request; the summary counts and the receipt list show all three with message type `analysis-requested`, outcome, recording-set version, and run; repeating an interrupted request returns the created run without a second one.

### Operations visibility

- [ ] T016 [P] [US4] Create `ITESTS/Analysis/ExplicitAnalysisRequestOperationsTests.cs`: Club Admin reads of `/api/v1/operations/analysis/summary` and `/api/v1/operations/analysis/notification-receipts` count and list receipts of explicit requests with the outcome filter and paging as before, show the message type `analysis-requested` (and never a payload in message detail), and place a rejected request with its category in the rejected counts; a request interrupted after run creation and repeated with its key yields the recorded outcome and no second run; the analysis reconciliation neither creates nor deletes runs of explicit requests and its outcome is unchanged for runs of both paths. If a failing assertion shows an operations read or its DTO does not expose the new message type or categories, fix that read in `PLATFORM/SocAlytics.Platform.Infrastructure/Persistence/Analysis/` and the matching DTO in `PLATFORM/SocAlytics.Platform.Api/Analysis/` (the minimal change; no query change is expected)
- [ ] T017 [P] [US4] Check that the Web UI workflow operations area in `src/clients/web/src` renders the new message type `analysis-requested` and the new rejection categories without error (no new control is added); change only the receipt label or category mapping, and its unit test, if a value is not handled, then run `npm test` in `src/clients/web`

**Checkpoint**: All user stories are independently functional.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: The committed contract, the architecture and status documentation, and the final verification.

### Contract snapshot

- [ ] T018 Refresh the committed OpenAPI snapshot with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`, confirm the diff contains `requestAnalysisRun`, its `Idempotency-Key` parameter, the `202` response, and the problem results of contracts/request-analysis-run.md, and run the test again without the variable; then run `npm run build` in `src/clients/web` so the regenerated Kiota client builds

### Documentation

- [ ] T019 [P] Make `docs/architecture/job-processing.md` and `docs/architecture/overview.md` true for the merged state: name `requestAnalysisRun` on the explicit branch of the analysis request flow sequence diagram, say it applies the complete-rerun rule and the same supersession through the shared run creation, state in Implementation Validation that the explicit request is implemented and evidenced, and say the "Explicit demand" edge is implemented as a member operation without a Web UI control; correct every planned, future-tense, or unimplemented wording about explicit analysis demand
- [ ] T020 [P] Make `docs/architecture/platform-implementation.md`, `docs/architecture/client-applications.md`, and `docs/architecture/contracts-and-compatibility.md` true for the merged state: add explicit requests, idempotent replay, duplicate and race protection, and operations visibility to the analysis run evidence and test list, remove "not implemented" wording about explicit analysis demand, state that the workflow operations receipts cover finalization and explicit analysis requests distinguished by message type (the Web UI gains no control), and state that the operation joins the committed OpenAPI contract with no new JSON Schema
- [ ] T021 [P] Update the current-state statements in `README.md`, `src/platform/README.md`, and `AGENTS.md` to list the explicit analysis request (`POST /api/v1/matches/{matchId}/analysis-runs`) as development evidence while keeping "no Web UI control" explicit and production values unresolved

### Final verification

- [ ] T022 Run `node .github/scripts/check-markdown.mjs`, fix findings in the changed documentation, then run the whole `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests` project, the Host and architecture tests, and the scenarios of quickstart.md, and fix any regression the feature caused

---

## Dependencies & Execution Order

- **Setup (Phase 1)**: no dependencies; T001 and T002 run in parallel.
- **Foundational (Phase 2)**: needs Phase 1; T003 first, then T004 to T007 in parallel. It blocks every user story.
- **User Story 1 (Phase 3)**: needs Phase 2; T008 → T009 → T010.
- **User Story 2 (Phase 4)**: needs User Story 1 (it extends the same handler and test file); T011 → T012.
- **User Story 3 (Phase 5)**: needs User Story 2; T013 → T014 → T015.
- **User Story 4 (Phase 6)**: needs User Story 3 for the rejected outcome; T016 and T017 run in parallel.
- **Polish (Phase 7)**: T018 needs all endpoint work; T019 to T021 in parallel after the behavior is final; T022 last.

### Parallel Opportunities

- Phase 1: T001 and T002.
- Phase 2 after T003: T004, T005, T006, and T007.
- Phase 6: T016 and T017.
- Phase 7: T019, T020, and T021.

## Implementation Strategy

### MVP First

Complete Phases 1 and 2, then User Story 1 (T008 to T010) and validate: a Coach's request creates a run, supersedes older runs, and is audited. Do not ship before User Stories 2 and 3, because duplicate protection and clear refusals are part of the minimum correct behavior (all three are P1).

### Incremental Delivery

1. Setup and Foundational: the schema, the shared `AnalysisRunCreator`, and the stores, with the finalization path proven unchanged.
2. User Story 1, then 2, then 3, each extending the same handler and test file and verified before the next.
3. User Story 4 operations evidence, then the OpenAPI snapshot, the documentation, and the final verification.
