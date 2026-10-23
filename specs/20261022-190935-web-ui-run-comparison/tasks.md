# Tasks: Web UI Analysis Run Comparison

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [UI contract](contracts/comparison-ui.md),
[member-read contract](contracts/member-reads.md), and
[validation guide](quickstart.md).

**Tests**: Required by FR-018 and SC-001 through SC-007. Each implementation
task includes its focused executable evidence: write the relevant assertions
first, observe the expected failure, implement, and leave the task passing.
Browser evidence tasks follow the implementation they exercise.

**Organization**: Tasks are grouped by user story. Paths below are relative
to the repository root. Do not create a project, dependency, endpoint,
platform change, root contract, generated-client edit, or environment-action
change. Generate Kiota only through the existing build, from the unchanged
OpenAPI snapshot.

## Format: `[ID] [P?] [Story] Description with file path`

- Consecutive unchecked `[P]` tasks under one heading run in parallel, each
  from the same commit; their file footprints and prerequisites are disjoint.
- Tasks without `[P]` run in order within their phase.
- `[US1]` through `[US4]` identify the corresponding specification story.
- Preserve unrelated working-tree changes and existing review, cancellation,
  supersession, upload, and triage behavior. Never check these boxes from
  a GitHub task work job; the land job owns task completion.

## Phase 1: Setup (Shared Evidence Fixtures)

**Purpose**: Prepare existing simulated evidence, not a new toolchain or
application scaffold.

- [x] T001 Extend `src\clients\web\tests\e2e\support\simulatedApi.ts` using its existing `analysisRunFixture`, `seedAnalysisRun`, and `supersedeAnalysisRun` patterns: expose explicit current/supersession status fields and nullable dependency input coverage on run-status responses, support two independently identifiable terminal runs with exact recording-set reads and paged candidate history, and retain wire-shape fidelity for existing review fixtures. Provide opt-in stream/count, delayed-read, failure, and access-change controls only where existing route overrides cannot express the acceptance cases; preserve existing default scenarios and run the existing simulated analysis-review and lineage evidence. No new fixture server, browser project, or runtime process.

**Checkpoint**: Existing fixtures can express comparison cases without
changing product behavior or weakening current tests.

## Phase 2: Foundational (Blocking Read and Load Boundaries)

**Purpose**: Establish safe adapters and an all-or-nothing comparison load
before any route can present a pair.

### Independent adapter changes

- [x] T002 [P] Extend `src\clients\web\src\platform\models\analysis.ts` and `src\clients\web\src\platform\adapters\analysisRunAdapter.ts`, with tests in `src\clients\web\tests\unit\platform\adapters\analysisRunAdapter.test.ts`: retain status `isCurrent` as a "Required explicit boolean from the status response. False does not itself mean superseded"; retain nullable `supersededBy` where "A non-null object must be complete" (successor run ID, recording-set ID, finalization timestamp), and nullable `supersededAt` with "do not invent a value". Map `nodes[].inputCoverage` as "Nullable/not-reported dependency coverage, distinct from a reported empty list"; require upstream identity and policy, retain raw unfamiliar policies with no supported-policy inference, validate `minimumAcceptedFraction` as "Optional reported fraction, finite and within zero to one when present" and all five counts as "Nonnegative safe integer execution-unit counts". Add the exact known terminal-state predicate without assuming unknown states are terminal. Preserve `toAnalysisRun` list/cancellation compatibility defaults; stricter truth belongs to `getRun`. Update typed and wire-status fixtures in `src\clients\web\tests\unit\features\analysis\analysisRunPage.test.tsx`, `runStatusSection.test.tsx`, and `reviewState.test.ts` in that same directory as required, without touching the T003 footprint or generated code.
- [x] T003 [P] Harden `src\clients\web\src\platform\adapters\analysisFactAdapter.ts` and `src\clients\web\tests\unit\platform\adapters\analysisFactAdapter.test.ts`: require an actual stream `items` array so explicit `[]` is valid but missing/null is incompatible; validate `factCount` as a "nonnegative safe integer" and output coverage as "separate accepted/missing/failed/blocked/skipped counts" with nonnegative safe integers. Reject "Positive facts with no accepted units" and missing required identity, contract, or provenance through `PlatformFailure('incompatible')`; preserve raw unfamiliar contract values and the optional model digest, for which "A missing optional model digest is 'not reported'; it is neither an error nor a fabricated digest". Preserve concealed 404 and existing fact-page behavior; test genuine empty, invalid, complete-zero, partial-zero, and absent-model cases without changing shared model files.

### Selection and identity validation

- [x] T004 Implement comparison-owned selection parsing and context validation in `src\clients\web\src\features\analysis\comparisonPresentation.ts` with `src\clients\web\tests\unit\features\analysis\comparisonPresentation.test.ts`: allow no selection or left-only selection; reject malformed IDs, duplicate query parameters, unsupported parameter combinations, right-only selection, identical IDs, cross-match/team or mismatched response IDs, and nonterminal/unknown run states. Require "different IDs and two known terminal runs"; current/superseded status never determines eligibility or replaces an ID. Validate each exact recording set's ID/match/team, finalization and ordered member metadata (recording-version IDs, content digests, timeline-mapping IDs/digests), require each stream node to resolve to consistent run capability/version/scope metadata, require stream provenance workflow to agree with the containing run, and reject duplicate within-run `nodeKey` identities. Reuse existing adapters and `PlatformFailure` rather than defaulting missing metadata or changing shared recording mappers.

### Authorized comparison loader

- [x] T005 Create `src\clients\web\src\features\analysis\useAnalysisComparison.ts` and focused tests in `src\clients\web\tests\unit\features\analysis\useAnalysisComparison.test.tsx`: use dedicated comparison query keys containing member ID, match ID, ordered run IDs, and generation, with no previous-data placeholder, no shared run/stream/lineage cache writes, and zero cache retention after unmount. Freshly read match then owning team, fetch both run statuses in parallel, validate with T004, then fetch both stream summaries and at most two distinct exact recording sets in parallel (one set read when shared). Publish only after all reads for the active identity succeed; entry, explicit refresh/retry, and pair changes invalidate ready content before asynchronous work. Use the states `selecting`, `invalidSelection`, `loading`, `ready`, `notFound`, `notPermitted`, `offline`, `incompatible`, and `error`; preserve typed refusal/session handling. Retire generations and discard completed/pending data on pair/member/session changes, offline, refusal, and unmount, guarding late completions even without transport abort. Test read order/counts, no payload/grant/object reads, pending/failed-side atomicity, shared-set deduplication, and zero comparison/shared-cache residue after cleanup. No terminal polling or unconditional-read ETag cache.

**Checkpoint**: No completed model can escape the current authorized load
scope. All stories depend on this phase. Lifecycle return triggers and the
expanded adversarial matrix are completed in US3.

## Phase 3: User Story 1 - Compare Two Finished Runs from a Match (Priority: P1)

**Goal**: Reach one comparison through the match or terminal review, choose
two explicit runs, inspect their independently attributed context and stream
summaries, and follow either exact detailed-review link.

**Independent Test**: For Club Admin, owning-team Coach, and Viewer, select
a terminal current run and superseded history, verify both recording sets
and workflows, and follow both review links. Also exercise same-set history
and fewer than two eligible candidates without automatic substitution.

### Selection and base presentation

- [x] T006 [US1] Build the paged two-run selection form in `src\clients\web\src\features\analysis\AnalysisComparisonPage.tsx`, English keys in `src\clients\web\src\i18n\en\analysis.json`, and selection tests in `src\clients\web\tests\unit\features\analysis\analysisComparisonPage.test.tsx`. Use `listByMatch` with explicit load-more/loading/error/exhaustion state, not `loadAllRuns` or its twenty-page limit; show recognizable state/run/recording-set/workflow context. Preserve a review-origin left ID, never preselect the newest pair or replace IDs absent from loaded pages, and permit explicit later edits to either side. Explain terminal eligibility and insufficient history only as established by exhausted pages, with older pages still reachable. URL state contains selection identifiers only and uses the T004 parser; candidate pages are memory-only, non-authoritative, comparison-scoped, and discarded on offline/access loss/session end/unmount.
- [x] T007 [US1] Connect T005 to `src\clients\web\src\features\analysis\AnalysisComparisonPage.tsx` and create the initial independently attributed stream presentation in `src\clients\web\src\features\analysis\ComparisonStreams.tsx`, updating `src\clients\web\src\i18n\en\analysis.json` and `src\clients\web\tests\unit\features\analysis\analysisComparisonPage.test.tsx`. Show explicit left/right run IDs, terminal states, current flags, authoritative successor and supersession timestamps when present, exact recording-set finalization/member context, workflow ID/version, and exact side-specific detailed-review links. Retain the non-current/non-superseded state. Initially render every stream independently with its contract, count, all five coverage counts and pinned provenance using `ProvenanceDetails`; do not yet guess alignment or equate zero facts with complete analysis. Explain that reported quantities do not establish equal full inputs or output equivalence; render no result details until the full pair is ready.
- [x] T008 [US1] Register the signed-in route `/matches/:matchId/analysis-comparison` in `src\clients\web\src\router.tsx`, add its match-run-area entry in `src\clients\web\src\features\lineage\AnalysisRunStates.tsx`, and add terminal-review entry retaining `left=runId` in `src\clients\web\src\features\analysis\AnalysisRunPage.tsx`. Use the T002 terminal predicate, explain ineligibility for in-progress/unknown review states, and localize in `src\clients\web\src\i18n\en\analysis.json` (using the analysis namespace from lineage). Extend `src\clients\web\tests\unit\features\analysis\analysisRunPage.test.tsx` and `analysisComparisonPage.test.tsx` in that directory for entry, role-aware visibility, fixed origin identity, and exact links; preserve existing fact selection/ranges/polling, match cancellation, and triage navigation.

### Independent connected evidence

- [x] T009 [P] [US1] Add selection/navigation scenarios to `src\clients\web\tests\e2e\simulated\analysis-comparison.spec.ts` using the T001 fixture: each permitted role enters from match and terminal review, both review links target exact selected IDs, current/superseded and same-recording-set history remain distinct, two older terminal runs remain selectable while the current run runs, insufficient history does not invent a pair, and paged/load-more/error behavior reaches older runs beyond the existing helper's twenty-page cutoff. Keep this task's changes in this test file and reuse existing fixture/keyboard helpers; SC-001 and SC-005 require observable identities, not just a successful route.
- [x] T010 [P] [US1] Extend `src\clients\web\tests\e2e\platform\run-supersession.spec.ts` without changing its existing cancellation/supersession assertions or active-season cleanup: make the second run terminal by the existing cancel UI if still active, compare the two exact runs through match entry, assert each recording set/workflow and current/superseded attribution plus honest availability without requiring facts, follow both review links and return, and re-enter from terminal review to verify the fixed left ID. Keep changes in this existing scenario, use the configured `platform-supersession` project dependencies, and do not add endpoints, scripted Manager protocols, or a browser project. This proves connected metadata/navigation, not rich completed Analyst output.

**Checkpoint**: US1 is independently demonstrable with authorized terminal
metadata and exact navigation. This is the suggested first MVP increment,
not permission to omit the remaining mandatory P1 stories from delivery.

## Phase 4: User Story 2 - Understand Differences without False Equivalence (Priority: P1)

**Goal**: Account for every stream once per side, align only unambiguous
capability/scope pairs, and distinguish contracts, inputs, coverage,
availability, and pinned provenance without ranking quality.

**Independent Test**: Compare deterministic rich fixtures with changed
contracts/recording sets/input reports, unmatched and ambiguous streams,
each availability case, and absent optional model provenance. Assert exact
original metadata/counts and zero guessed pairs or synthetic zero sides.

### Pure comparison semantics

- [x] T011 [US2] Extend `src\clients\web\src\features\analysis\comparisonPresentation.ts` and `src\clients\web\tests\unit\features\analysis\comparisonPresentation.test.ts` with collision-safe tuple indexing of `(capabilityId, executionScope)`, deterministic left-first/right-only order, and discriminated `aligned`/`unmatched` entries. Align only exactly one stream per side; for ambiguous groups emit every left/right stream independently, never zip or take a subset. Produce explicit field differences for node identity, capability/schema versions, output type, profile ID/digest, image and optional model; compare dependency coverage only by explicit `upstreamNodeKey`, preserving changed/missing keys, raw policies, optional minimum fractions, and all counts without merging dependencies. Derive every availability branch from the data-model table: zero expected units is not completeness, no accepted units is no accepted results, any missing/failed/blocked/skipped count means incomplete coverage, and complete zero facts requires accepted positive coverage with all other counts zero. Test reordered/collision-like keys, duplicate node rejection, unchanged reports not proving equal snapshots, all-skipped and partial-zero cases, and exact once-per-side accounting without a Cartesian product.

### Honest difference and availability rendering

- [x] T012 [US2] Upgrade `src\clients\web\src\features\analysis\ComparisonStreams.tsx` and the pair-context presentation in `src\clients\web\src\features\analysis\AnalysisComparisonPage.tsx`, with English keys in `src\clients\web\src\i18n\en\analysis.json` and rendering assertions in `src\clients\web\tests\unit\features\analysis\analysisComparisonPage.test.tsx`. Consume T011 entries, label alignment as a navigation aid, explain missing/ambiguous counterparts without fabricated values, display every contract/provenance/input-coverage difference, and distinguish recording-set/workflow differences separately. Show each node's dependency coverage, including "Missing/null input coverage means not reported, not zero coverage", a reported empty dependency list, and unsupported raw policies. Explain every output-availability case, including skipped work and explicit empty-stream workflows; model digest absence remains explicit. Keep full IDs/digests inspectable, preserve all five output counts, and include permanent no-equal-full-input/no-semantic-equivalence wording. Never describe quantity, coverage, provenance or recency as better quality or performance.

### Rich simulated evidence

- [x] T013 [US2] Extend `src\clients\web\tests\e2e\simulated\analysis-comparison.spec.ts` for unique alignment with renamed nodes and changed versions/schema/type/provenance, left-only/right-only and repeated capability/scope groups, different workflows/recording sets, changed/missing input dependency keys and policies/fractions/counts, absent optional model digest, and unknown contract values. For SC-002 assert 100% of fixture stream identities appear exactly once per side and ambiguous groups have zero guessed pairings, including reordered arrays and collision-like tuple strings. Verify independently attributed original values and no equal-input or semantic-equivalence claim; use existing route overrides for malformed fixtures without expanding shared fixture code.
- [ ] T014 [US2] Add the full availability and quantity wording matrix to `src\clients\web\tests\e2e\simulated\analysis-comparison.spec.ts`: complete positive facts, accepted complete zero facts, partial positive/zero facts, no accepted results, zero expected units, all-skipped/failed/blocked/missing units, failed/cancelled runs retaining accepted results, one/both explicit empty stream lists, and incompatible omitted/null collections, invalid counts or inconsistent node/workflow metadata. Assert SC-003's zero success-shaped substitutions and SC-007's absence of performance/quality rankings when the newer run has more/fewer facts or less coverage.

**Checkpoint**: US2 preserves every reported stream and limitation without
requiring facts, objects, media, or a new API.

## Phase 5: User Story 3 - Keep Comparison Authorized and Explicitly Connected (Priority: P1)

**Goal**: A fresh authorized pair is the only ready display; return,
refresh, lost access, session end, offline/reconnect, and delayed requests
cannot restore stale details.

**Independent Test**: Refuse invalid/cross-match/unreadable pairs; delay or
fail either side; change selection with old responses last; revoke access;
end the session; go offline and reconnect while fresh reads are pending or
refused. Assert zero displayed result details after each invalidation.

### Lifecycle freshness

- [ ] T015 [US3] Complete route/history return, browser pageshow return, window focus/visibility return, and reconnect revalidation in `src\clients\web\src\features\analysis\useAnalysisComparison.ts` with tests in `src\clients\web\tests\unit\features\analysis\useAnalysisComparison.test.tsx`. Coalesce duplicate lifecycle signals, synchronously suspend the completed display before asynchronous reads, and start a new generation without changing selected IDs. Both sides and fresh match/team context must succeed even after `/alive` succeeds; cached `useMatchContext`, invalidation alone or immutable terminal results never satisfy the barrier. Verify current/superseded changes become visible only with a newly validated pair, while terminal accepted history remains unchanged; no polling, subscriptions, or global connectivity/session refactor.
- [ ] T016 [US3] Strengthen cancellation/cleanup and adversarial request tests in `src\clients\web\src\features\analysis\useAnalysisComparison.ts`, `src\clients\web\src\features\analysis\AnalysisComparisonPage.tsx`, and their tests `src\clients\web\tests\unit\features\analysis\useAnalysisComparison.test.tsx` and `analysisComparisonPage.test.tsx` in that directory. Cover candidate-page and pair changes, member replacement, concealed 404/disclosed 403 from any required context/status/stream/set read, session-ended middleware, offline, and unmount with delayed completions. Retire and clear pending/completed comparison and candidate state, assert zero comparison-specific queries/data and no shared-cache pollution, and prove retired responses cannot repopulate either selectors or results. Scope cleanup to comparison data except the existing session provider's global clear; retain fixed selected URL IDs through recoverable failures.

### Refusal and recovery presentation

- [ ] T017 [US3] Complete explicit invalid-selection, loading-side, unavailable/not-found, disclosed not-permitted, offline, incompatible, and recoverable-error views in `src\clients\web\src\features\analysis\AnalysisComparisonPage.tsx`, `src\clients\web\src\i18n\en\analysis.json`, and `src\clients\web\tests\unit\features\analysis\analysisComparisonPage.test.tsx`. Reuse `NotFoundPage`, `NotPermittedPage`, `FailureAlert`, session middleware and connectivity conventions; never disclose a concealed run or leave the successful side visible on refusal. Refresh/retry must be labelled, invalidate results first, and load a wholly fresh pair; failures never become empty streams/zero facts. Test all malformed query combinations, mismatched returned run/set identities and team ownership, and nonterminal/unknown-state statuses as appropriate distinct refusal or incompatibility states.
- [ ] T018 [US3] Add `src\clients\web\tests\e2e\simulated\analysis-comparison-access.spec.ts` covering unrelated-team and Registrar-only refusal, duplicate/cross-match/nonterminal/unknown/malformed direct pairs, concealed 404/disclosed 403, one-side delayed/failed reads, explicit retry, pair changes with older responses last, refresh/history/window return after supersession, detected team-access loss, session end, and offline/reconnect with delayed or refused authorized reads despite healthy `/alive`. For SC-004 assert zero result/coverage/provenance details while pending, refused, signed out or offline, not merely presence of a warning; assert both sides are freshly read after every freshness trigger and selected IDs never change automatically.

**Checkpoint**: All required P1 stories are implemented and adversarially
verified; authorized successful old data cannot survive a new barrier.

## Phase 6: User Story 4 - Accessible Comparison without Stored Analysis (Priority: P2)

**Goal**: Selection, comparison and every refusal/recovery state work with
keyboard and assistive technology, while analysis stays strictly in memory.

**Independent Test**: Complete selection and both review-link journeys by
keyboard, run WCAG 2.2 AA checks across every rendered state and narrow
layouts, then inspect all browser stores/caches for zero fixture data.

### Accessible production presentation

- [ ] T019 [US4] Complete semantic headings, explicit left/right labels and detail-link names, selector/load-more/refresh/retry labels, visible focus, concise state announcements, non-color attribution, responsive layout, and fully inspectable long identifiers/digests in `src\clients\web\src\features\analysis\AnalysisComparisonPage.tsx`, `ComparisonStreams.tsx` in that directory, and `src\clients\web\src\i18n\en\analysis.json`. Add keyboard, accessible-name and announcement assertions in `src\clients\web\tests\unit\features\analysis\analysisComparisonPage.test.tsx`; preserve existing translation conventions and English as the shipped language, without duplicating the full stream list in live announcements or adding a UI dependency.

### Independent accessibility and non-persistence evidence

- [ ] T020 [P] [US4] Add axe WCAG 2.2 AA and keyboard evidence to `src\clients\web\tests\e2e\simulated\analysis-comparison.spec.ts` using `src\clients\web\tests\e2e\support\axe.ts` and `keyboard.ts`: exercise selection/load-more, both exact review links, ready/aligned/unmatched, empty/incomplete, loading, unavailable/refused/access-loss, offline, incompatible and recoverable-error states, including narrow layouts and long inspectable IDs/digests. Require zero automated violations and keyboard-operable controls for SC-006; keep this task's edits in the comparison browser spec, with product accessibility changes owned by T019.
- [ ] T021 [P] [US4] Add marker-based non-persistence assertions to `src\clients\web\tests\e2e\simulated\analysis-comparison-access.spec.ts` and extend `src\clients\web\tests\unit\serviceWorker\shellWorker.test.ts` where needed to prove comparison API responses remain outside the shell cache. After ready, pair change, navigation away, access loss, session end, offline and reconnect, inspect local/session storage, IndexedDB and Cache Storage for zero comparison response, coverage or provenance marker values; selection IDs in the URL remain allowed. Reuse T016's in-memory cleanup assertions rather than treating storage inspection as proof of memory cleanup. Keep edits to these two evidence files, leave the service-worker shell-only policy intact, and introduce no persistence/cache plugin.

**Checkpoint**: SC-006 has keyboard, automated accessibility, browser-store
and cache-policy evidence; US3 separately proves in-memory cleanup.

## Phase 7: Polish and Cross-Cutting Delivery Gates

**Purpose**: Make documentation true for the delivered state and verify the
whole connected scope without broadening it.

- [ ] T022 Re-read all affected `docs/architecture/` narratives and make every design/status claim true for the merged implementation, not just the plan's example substitutions: update `docs\architecture\client-applications.md`, `job-processing.md`, `analysts-models-and-hardware.md`, and `platform-implementation.md` in that directory, checking `docs\architecture\match-data-pipeline.md` and other architecture summaries/exclusions for statements delivery makes false. Correct decided/not-yet-implemented and blanket no-comparison wording, distinguish single-run fact review from metadata comparison, and document actual selection, uniqueness, coverage/provenance, freshness, accessibility and memory-only behavior. Update directly related capability summaries in `src\clients\web\README.md`, `README.md`, and `AGENTS.md` wherever they still exclude comparison; preserve existing pending design edits. Distinguish rich simulated evidence from real-platform terminal metadata/navigation; preserve all unrelated playback/visualization/evaluation/agent/offline/production-value and production-readiness deferrals. Validate changed documentation with `node .github\scripts\check-markdown.mjs --files <newline-separated path-list file>`; the argument is a list file, not an individual Markdown path.
- [ ] T023 Execute the complete acceptance matrix in `specs\20261022-190935-web-ui-run-comparison\quickstart.md` against `src\clients\web`: run `npm run build` (existing Kiota generation from the unchanged snapshot), `npm test`, targeted simulated comparison specs and the configured `platform-supersession` chain, then `npm run test:e2e` for all existing browser projects and `node .github\scripts\check-markdown.mjs` from the repository root. Resolve only feature-caused failures in the comparison implementation/evidence paths named above, preserve existing analysis review/cancellation/supersession/triage assertions, and confirm SC-001 through SC-007 with exact stream accounting, zero guessed pairings, zero stale displayed details, zero persisted metadata, no payload/object reads, and no quality-ranking language. Record any actual acceptance-guide corrections in `specs\20261022-190935-web-ui-run-comparison\quickstart.md` only during local implementation; GitHub work jobs leave spec artifacts untouched and report outcomes through their normal task results. Missing Docker/tooling fails visibly; never substitute production databases, weaken evidence, or claim incomplete runs passed.

## Dependencies and Execution Order

### Phase graph

```text
Phase 1: T001
  -> Phase 2: (T002 || T003) -> T004 -> T005
  -> US1: T006 -> T007 -> T008 -> (T009 || T010)
  -> US2: T011 -> T012 -> T013 -> T014
  -> US3: T015 -> T016 -> T017 -> T018
  -> US4: T019 -> (T020 || T021)
  -> Polish: T022 -> T023
```

### Actual prerequisites and story dependencies

- T002/T003 depend on the contract-correct fixtures of T001; neither adapter
  task consumes the other's changes. T004 uses the completed adapter/model
  boundaries; T005 consumes its validators and both strict adapters.
- US1 depends on the foundation. T006 creates selection, T007 consumes the
  loader to render it, and T008 exposes the working route/entry points.
  T009/T010 then exercise independent simulated and real-platform files.
- US2 depends on the independently testable US1 page. T011 extends its
  helpers, T012 renders the new semantics, and T013/T014 exercise distinct
  semantic matrices sequentially in the same browser file.
- US3 depends on the existing US1 route/load scope and US2 presentation to
  test zero leaked result details. It completes lifecycle triggers and
  adversarial cleanup before refusal/recovery and connected access evidence.
  Core authorization and pair isolation are already foundational.
- US4 depends on all P1 behavior and its full state matrix. T019 owns
  production accessibility changes; T020/T021 add disjoint evidence after it.
- T022 depends on all stories and their actual evidence; T023 follows the
  documentation and verifies delivery. Final feature-caused corrections
  must rerun affected focused checks before the final gate.

The stories are independently testable increments, not independent branches:
they deliberately share the existing analysis page, helpers, translations,
and load scope. Do not run story implementations concurrently.

### Parallel opportunities and examples per story

| Scope | Runnable group | Disjoint footprints or reason for sequential work |
| --- | --- | --- |
| Foundation | T002 + T003 | Run model/adapter and dependent run fixtures versus fact adapter and its own adapter test file. |
| US1 | T009 + T010 | Simulated comparison spec versus existing real-platform supersession spec; both follow T008 and use completed fixtures. |
| US2 | None within this story | Helpers, page/stream rendering, translations and rich browser cases have ordered dependencies or shared files. |
| US3 | None within this story | Freshness, cleanup, refusal rendering and adversarial evidence depend on one evolving load scope. |
| US4 | T020 + T021 | Comparison browser accessibility spec versus access browser persistence spec and shell-worker unit tests; product changes are already complete. |

For example, launch the two named US1 evidence tasks together only after
T008; launch the two US4 evidence tasks together only after T019. Run US2's
T011 through T014 and US3's T015 through T018 in the listed order rather
than manufacturing parallel groups with overlapping files. Sharing a live
browser/platform environment still requires the existing runner isolation.

## Requirement and Acceptance Coverage

| Requirement or outcome | Owning tasks |
| --- | --- |
| FR-001 through FR-005; SC-001, SC-005 | T002, T004 through T010 |
| FR-006 through FR-011; SC-002, SC-003, SC-007 | T002 through T004, T007, T011 through T014 |
| FR-012 through FR-014; SC-004 | T004 through T006, T015 through T018 |
| FR-015; no reusable or persisted analysis data | T005, T006, T016, T018, T021 |
| FR-016; existing read-only adapter boundary | T002 through T005, T023 |
| FR-017; SC-006 | T019 through T021 |
| FR-018; connected and adversarial evidence | T001 through T021, T023 |
| FR-019; architecture true at merge | T022 |
| FR-020; preserved boundaries and regressions | T008, T010, T022, T023 |

## Implementation Strategy

1. Complete Setup and Foundation, validating adapter behavior and load
   isolation before exposing the route.
2. Deliver US1 as the first demonstrable MVP: explicit selection, exact
   context, every independently attributed stream, and connected links.
   Validate both evidence settings; do not claim full feature acceptance yet.
3. Add US2's honest alignment/difference/availability semantics, then US3's
   full freshness/refusal/access-loss matrix. All three P1 stories are
   required; do not ship a partial comparison as complete.
4. Finish US4's accessibility and non-persistence evidence, correct the
   architecture and capability summaries, then run the complete delivery gate.
5. Keep failures explicit and fixes scoped. No deployment, publication,
   commit, push, new production decision, or readiness claim is authorized
   by this task list.
