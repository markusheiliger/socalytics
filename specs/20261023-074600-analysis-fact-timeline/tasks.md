# Tasks: Web UI Analysis Fact Timeline

**Input**: Design documents from `/specs/20261023-074600-analysis-fact-timeline/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md)

**Tests**: Required by FR-021 (Vitest unit tests; Playwright `simulated` and `platform` projects with axe).

**Organization**: Tasks are grouped by user story. All paths are relative to the repository root; the Web UI is `src/clients/web`. No platform, contract, or OpenAPI change (FR-014). Zoom levels and loading bounds are development values.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies on each other)
- **[Story]**: User story the task belongs to (US1 to US5)

## Phase 1: Setup

### Translation keys and query keys

- [x] T001 [P] Add the timeline translation keys (lane and bin state labels for facts present, analysed without facts, facts beyond the loading bound not shown, partially covered, not analysed, no reported coverage, in progress, complete without facts, capability not in run, unavailable, error, connection required; zoom, extent, selection, "Show facts", "Back to timeline", text alternative, partial "based on N of M facts", extent unavailable, live-region messages) to `src/clients/web/src/i18n/en/analysis.json` (FR-020)
- [x] T002 [P] Add the timeline query key prefix `['analysis', 'timeline']` and a lane key built from run, node key, fact count, and coverage counts of the stream summary (research R6) to `src/clients/web/src/features/analysis/analysisKeys.ts`, with the case in `src/clients/web/tests/unit/features/analysis/analysisKeys.test.ts`

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: Pure derivation and lane loading used by every story.

### Derivation and loading

- [x] T003 Implement `src/clients/web/src/features/analysis/timeline/timelineBins.ts`: the four fixed zoom levels (`10s`, `30s`, `1m`, `5m` with `binMs` 10 000, 30 000, 60 000, 300 000 and 30 bins each), the overall extent over all windows and facts rounded out to the finest bin size (undefined when none), default zoom (the finest level whose 30 bins cover the extent, else the widest, chosen once after all lanes have left `loading`; a user-chosen zoom and extent are kept across reloads), `extentStartMs` floored to a multiple of `binMs` and clamped to `[floor(extentStart), extentEnd - bins*binMs]`, visible extent half-width movement, and bin derivation per research R4 (a fact counts once in every overlapping bin of `[s, e)`; a zero-length fact counts in the bin containing `startMs`; coverage class `accepted`, `notAnalysed`, `partial`, `none`; bin state `facts`, `analysedNoFacts` only for a fully accepted bin with count 0 up to the loaded horizon or in a lane that is not partial, `notLoaded` ("facts beyond the loading bound not shown", also in the text alternative) for a count-0 bin of a `partial` lane that starts after the start of the last loaded fact, `partial`, `notAnalysed`, `noCoverage`; unknown window states kept raw in `rawStates` and treated as not analysed); constants `MAX_FACTS_PER_STREAM = 5000`, `MAX_FACTS_TOTAL = 20000`, `BINS_PER_VIEW = 30` as development values
- [x] T004 Implement `src/clients/web/src/features/analysis/timeline/timelineLane.ts`: the lane allowance `min(5 000, floor(20 000 / lane count))`, the `partial` rule (`loadedFactCount < reportedFactCount`), and the lane state rules of data-model.md (`loading`, `ready`, `inProgress` only for a known `Requested` or `Running` run state and winning over `completeWithoutFacts` and `noCoverage`; any other run state, including an unknown one, is final, and an unknown raw run or node state is shown in the lane detail and never as zero, `completeWithoutFacts` only when the run is final and every window is `accepted`, `noCoverage`, `capabilityNotInRun`, `unavailable`, `error`, `connectionRequired`; a lane with facts is always `ready`)
- [x] T005 Implement `src/clients/web/src/features/analysis/timeline/useLaneFacts.ts`: one TanStack Query per lane (`gcTime: 0`, `staleTime: Infinity`, no persistence) that follows continuation tokens of `AnalysisFactAdapter.listFacts` without a range until the last page or the lane allowance, keeps only `startMs` and `endMs` (payload dropped at once), takes coverage from the first page, restarts loading once when a continuation is rejected and then yields `unavailable`

### Foundational tests

- [x] T006 [P] Unit tests for bin derivation in `src/clients/web/tests/unit/features/analysis/timeline/timelineBins.test.ts`: multi-bin and identical or overlapping facts counted once per bin, zero-length facts, facts outside the visible extent, partial coverage, unknown states shown raw and never zero, no zero outside fully accepted bins, extent union and rounding, extent unavailable, default zoom (finest level covering the extent, else 5 m, chosen once after lanes load, kept when later loads change the extent), `extentStartMs` flooring and clamping, movement, extra-time bins beyond 90 minutes, the SC-002 sum invariant, and `partial` lane cases (a count-0 bin after the loaded horizon is `notLoaded`; a count-0 bin up to the horizon and in a non-partial lane stays `analysedNoFacts`)
- [x] T007 [P] Unit tests for lane rules in `src/clients/web/tests/unit/features/analysis/timeline/timelineLane.test.ts`: allowance for 1, 4, and 10 lanes, partial marking, and every lane state including the precedence rules and final run states (`Failed`, `Cancelled`, unknown) never `inProgress`
- [x] T008 [P] Unit tests for `useLaneFacts` in `src/clients/web/tests/unit/features/analysis/timeline/useLaneFacts.test.tsx`: paging over at least three pages, stopping at the allowance, payload not retained, coverage from the first page, one restart after a rejected continuation and `unavailable` after the second, reload when the lane key changes

**Checkpoint**: Derivation and loading are verified; stories can start.

---

## Phase 3: User Story 1 - See where in the match facts occur (Priority: P1) MVP

**Goal**: One lane per fact stream over a shared match-clock axis with density per bin at a selectable zoom level and movable extent.

**Independent Test**: For a run whose streams have facts over several pages, the review shows one lane per stream with bins matching the facts' ranges; zoom and extent change bin size and visible range; totals agree with the stream's reported count; partial density is stated.

- [x] T009 [US1] Implement `src/clients/web/src/features/analysis/timeline/TimelineLane.tsx`: lane label (capability id and version, node key), reported total, bins per the derived data with count, pattern, and glyph per state, "based on N of M facts" and partial marking, and the lane detail with the run-pinned provenance through the existing `ProvenanceDetails.tsx` (FR-005); platform text rendered as text only (FR-016)
- [x] T010 [US1] Implement `src/clients/web/src/features/analysis/timeline/AnalysisTimeline.tsx`: lanes from the page's stream list, shared axis in the match-clock format (`matchClock.ts`), the zoom button group, "Earlier" and "Later" extent buttons, the extent-unavailable state with the capability list (FR-008), choosing the default zoom once after all lanes have left `loading` and keeping a user-chosen zoom and extent across reloads, and one polite live region for status changes (FR-019)
- [x] T011 [US1] Show the timeline, open by default when the run review shows, above the fact list in `src/clients/web/src/features/analysis/AnalysisRunPage.tsx`, keeping `open`, `zoom`, and `extentStartMs` in page state (data-model.md); no timeline for members without read access

### Timeline component tests and fixture

- [x] T012 [P] [US1] Component tests in `src/clients/web/tests/unit/features/analysis/timeline/analysisTimeline.test.tsx`: lanes per stream and per node of the same capability, shared axis labels beyond 90 minutes, zoom and extent changes, default zoom chosen once and zoom and extent kept when lanes reload, totals agree with the reported count, partial density wording including the `notLoaded` label in bins and the text alternative, and markup-like node keys, capability ids, and failure reasons rendered as literal text (FR-016)

### Existing review scenarios

- [x] T034 [US1] Adapt the existing review browser scenarios in `src/clients/web/tests/e2e/simulated/analysis-review.spec.ts` to the timeline that T011 now shows by default above the fact list: the scenario "shows a platform error with a reload that recovers (scenario 4)" fails its first facts request with a `500` for any request to the facts route, which the timeline's lane load now consumes, so the review's error alert never appears. Make each failure or rejection injected for the fact list (a `500`, a rejected continuation, an outdated stream) apply to the fact list's own requests only (for example the request that carries the page-size query used by the fact list, as distinguished from the timeline's lane reads) or close the timeline first, keeping every assertion of the scenario unchanged and without weakening any expectation; re-check every other scenario of the file for the same interaction (FR-021)

### Timeline scenario fixture

- [x] T013 [P] [US1] Add a timeline scenario fixture (several pages of unevenly spread facts, two nodes of one capability, extra-time coverage) to `src/clients/web/tests/e2e/support/simulatedApi.ts`

### Density browser scenarios

- [x] T014 [US1] Create `src/clients/web/tests/e2e/simulated/analysis-timeline.spec.ts` with the US1 scenarios: lanes over a shared axis, density per bin, zoom and extent, partial density

**Checkpoint**: The timeline shows density and is usable on its own (MVP).

---

## Phase 4: User Story 2 - Coverage gaps distinct from zero facts (Priority: P1)

**Goal**: Coverage windows and every explicit lane and bin state are shown distinctly; zero appears only for fully accepted bins.

**Independent Test**: A run with a failed segment, a skipped window, an accepted window without facts, and a stream without coverage shows four distinct bin states; a running run shows "in progress" instead of zero.

### Lane states and refresh

- [x] T015 [US2] Extend `src/clients/web/src/features/analysis/timeline/TimelineLane.tsx` with the coverage windows drawn on the lane with state text, pattern, or shape besides colour, the bin labels for analysed without facts, partially covered, not analysed, and no reported coverage, raw values for unknown states, and the lane states `inProgress`, `completeWithoutFacts`, `noCoverage`, `capabilityNotInRun`, `unavailable` (reload action), `error` (reload action), and `connectionRequired` using the review's existing wording where one exists
- [x] T016 [US2] Wire the run status refresh in `src/clients/web/src/features/analysis/timeline/AnalysisTimeline.tsx` so lanes reload when the stream summary's count or coverage changes, refreshing stops with the final run state, and selection, zoom, and extent are kept while changed data is announced without moving focus (FR-013); during a reload a lane keeps showing its previous data, marked as updating and announced politely, until the new pages are complete, so the status text appears within 15 s independent of paging time; lanes refresh only through the page's existing visibility-aware status refresh, so nothing refreshes while the page is hidden

### Lane state component tests and fixture

- [x] T017 [P] [US2] Component tests in `src/clients/web/tests/unit/features/analysis/timeline/timelineLaneStates.test.tsx`: every state of FR-006 (including `notLoaded` in a partial lane) shows its own label, no count of zero outside accepted coverage, a reloading lane keeping its previous data marked as updating until the new pages complete, unknown raw values, in-progress lane without density, refresh keeping selection, zoom, and extent, no lane refresh while the page is hidden, and final run states (`Failed`, `Cancelled`, unknown) never in progress with unknown raw values shown
- [x] T018 [P] [US2] Add coverage scenarios (failed segment, skipped window, accepted window without facts, stream without coverage, running run, complete without facts, capability not in run, lane load failure) to `src/clients/web/tests/e2e/support/simulatedApi.ts`

### Coverage browser scenarios

- [ ] T019 [US2] Add the US2 scenarios (four distinct bin states, in-progress lane refreshing within 15 s without losing selection, every explicit lane state) to `src/clients/web/tests/e2e/simulated/analysis-timeline.spec.ts`

**Checkpoint**: No state is inferred as zero.

---

## Phase 5: User Story 3 - Jump from a timeline position to the fact list (Priority: P1)

**Goal**: Select a bin or span and show exactly those facts in the existing fact list, then return with zoom and extent kept.

**Independent Test**: A Coach selects a bin and jumps; the list shows exactly the facts overlapping the range, the address carries stream and range, and "Back to timeline" restores zoom and extent with a fresh read.

### Selection and jump

- [ ] T020 [US3] Add the selection model (one bin or a contiguous span of bins on one lane, range `[start of first bin, end of last bin)` stored by absolute `fromMs`/`toMs`, cleared by zoom or run change and, with a polite announcement, when the extent moves so that it is no longer fully visible) and the selection summary with match-clock range, summed count (for a `partial` lane marked "at least N, based on loaded facts", or the `notLoaded` state label when the selected bins start after the loaded horizon) or coverage state label, and the "Show facts" action to `src/clients/web/src/features/analysis/timeline/TimelineLane.tsx` and `src/clients/web/src/features/analysis/timeline/AnalysisTimeline.tsx`
- [ ] T021 [US3] In `src/clients/web/src/features/analysis/AnalysisRunPage.tsx`, call the page's `changeRange` with the lane's node key and the selection range on "Show facts", close the timeline (discarding its data), and show a "Back to timeline" action that reopens it with the kept zoom and extent and a fresh read (research R7); a not-analysed or empty range lands on the fact list's existing explicit state with its coverage

### Jump tests

- [ ] T022 [P] [US3] Component tests in `src/clients/web/tests/unit/features/analysis/timeline/timelineJump.test.tsx`: span range, selection cleared with an announcement when the extent moves it out of view, jump range passed to `changeRange`, zoom and extent kept on return, not-analysed range, partial-lane selection summary ("at least N" or `notLoaded`)
- [ ] T023 [P] [US3] Add the US3 scenarios (select, jump, exactly the overlapping facts listed, address with stream and range, not-analysed range state, return keeping zoom and extent, partial-lane selection summary marked partial or `notLoaded`, at most 3 interactions) to `src/clients/web/tests/e2e/simulated/analysis-timeline.spec.ts`

**Checkpoint**: Overview connects to the existing fact list.

---

## Phase 6: User Story 4 - Keyboard and screen reader (Priority: P2)

**Goal**: The graphic is keyboard-operable and a text alternative lists the same data.

**Independent Test**: Keyboard-only operation of every control; axe reports no violations in every state; the alternative equals the graphic's data.

### Keyboard and text alternative

- [ ] T024 [US4] Add roving-tabindex bin navigation (arrow keys move, Shift+arrow extends a span, Enter or Space selects, Escape clears), visible focus, and accessible names with range and count or coverage state to `src/clients/web/src/features/analysis/timeline/TimelineLane.tsx` (research R5)
- [ ] T025 [US4] Implement `src/clients/web/src/features/analysis/timeline/TimelineTextAlternative.tsx`: one table per lane from the same derived data (range and count or coverage state of every bin at the current zoom and extent) and the "Show text alternative" toggle in `src/clients/web/src/features/analysis/timeline/AnalysisTimeline.tsx`

### Accessibility tests

- [ ] T026 [P] [US4] Component tests in `src/clients/web/tests/unit/features/analysis/timeline/timelineKeyboard.test.tsx` (key handling, focus, selection, live-region messages without focus moves) and `src/clients/web/tests/unit/features/analysis/timeline/timelineTextAlternative.test.tsx` (table rows equal the graphic's bins for the same zoom and extent)
- [ ] T027 [P] [US4] Add the US4 scenarios (keyboard-only flow from lane to jump, text alternative equals graphic, axe checks in every state using `tests/e2e/support/axe.ts` and `tests/e2e/support/keyboard.ts`) to `src/clients/web/tests/e2e/simulated/analysis-timeline.spec.ts`

**Checkpoint**: The timeline meets the accessibility requirements.

---

## Phase 7: User Story 5 - Online-only, memory-only, scoped to access (Priority: P2)

**Goal**: Timeline data is memory-only and discarded on leaving, offline, sign-out, or access loss.

**Independent Test**: Storage and service-worker caches hold no timeline data; offline shows connection required; revoked access discards the data at the next read.

### Discard on leaving and access loss

- [ ] T028 [US5] Remove all queries under the `['analysis', 'timeline']` prefix on unmount, when connectivity is lost, on sign-out, and when a read is refused as forbidden or not found, and show the connection-required state offline, in `src/clients/web/src/features/analysis/AnalysisRunPage.tsx` and `src/clients/web/src/features/analysis/timeline/AnalysisTimeline.tsx`

### Discard and platform tests

- [ ] T029 [P] [US5] Unit tests in `src/clients/web/tests/unit/features/analysis/timeline/timelineDiscard.test.tsx`: queries removed on unmount, offline, forbidden, and not found; no browser storage writes
- [ ] T030 [P] [US5] Add the US5 scenarios (non-member refused without a timeline, offline connection-required, data discarded on leaving and access loss, no fact, density, or coverage data in browser storage or service-worker caches, role-based access for Club Admin, Coach, and Viewer) to `src/clients/web/tests/e2e/simulated/analysis-timeline.spec.ts`
- [ ] T031 [P] [US5] Create `src/clients/web/tests/e2e/platform/analysis-timeline.spec.ts` against the running platform (reusing the data setup of `src/clients/web/tests/e2e/platform/analysis-review.spec.ts`): a Coach opens a run with accepted facts, sees the timeline with lane totals equal to the reported fact count, jumps from a bin, and sees exactly the facts overlapping it (FR-021, SC-004)

**Checkpoint**: All stories are complete and verified.

---

## Phase 8: Polish and Cross-Cutting Concerns

### Documentation and validation

- [ ] T032 Update the documentation so `docs/architecture/` is true for the merged state, re-reading the documents at implementation time: in `docs/architecture/client-applications.md` the Web UI summary list and the connected analysis review paragraph name the fact timeline as the first implemented visualization (lanes per fact stream, density at fixed development zoom levels, coverage windows distinct from zero facts, text alternative, jump to the range-filtered fact list, online-only and memory-only) and keep only the still-absent visualizations (spatial, heatmaps, pitch views), the run controls, comparison, export, and the Coach Agent workflows as not implemented, and the run comparison paragraph is re-read; in `docs/architecture/analysts-models-and-hardware.md` Capability-Scoped Result Access names the timeline as a second consumer of `listAnalysisRunFacts` and `listAnalysisRunFactStreams` with density derived in the browser from bounded page-by-page reads and no new operation; correct every planned, future-tense, or unimplemented wording the feature makes false, and word the loading bound and zoom levels as development values; correct the review's scope statements in `AGENTS.md` and any `README.md` or `src/clients/web` README statement; then pass `node .github/scripts/check-markdown.mjs`
- [ ] T033 Run the validation of [quickstart.md](quickstart.md) from `src/clients/web` (`npm test -- tests/unit/features/analysis/timeline tests/unit/features/analysis/analysisKeys.test.ts`, `npm run build`, `npm run test:e2e -- --project simulated analysis-timeline`, and `npm run test:e2e -- --project platform analysis-timeline` where Docker is available) and fix any failure

---

## Dependencies and Execution Order

- Phase 1 first (T001 and T002 run in parallel); Phase 2 depends on T001 and T002; T003 to T005 are sequential (each builds on the previous), T006 to T008 run in parallel after them (three separate test files).
- US1 (Phase 3) depends on Phase 2 and is the MVP. US2, US3, and US4 depend on US1's components and extend the same component files, so their implementation tasks run one after another in priority order. US5 depends on US1 and the lane queries; its platform test (T031) also depends on US3.
- Within each story the test group (component tests and fixture or scenario files, all different files) runs in parallel after the implementation tasks; the browser scenarios of US1 and US2 follow their fixture (T014 after T013, T019 after T018), and T034 runs before the fixture because the timeline shown by T011 changes the existing review scenarios as their own task.
- Phase 8 depends on all stories.

## Parallel Examples

- T001 and T002; after T005: T006, T007, and T008.
- After T011: T012, then T034 (the existing review scenarios must pass with the timeline), then T013, then T014. After T016: T017 and T018, then T019.
- After T021: T022 and T023. After T025: T026 and T027. After T028: T029, T030, and T031.

## Implementation Strategy

1. MVP: Phases 1 to 3 (density timeline with zoom and extent).
2. Add US2 (explicit states), then US3 (jump), then US4 (accessibility), then US5 (discard and platform evidence).
3. Finish with the documentation task and the quickstart validation.
