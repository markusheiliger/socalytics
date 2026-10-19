---

description: "Task list for Web UI Connected Analysis Review"
---

# Tasks: Web UI Connected Analysis Review

**Input**: Design documents from `specs/20261017-181118-web-ui-analysis-review/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required. FR-022 asks for automated tests of the adapters, every explicit state, paging and ranges, refresh, role-based access, accessibility, the online-only rule, and a browser test against the running platform; test work is part of the tasks below.

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently. All paths are relative to the repository root; Web UI paths start with `src/clients/web/`.

**Before starting**: the batch sibling [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md) must be merged, so the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` contains `listAnalysisRunFactStreams` and `listAnalysisRunFacts`; `npm run build` (or `npm run generate`) in `src/clients/web` regenerates the ignored Kiota client from it. This feature never changes the snapshot.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files never overlap.
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4).
- Every task names the files it creates or changes.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The translation namespace and client models every view and adapter uses.

- [x] T001 Create the `analysis` translation namespace `src/clients/web/src/i18n/en/analysis.json` with English texts for every label, heading, action, and message of the review: run details (state, requested, started, ended, failure reason, workflow), node list (node key, capability, tier, scope, state, blocked by, job counts waiting/ready/claimed/completed/failed/blocked), stream list (capability, schema version, output type, fact count, coverage counts), fact browser (range, kind, confidence, payload summary, "Show full payload", previous/next page, first/last page notes), range form (from, to, apply, clear, the format hint `m:ss` or `m:ss.SSS`, each validation message), coverage (window and the states `accepted`, `missing`, `failed`, `blocked`, `skipped`), provenance (workflow, Analyst profile, image digest, model digest, "Show full digest", copy, copied, copy failed), one message per state of `specs/20261017-181118-web-ui-analysis-review/contracts/review-states.md`, the "new results are available" notice with its reload action, the continuation-restart note, and display names for known run states, node states, tiers, scopes, and output types; register the namespace in `ns` and `resources` of `src/clients/web/src/i18n/index.ts` (FR-019)
- [x] T002 Create the application-owned models of `specs/20261017-181118-web-ui-analysis-review/data-model.md` in `src/clients/web/src/platform/models/analysis.ts`: `AnalysisRunStatus` (with `requestedCapabilities` and `nodes`), `NodeStatus` with job counts, `FactStreamSummary` with coverage counts, `FactPage`, `Fact`, `CoverageWindow`, `Provenance` (`modelDigest` optional), `FactSelection` with `range: { fromMs?: number; toMs?: number }` ("integers, `fromMs ≥ 0`, `toMs > fromMs` when both set"), `JsonValue`, the adapter outcome unions `RunOutcome`, `StreamsOutcome`, and `FactsOutcome` of `contracts/analysis-adapters.md`, and an `isRunInProgress(state)` helper that matches `Requested` and `Running` case-insensitively (FR-006, FR-016)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Adapters, pure helpers, and the simulated API that every story builds on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Adapters, helpers, and simulated platform

- [x] T003 [P] Add `getRun(runId): Promise<RunOutcome>` to `src/clients/web/src/platform/adapters/analysisRunAdapter.ts` over the generated `getAnalysisRun` request (`api.client.api.v1.analysis.runs.byRunId(…)`), mapping the DTO to `AnalysisRunStatus` (missing required fields → `PlatformFailure('incompatible')`), a `404` to `{ kind: 'runNotFound' }`, and every other failure through the shared mapping; extend `src/clients/web/tests/unit/platform/adapters/analysisRunAdapter.test.ts` with mapping, `404`, `403`, and incompatible cases (FR-003, FR-004, FR-005, FR-017)
- [x] T004 [P] Create `src/clients/web/src/platform/adapters/analysisFactAdapter.ts` with `listStreams(runId)` over `listAnalysisRunFactStreams` and `listFacts(request)` over `listAnalysisRunFacts`, always sending `nodeKey`, `capabilityId`, `capabilityVersion`, and `schemaVersion`, optional `fromMs`/`toMs`/`continuationToken`, and no `pageSize`; translate problems locally as in the outcome table of `specs/20261017-181118-web-ui-analysis-review/contracts/analysis-adapters.md` (`404` with code `analysis-capability-not-in-run` → `capabilityNotInRun`, other `404` → `runNotFound`, `400` naming `continuationToken` → `continuationRejected`, `analysis-capability-ambiguous` → `programmingError`) without changing `src/clients/web/src/platform/failure.ts`; normalize each payload from the generated client (untyped node or model with `additionalData`) into `JsonValue`, anything else → `incompatible`; add `src/clients/web/tests/unit/platform/adapters/analysisFactAdapter.test.ts` covering mapping, every outcome, both payload forms, and provenance with and without `modelDigest` (FR-007, FR-008, FR-011, FR-013, FR-017)
- [x] T005 [P] Create `src/clients/web/src/features/analysis/matchClock.ts` with `formatMatchClock(ms)` (`m:ss` with unbounded minutes, `.SSS` only when the milliseconds are not zero), `formatMatchRange(startMs, endMs)` with an accessible "from … to … (end excluded)" label, and `parseMatchClock(text)` accepting only `m:ss` and `m:ss.SSS` with seconds below 60; plus `validateRange` returning a typed error for a negative, malformed, or non-increasing range (empty side = unbounded); add `src/clients/web/tests/unit/features/analysis/matchClock.test.ts` including times beyond 90 minutes (FR-008, FR-010)
- [x] T006 [P] Create `src/clients/web/src/features/analysis/payloadSummary.ts` with `summarizePayload(value)` (at most 6 top-level fields in returned order as `name: value`, scalars cut to 60 characters, nested objects as `{n fields}`, arrays as `[n items]`, the remainder counted) and `fullPayloadText(value)` (`JSON.stringify(value, null, 2)`); add `src/clients/web/tests/unit/features/analysis/payloadSummary.test.ts` with large, deeply nested, and markup-containing payloads (FR-008, FR-020)
- [x] T007 [P] Create `src/clients/web/src/features/analysis/reviewState.ts` with `deriveFactViewState` implementing every row of `specs/20261017-181118-web-ui-analysis-review/contracts/review-states.md` in its order (`connectionRequired`, `notFound`, `notPermitted`, `capabilityNotInRun`, `invalidRange`, `error`, `loading`, `facts` with the `incomplete` flag, `noFactsInRange`, `inProgress`, `incomplete`, `completeWithoutFacts`) as a pure function of connectivity, outcomes, failures, run state, a precomputed range-validity flag (it does not import `matchClock.ts`), selection, and page; add `src/clients/web/tests/unit/features/analysis/reviewState.test.ts` with one case per row and the precedence between rows (FR-014, FR-015)
- [x] T008 [P] Create `src/clients/web/src/features/analysis/analysisKeys.ts` with the query keys of `contracts/analysis-adapters.md` (`['analysis', 'run', runId]`, `['analysis', 'streams', runId]`, `['analysis', 'facts', runId, nodeKey, fromMs, toMs, token]`) and a `runRefreshInterval(run)` that returns the existing `RUN_REFRESH_MS` (10 s, imported from `src/clients/web/src/features/lineage/AnalysisRunStates.tsx`) while the run is in progress and `false` otherwise; add `src/clients/web/tests/unit/features/analysis/analysisKeys.test.ts` (FR-006)
- [x] T009 [P] Extend `src/clients/web/tests/e2e/support/simulatedApi.ts` with seedable analysis state and routes for `GET /api/v1/analysis/runs/{runId}`, `…/fact-streams`, and `…/facts`: run status with nodes and job counts, stream summaries, fact pages served from a seeded list with opaque continuation tokens and the platform's ordering and overlap rule for `fromMs`/`toMs`, coverage windows clipped to the range, provenance with and without a model digest, `404` with `analysis-capability-not-in-run`, `400` for a rejected continuation, `404` for unknown runs and members without a team role, and a hook that advances a running run's state, counts, and coverage between polls; keep the existing `listMatchAnalysisRuns` route consistent with the seeded runs (FR-022)

**Checkpoint**: Adapters, helpers, and the simulated API exist with passing unit tests.

---

## Phase 3: User Story 1 - Open a run and see how its analysis went (Priority: P1) 🎯 MVP

**Goal**: From the match view, a member who may read the match opens a run's review and sees run and node status, refreshed while the run is in progress.

**Independent Test**: For runs in each state, a Viewer opens the review from the match and sees run details and every node with its counts; a running run's state changes without a reload; a member without a team role gets the not-permitted or not-found view.

### Run status view

- [x] T010 [US1] Create `src/clients/web/src/features/analysis/RunStatusSection.tsx` showing the run's state, request, start, and end times (`formatKickoff`), failure reason, workflow id and version, and one entry per node with node key, capability id and version, tier, execution scope, state, blocking node when present, and job counts by state, with translation lookups that fall back to the raw value for unknown states, tiers, and scopes and a polite live region announcing state changes; use only keys of `src/clients/web/src/i18n/en/analysis.json`; add `src/clients/web/tests/unit/features/analysis/runStatusSection.test.tsx` (FR-004, FR-005, FR-016, FR-019, FR-020)
- [x] T011 [US1] Create the route view `src/clients/web/src/features/analysis/AnalysisRunPage.tsx` for `/matches/:matchId/analysis-runs/:runId`: load the match through `useMatchContext` and show `NotPermittedPage` without a team role or on a `403`, load the run with `getRun` under the run query key with `refetchInterval: runRefreshInterval`, `refetchIntervalInBackground: false`, and no window-focus refetch, show `NotFoundPage` for `runNotFound` or a run whose `matchId` differs from the route, a `FailureAlert` with reload for other failures, and a back link to the match; register the route in `src/clients/web/src/router.tsx`; add the "Open analysis review" link per run to `src/clients/web/src/features/lineage/AnalysisRunStates.tsx` with its key in `src/clients/web/src/i18n/en/lineage.json`; add `src/clients/web/tests/unit/features/analysis/analysisRunPage.test.tsx` (access, not found, mismatched match, error, refresh starts and stops with the run state) (FR-001, FR-002, FR-003, FR-006, FR-017)

### Run review browser evidence

- [x] T012 [P] [US1] Create `src/clients/web/tests/e2e/simulated/analysis-review.spec.ts` with a `Run status` block: open a run from the match by keyboard, check run details and every node with counts for a completed, a failed, and a running run, see the running run's state change within 15 seconds without a reload, and see the not-found view for an unknown run and for a run of another match, using the analysis state of `src/clients/web/tests/e2e/support/simulatedApi.ts` from T009 without changing it (US1 acceptance 1–5, SC-004)
- [x] T013 [P] [US1] Create `src/clients/web/tests/e2e/platform/analysis-review.spec.ts` that signs in as the Club Admin, opens the match the upload flow finalized, opens its run's review from the run list, and checks run and node status and the not-found view for an unknown run identifier; add the chained project `platform-analysis` (`testMatch: /analysis-review\.spec\.ts$/`, depending on `platform-upload`) to `src/clients/web/playwright.config.ts` and make `platform-recovery` depend on `platform-analysis` (FR-022)

**Checkpoint**: The run review with run and node status works end to end and is the MVP.

---

## Phase 4: User Story 2 - Browse a capability's facts with coverage and provenance (Priority: P1)

**Goal**: The member chooses a capability, pages through its facts in match-time order, filters by a match-time range, and sees coverage and provenance.

**Independent Test**: For a stream with at least three pages and mixed coverage, a Coach sees the first page in order with coverage and provenance, pages forward and back without repeats, applies and clears a range, and sees only overlapping facts with coverage limited to the range.

### Paging state

- [x] T014 [US2] Create `src/clients/web/src/features/analysis/useFactPages.ts`: a hook that keeps the continuation-token stack of `data-model.md` (first page without token; Next pushes the current page's `continuationToken`, Previous pops) for one selection, discards it when the selection changes, on reload, and on `continuationRejected` (reporting that a restart happened), and loads pages through `listFacts` under the facts query key with no automatic refetch; add `src/clients/web/tests/unit/features/analysis/useFactPages.test.tsx` proving three pages forward and back without repeats and the resets (FR-009, FR-013)

### Fact browser building blocks

- [x] T015 [P] [US2] Create `src/clients/web/src/features/analysis/CoverageList.tsx` (a list of coverage windows with `formatMatchRange` and translated states, raw fallback) and `src/clients/web/src/features/analysis/ProvenanceDetails.tsx` (workflow id and version, Analyst profile id and digest, image digest, model digest only when present; digests shortened to the algorithm prefix plus 12 hexadecimal characters with a disclosure showing the full selectable value and a copy button using `navigator.clipboard.writeText` that announces success or failure); use only keys of `src/clients/web/src/i18n/en/analysis.json`; add `src/clients/web/tests/unit/features/analysis/coverageProvenance.test.tsx` (FR-011, FR-012, FR-016)
- [x] T016 [P] [US2] Create `src/clients/web/src/features/analysis/FactRangeForm.tsx`: labelled "from" and "to" match-clock fields prefilled from the current range, validation through `validateRange` with messages tied to the fields before any request, Apply and Clear actions that report the new range to the caller; use only keys of `src/clients/web/src/i18n/en/analysis.json`; add `src/clients/web/tests/unit/features/analysis/factRangeForm.test.tsx` (FR-010, FR-019)
- [x] T017 [P] [US2] Create `src/clients/web/src/features/analysis/FactStreamList.tsx`: one entry per stream with capability id and version, schema version, output type (raw fallback), fact count, and coverage counts per state, each selectable by keyboard and marking the selected stream; use only keys of `src/clients/web/src/i18n/en/analysis.json`; add `src/clients/web/tests/unit/features/analysis/factStreamList.test.tsx` (FR-007, FR-016)

### Fact browser and selection

- [x] T018 [US2] Create `src/clients/web/src/features/analysis/FactBrowser.tsx` for the selected stream: the range form, the facts of the current page in platform order (match-clock range, kind as text, confidence as a whole percentage via `Intl.NumberFormat`, the payload summary, and a disclosure with `fullPayloadText` in a `<pre>` text node), Previous/Next controls with first- and last-page indication, provenance, and coverage, using `useFactPages`; add `src/clients/web/tests/unit/features/analysis/factBrowser.test.tsx` (FR-008, FR-009, FR-011, FR-012, FR-020)
- [x] T019 [US2] Wire streams and selection into `src/clients/web/src/features/analysis/AnalysisRunPage.tsx`: load `listStreams` under the streams query key with the run's refresh interval, render `FactStreamList` and, for the `stream` search parameter, `FactBrowser`; read and write `stream`, `fromMs`, and `toMs` as in `specs/20261017-181118-web-ui-analysis-review/contracts/routes-and-roles.md` (one history entry per selection change, no page position in the address); extend `src/clients/web/tests/unit/features/analysis/analysisRunPage.test.tsx` with selection from and to the address (FR-001, FR-007, FR-010)

### Fact browsing browser evidence

- [ ] T020 [US2] Add a `Fact browsing` block to `src/clients/web/tests/e2e/simulated/analysis-review.spec.ts`: reach the first fact page of a stream from the match view in at most 3 navigation steps (SC-001), list streams with counts and coverage, open a stream with at least three pages, page forward and back and assert every fact exactly once in order, apply a range and see only overlapping facts and clipped coverage, see an invalid range explained without a request, clear the range, reload with the stream and range kept, open a full payload, and see provenance with and without a model digest (US2 acceptance 1–6, SC-002)

**Checkpoint**: Fact browsing with paging, ranges, coverage, and provenance works on its own.

---

## Phase 5: User Story 3 - Understand empty, incomplete, and unavailable analysis (Priority: P2)

**Goal**: Every explicit state has its own worded message, facts accepted so far are marked incomplete, and refreshed results are offered without moving the member's position.

**Independent Test**: For a running run, a partially completed run with a failed segment, a capability without facts, and an empty range, the member sees distinct messages, and the running run's counts and coverage change after the refresh without a reload.

### State messages

- [ ] T021 [US3] Render every state of `specs/20261017-181118-web-ui-analysis-review/contracts/review-states.md` in `src/clients/web/src/features/analysis/FactBrowser.tsx` through `deriveFactViewState`: its own message per state, coverage in every data state, the incomplete note on facts while the run is in progress or coverage is not fully accepted, the non-accepted windows for `incomplete`, and the continuation-restart note; add `src/clients/web/tests/unit/features/analysis/factBrowserStates.test.tsx` with one case per state (FR-014, FR-015, SC-003)
- [ ] T022 [US3] In `src/clients/web/src/features/analysis/AnalysisRunPage.tsx`, compare the refreshed stream summary of the selected stream with the counts and coverage the open page was loaded with and show the "new results are available" notice with a reload action that restarts at the first page, announced through a polite live region; treat a `stream` node key that is not in the listing as `capabilityNotInRun` and show the stream list; extend `src/clients/web/tests/unit/features/analysis/analysisRunPage.test.tsx` (FR-006, FR-014, US3 acceptance 5 and 6)

### State browser evidence

- [ ] T023 [US3] Add an `Explicit states` block to `src/clients/web/tests/e2e/simulated/analysis-review.spec.ts`: running run without facts (`inProgress`), partially completed run with failed and blocked windows (`incomplete` and facts with the incomplete note), complete coverage without facts (`completeWithoutFacts`), a range without facts (`noFactsInRange`), an outdated stream address (`capabilityNotInRun`), a rejected continuation (restart note), a platform error with reload, and a running run whose counts and coverage change after a poll with the notice offered instead of the page changing (US3 acceptance 1–6); extend `src/clients/web/tests/e2e/platform/analysis-review.spec.ts` to open the run's first stream and see zero facts with the in-progress or incomplete message (FR-022)

**Checkpoint**: The review never shows an unexplained empty list.

---

## Phase 6: User Story 4 - Find and use the review safely in every supported setting (Priority: P3)

**Goal**: The review is reachable only for readers, accessible, and online only.

**Independent Test**: Browser tests reach the review by keyboard, pass axe checks in every state, prove that offline shows the connection-required state and that no analysis data reaches service-worker caches or browser storage, and refuse non-readers.

### Access, accessibility, and online-only evidence

- [ ] T024 [P] [US4] Extend `src/clients/web/tests/unit/serviceWorker/shellWorker.test.ts` to assert that `classifyRequest` leaves `/api/v1/analysis/runs/{id}`, `…/fact-streams`, and `…/facts` unhandled for every method (FR-018)
- [ ] T025 [P] [US4] Create `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts`: the review link and address for a Club Admin, a Coach, and a Viewer of the team, and the not-permitted or not-found view for a member without a team role, and the not-found view at the next refresh after a Viewer's team role is revoked while the review is open (with a test-level route override); axe checks (`tests/e2e/support/axe.ts`) in the loading, facts, empty, incomplete, capability-not-in-run, error, and connection-required states; keyboard-only navigation from the match through stream selection, range, paging, payload, and digest disclosure with visible focus; offline shows the connection-required state without earlier analysis data; after a review session, Cache Storage has no `/api/` entry and `localStorage`, `sessionStorage`, and IndexedDB hold no analysis data, using the analysis state of `src/clients/web/tests/e2e/support/simulatedApi.ts` without changing it; where these checks fail, fix focus order, labels, live regions, and the connection-required handling (with the existing connectivity state of `src/clients/web/src/connectivity/useConnectivity.ts` and no browser storage) in `src/clients/web/src/features/analysis/AnalysisRunPage.tsx`, `FactBrowser.tsx`, `FactStreamList.tsx`, `FactRangeForm.tsx`, `CoverageList.tsx`, `ProvenanceDetails.tsx`, and `RunStatusSection.tsx` in the same task (FR-002, FR-018, FR-019, SC-005, SC-006)

**Checkpoint**: All user stories work independently and meet the Web UI's quality rules.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Translation consistency, documentation, and the full validation.

### Translations and documentation

- [ ] T026 [P] Reconcile `src/clients/web/src/i18n/en/analysis.json` with the keys the review's components use (no missing or unused keys) and make `src/clients/web/tests/unit/i18n/keys.test.ts` pass with no literal user-facing text in `src/clients/web/src/features/analysis/` (FR-019)
- [ ] T027 [P] Make `docs/architecture/` describe the merged state as `plan.md` → Architecture Updates requires: re-read `docs/architecture/` and correct every narrative and status statement this feature affects, for example the connected analysis review paragraph and the "(analysis review and agent conversations are not implemented yet)" line in `docs/architecture/client-applications.md` → Web UI, the Web UI workflows, evidence list, and "the other Web UI client workflows (analysis review and agent conversations)" in `docs/architecture/platform-implementation.md` → Acceptance Evidence and Architecture Reassessment, and the member-read sentence in `docs/architecture/match-data-pipeline.md` → Segment And Match Analysis Scope; the listed rewrites are examples, not the whole job, and planned or future wording about the review must not remain; update `AGENTS.md` ("Current State"), `src/clients/README.md`, and the root `README.md` where they say analysis review is not implemented; run `node .github/scripts/check-markdown.mjs`

### Validation

- [ ] T028 Run the validation of `specs/20261017-181118-web-ui-analysis-review/quickstart.md` from `src/clients/web` (`npm ci`, `npm run build`, `npm test`, `npm run test:e2e -- --project simulated`, `npm run test:e2e -- --project platform`) and fix any failure in the files of this feature

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: needs the merged batch sibling (fact operations in the OpenAPI snapshot); T001 and T002 run in order.
- **Foundational (Phase 2)**: depends on Setup; T003–T009 form one parallel group.
- **User Story 1 (Phase 3)**: depends on Foundational.
- **User Story 2 (Phase 4)**: depends on Foundational and on T011 (the route view it extends).
- **User Story 3 (Phase 5)**: depends on US2 (it extends `FactBrowser.tsx` and the selection in `AnalysisRunPage.tsx`).
- **User Story 4 (Phase 6)**: depends on US1–US3 (its checks cover every view and state).
- **Polish (Phase 7)**: depends on all stories; T028 runs last.

### User Story Dependencies

- **US1 (P1)**: independent after Foundational; the MVP.
- **US2 (P1)**: builds on the US1 route view; independently testable through its own browser block.
- **US3 (P2)**: refines US2's fact browser.
- **US4 (P3)**: verifies and hardens all views.

### Within Each User Story

- Components before the route wiring that composes them; unit tests in the same task as the code they cover.
- Browser evidence after the views it exercises.
- `src/clients/web/tests/e2e/simulated/analysis-review.spec.ts` is created by T012 and extended by T020 and T023 in later phases, never in parallel.

### Parallel Opportunities

- Phase 2: T003–T009 (adapters, helpers, simulated API; disjoint files).
- Phase 3: T012 and T013 (simulated spec; platform spec and `playwright.config.ts`).
- Phase 4: T015, T016, and T017 (separate components and tests; none edits `analysis.json`).
- Phase 6: T024 and T025 (the service-worker unit test; the access browser spec with any view fixes it requires).
- Phase 7: T026 and T027.

---

## Parallel Example: Foundational

```text
Task: "T003 getRun in analysisRunAdapter.ts with its tests"
Task: "T004 analysisFactAdapter.ts with its tests"
Task: "T005 matchClock.ts with its tests"
Task: "T006 payloadSummary.ts with its tests"
Task: "T007 reviewState.ts with its tests"
Task: "T008 analysisKeys.ts with its tests"
Task: "T009 analysis routes in simulatedApi.ts"
```

## Parallel Example: User Story 2

```text
Task: "T015 CoverageList.tsx and ProvenanceDetails.tsx with tests"
Task: "T016 FactRangeForm.tsx with tests"
Task: "T017 FactStreamList.tsx with tests"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2.
2. Complete Phase 3 (US1): the run review with run and node status, reachable from the match.
3. Validate with T012 and T013.

### Incremental Delivery

1. US1 → run and node status (MVP).
2. US2 → fact browsing with paging, ranges, coverage, and provenance.
3. US3 → explicit states and refresh notice.
4. US4 → access, accessibility, and online-only evidence.
5. Polish → translations, architecture and repository documentation, full validation.

---

## Notes

- Component tasks use only the keys T001 created; T026 reconciles the namespace at the end, so parallel component tasks never edit `analysis.json`.
- The review adds no platform operation and never changes the OpenAPI snapshot.
- Do not cache or persist analysis data anywhere in the browser.
