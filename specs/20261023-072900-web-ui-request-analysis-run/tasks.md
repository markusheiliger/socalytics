# Tasks: Web UI Request of a New Analysis Run

**Input**: Design documents from `/specs/20261023-072900-web-ui-request-analysis-run/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/ui-outcomes.md](contracts/ui-outcomes.md), [quickstart.md](quickstart.md)

**Tests**: Required by FR-018 (unit tests, `simulated` and `platform` browser tests).

**Organization**: Tasks are grouped by user story. All paths are relative to `src/clients/web/` unless they start with
`docs/`, `README.md`, or `AGENTS.md`. Texts live in `src/i18n/en/lineage.json` under `requestAnalysis.*`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no
  dependencies on each other or on incomplete tasks)
- **[Story]**: User story the task belongs to (US1 to US4)

## Phase 1: Setup

- [ ] T001 Confirm the prerequisite feature [Explicit Analysis Run Request](../20261023-015644-explicit-analysis-rerun/spec.md)
  is merged: the generated Kiota client under `src/platform/` of the Web UI (`requestAnalysisRun`,
  `POST /api/v1/matches/{matchId}/analysis-runs`) and the committed OpenAPI snapshot must contain the operation; if not,
  stop and report that the twin dependency is unmet (do not regenerate the client or change the snapshot in this feature)

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: Adapter, models, failure mapping, shared intent-key rule, texts, and fake platform used by every story.

### Foundation modules

- [ ] T002 [P] Create the shared intent-key rule in `src/features/shared/intentKey.ts` (create on first send, reuse while
  the outcome is unknown, drop on a definite answer; `crypto.randomUUID()`, 36 characters of `U+0021..U+007E`; memory only),
  extracted from `src/features/triage/useQuestionDraft.ts` without changing its behavior
- [ ] T003 [P] Extend `src/platform/failure.ts`: `parseProblem` keeps the problem's `category` extension, and the codes
  `no-finalized-recording-set`, `workflow-unresolvable`, and `idempotency-key-reused` get dedicated kinds instead of the
  generic `conflict` (and `programmingError`), a `503` problem with code `analysis-dependency-unavailable` keeps its own
  kind instead of the generic `unreachable` (which stays for a network failure without a response), keeping the existing
  `seasonArchived` kind and all current behavior
- [ ] T004 [P] Add the `AnalysisRunRequestOutcome` model (`runId`, `created`, `state`, `currentRunId?`, `workflowId`,
  `workflowVersion`) and its mapping from the `202` answer in `src/platform/models/recordings.ts`
- [ ] T005 [P] Add the English texts for the confirmation, the pending state, every outcome key and refusal key of
  [contracts/ui-outcomes.md](contracts/ui-outcomes.md), the disabled-reason texts, and the connection-needed text under
  `requestAnalysis.*` in `src/i18n/en/lineage.json` (plain language; never name a workflow or version before the answer)

### Foundation consumers

- [ ] T006 [P] Make `src/features/triage/useQuestionDraft.ts` use the shared rule from `src/features/shared/intentKey.ts`
  (behavior and existing triage tests unchanged)
- [ ] T007 [P] Add `requestRun(matchId, key)` to `src/platform/adapters/analysisRunAdapter.ts`: calls `requestAnalysisRun`
  through the generated client with `withIdempotencyKey`, no body, no `If-Match`, returns `AnalysisRunRequestOutcome`
- [ ] T008 [P] Create `src/features/lineage/requestAnalysisOutcome.ts` mapping a `202` outcome or a `PlatformFailure` to
  an outcome key, its severity (`status` or `alert`), whether the same key may be repeated, and the follow-up re-reads,
  exactly per [contracts/ui-outcomes.md](contracts/ui-outcomes.md); no rule moves into the client
- [ ] T009 [P] Add the scriptable fake `POST /api/v1/matches/{id}/analysis-runs` to `tests/e2e/support/simulatedApi.ts`
  (created, exists, exists with other current run, each refusal of the contract, delayed answer, dropped connection,
  `503`) that records every received `Idempotency-Key` and request count and updates the fake run list as the platform would

### Foundation tests

- [ ] T010 [P] Unit test the shared intent-key rule in `tests/unit/features/shared/intentKey.test.ts`
- [ ] T011 [P] Unit test the new failure kinds, the separate `503` `analysis-dependency-unavailable` kind and the
  network failure `unreachable` kind, and the kept `category` in `tests/unit/platform/failure.test.ts`
- [ ] T012 [P] Unit test `requestRun` (path, `Idempotency-Key`, no body, no `If-Match`, answer mapping) in
  `tests/unit/platform/adapters/analysisRunAdapter.test.ts`
- [ ] T013 [P] Unit test every row of the answer table of [contracts/ui-outcomes.md](contracts/ui-outcomes.md), including
  `temporary` (`503`) and `unreachable` (network failure) as separate outcomes that both keep the key, in
  `tests/unit/features/lineage/runs/requestAnalysisOutcome.test.ts`

**Checkpoint**: `npm run build` and `npm test` pass; the triage tests are unchanged.

---

## Phase 3: User Story 1 - Request a new analysis run from the match page (Priority: P1)

**Goal**: A Club Admin or Coach of the owning team confirms the action and sees a created or already-existing run,
with the run list read again.

**Independent Test**: Sign in as a Coach, open a match with a finalized recording set, request, confirm, and see the
outcome message, the link to the run, and the re-read run list with the returned run (SC-001, SC-002).

- [ ] T014 [US1] Create `src/features/lineage/RequestAnalysisDialog.tsx` (like `CancelRunDialog`): plain-language
  confirmation of what the request does, confirm and cancel actions, nothing sent before confirming, cancel and dismiss
  send nothing, focus handling and labels per the existing dialog pattern (FR-003, FR-004, FR-017)
- [ ] T015 [US1] In `src/features/lineage/AnalysisRunStates.tsx` add the "Request new analysis" action (visible by
  `canEditTeamMatches(member, teamId)`, the cancel action's gate), the pending state that cannot be activated again, the
  call to `analysisRunAdapter.requestRun` with a per-confirmation key from `src/features/shared/intentKey.ts`, the
  `role="status"` outcome area for `created`, `exists`, and `existsOtherCurrent` that always links to the run's review
  route (`/matches/{matchId}/analysis-runs/{runId}`) and adds the list-entry anchor (fragment anchor) only when the run
  is on the loaded page, and one re-read of the runs
  query (`RUNS_KEY`) and the lineage query after a `202` that keeps the outcome if the re-read fails (FR-005, FR-006,
  FR-008, FR-009, FR-016)
- [ ] T016 [P] [US1] Add component tests for the dialog, the pending state, the created and exists outcomes, and a
  returned run outside the loaded page (link to the review route without the list-entry anchor) in
  `tests/unit/features/lineage/runs/runRequest.test.tsx`
- [ ] T017 [P] [US1] Add the `simulated` browser test for the happy path in `tests/e2e/simulated/run-request.spec.ts`:
  nothing sent before confirm, one request on confirm, created, exists, and exists-with-other-current outcomes, the
  re-read list showing currency and supersession as reported, a failed re-read keeping the outcome and sending no second
  request, and axe and keyboard checks of the action, dialog, and outcome (FR-018, SC-001, SC-002, SC-007)
- [ ] T018 [P] [US1] Add the `platform` browser test for created (new current run, older run superseded) and already
  exists in `tests/e2e/platform/run-request.spec.ts` (FR-018)

**Checkpoint**: User Story 1 works and is testable on its own.

---

## Phase 4: User Story 2 - Understand why a request is refused (Priority: P1)

**Goal**: Every platform refusal is explained in plain language with a next step, and refusals that change access
re-read the session, match, and run list.

**Independent Test**: Script each refusal in the fake platform and see a distinct message, an unchanged run list, and
the re-reads of [contracts/ui-outcomes.md](contracts/ui-outcomes.md) (SC-003).

- [ ] T019 [US2] In `src/features/lineage/AnalysisRunStates.tsx` show refusals in a `role="alert"` area from
  `requestAnalysisOutcome.ts` (never by colour alone, the platform's reference shown for the generic text), keep the run
  list unchanged on refusals, re-read the lineage after `noFinalizedSet`, re-read the session member, the match, and the
  run list after `403` and `404`, and leave `401` to the existing session-ended handling (FR-010, FR-011)
- [ ] T020 [P] [US2] Extend `tests/unit/features/lineage/runs/runRequest.test.tsx` with the refusal display and the
  re-read cases
- [ ] T021 [P] [US2] Add the `simulated` browser test for every refusal (no finalized set, each workflow-unresolvable
  category, season archived, key reused, forbidden with the action disappearing, not found, `401` session end, generic
  with reference) in `tests/e2e/simulated/run-request-refusals.spec.ts` (FR-018, SC-003)
- [ ] T022 [P] [US2] Add the `platform` browser test for a match without a finalized recording set and a stamp without
  a resolvable default workflow in `tests/e2e/platform/run-request-refusals.spec.ts` (FR-018)

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - Only the right members see the action, and only while online (Priority: P2)

**Goal**: The action is shown to the right members only, disabled with a stated reason when the lineage shows no finalized
set, and never queued offline.

**Independent Test**: Sign in as each role and see the action only for Club Admins and Coaches of the owning team;
disconnect and see the connection-needed message with nothing stored or queued (SC-005, SC-006).

- [ ] T023 [US3] In `src/features/lineage/AnalysisRunStates.tsx` disable the action with a text reason when the loaded
  lineage has no recording-set version, disable it without a reason claim while the lineage is loading or failed, and
  show the connection-needed message (outcome key `unreachable`, key kept) when the platform cannot be reached at
  confirmation, queueing and retrying nothing in the background (FR-001, FR-002, FR-013, FR-014)
- [ ] T024 [P] [US3] Extend `tests/unit/features/lineage/runs/runRequest.test.tsx` with the visibility and disabled-state
  cases for every role
- [ ] T025 [P] [US3] Add the `simulated` browser test for role gating (Club Admin and Coach shown; Viewer, Analyst, other
  team, other club not shown and nothing sent), the disabled states, offline behavior, and absence of any run data,
  outcome, or key in browser storage, caches, and the console in `tests/e2e/simulated/run-request-access.spec.ts`
  (FR-015, FR-018, SC-005, SC-006)
- [ ] T026 [P] [US3] Add the `platform` browser test for role gating against the real platform in
  `tests/e2e/platform/run-request-access.spec.ts` (FR-018)

**Checkpoint**: User Stories 1 to 3 work independently.

---

## Phase 6: User Story 4 - Repeat, double-submit, and concurrent requests stay safe (Priority: P3)

**Goal**: No duplicate runs from double activation, repeats after transient failures, or concurrent requests.

**Independent Test**: Double-activate, drop the connection and repeat, and request from two pages; at most one run per
distinct analysis results and keys follow the rule of FR-007 and FR-012 (SC-004).

- [ ] T027 [US4] In `src/features/lineage/RequestAnalysisDialog.tsx` and `src/features/lineage/AnalysisRunStates.tsx`
  keep the confirmation's key for a repeat after a network failure (`unreachable`) or `503` (`temporary`), keeping the
  dialog open with the alert text and the confirm action re-enabled for the repeat, drop the key on every other answer
  and when the dialog closes so a later confirmation sends a new key, and block a second activation while pending
  (FR-006, FR-007, FR-012)
- [ ] T028 [P] [US4] Extend `tests/unit/features/lineage/runs/runRequest.test.tsx` with the key lifecycle cases,
  including the dialog staying open with the repeat enabled on `temporary` and `unreachable` and closing dropping the key
- [ ] T029 [P] [US4] Add the `simulated` browser test in `tests/e2e/simulated/run-request-repeat.spec.ts`: double
  activation sends one request, the same key on repeat after a dropped connection and after `503` (dialog still open
  with the alert and the confirm action enabled), a new key after any other answer and after closing, and a second page
  requesting concurrently shows `exists` (FR-018, SC-004)

**Checkpoint**: All user stories work independently.

---

## Phase 7: Polish and Cross-Cutting Concerns

- [ ] T030 [P] Correct `docs/architecture/client-applications.md` (Web UI capability list, connected analysis review
  paragraph, run comparison paragraph) so it states the implemented confirmed request action for Club Admins and Coaches of
  the owning team (online only, explained outcomes and refusals, idempotency key in memory only), and distinguishes the
  analysis review (still no run controls) from the match page's run list, which offers the cancel and request actions;
  re-read the whole document for planned, future-tense, or "does not exist" wording this feature makes false (FR-020)
- [ ] T031 [P] Correct `docs/architecture/job-processing.md` (Run State And Recovery and Durable Workflow Model) so the
  member-initiated run controls name the Web UI's request action next to cancellation, and
  `docs/architecture/platform-implementation.md` if it lists the browser-test coverage of the match page's run list,
  without claiming platform behavior this feature does not add (FR-020)
- [ ] T032 [P] Update the current-state statements of `README.md`, `docs/README.md` if it summarizes the Web UI, and
  `AGENTS.md` (the Web UI foundation list and the connected match workflow wording) to include the request action as
  development evidence
- [ ] T033 Run the checks of [quickstart.md](quickstart.md): `npm run build`, `npm test`, and
  `npm run test:e2e -- --project simulated` in `src/clients/web`, the `platform` project where Docker is available, and
  `node .github/scripts/check-markdown.mjs`; fix what they report

---

## Dependencies and Execution Order

- Phase 1 first. Phase 2 blocks all stories: T002 to T005 (foundation modules), then T006 to T009 (consumers; T007 needs
  T004, T008 needs T003 and T004, T006 needs T002), then T010 to T013.
- US1 (T014 to T018) needs Phase 2. US2, US3, and US4 each change `AnalysisRunStates.tsx`, so they run in the order US1,
  US2, US3, US4 for their implementation tasks; their tests (`[P]` tasks, one parallel group per story) only need the story's implementation.
- US2 needs T015; US3 needs T019 because both edit `AnalysisRunStates.tsx`; US4 needs T014 and T015.
- Phase 7 follows all stories; T030 to T032 may run in any order after the implementation is complete.

## Parallel Example

```text
Phase 2 foundation modules:  T002, T003, T004, T005
Phase 2 foundation tests:    T010, T011, T012, T013
User Story 1 tests:          T016, T017, T018
Polish documentation:        T030, T031, T032
```

## Implementation Strategy

- **MVP**: Phases 1 and 2 plus User Story 1 (the happy path), then User Story 2 (refusals), which together are the
  usable P1 slice.
- **Incremental delivery**: add User Story 3 (gating and online only), then User Story 4 (idempotency hardening), then
  documentation and the final checks.
