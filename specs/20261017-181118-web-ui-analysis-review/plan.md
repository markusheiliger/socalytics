# Implementation Plan: Web UI Connected Analysis Review

**Branch**: `20261017-181118-web-ui-analysis-review` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261017-181118-web-ui-analysis-review/spec.md`

## Summary

Add a connected, read-only analysis review to the Web UI at `src/clients/web/`
that shows, for one analysis run of a match, the run and node status and the
run's indexed facts per capability with coverage and provenance.

- **Route**: `/matches/:matchId/analysis-runs/:runId`, with the chosen fact
  stream and match-time range in the search parameters (`stream`, `fromMs`,
  `toMs`); opened from a new link on every run in the match view's existing
  run list (`AnalysisRunStates`).
- **Platform reads only**: `getAnalysisRun` (existing), and
  `listAnalysisRunFactStreams` and `listAnalysisRunFacts`, which
  [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md)
  adds to the OpenAPI snapshot. No new platform operation; the Kiota client is
  regenerated from the snapshot by the existing `npm run build`.
- **Adapters**: `analysisRunAdapter` gains `getRun`; a new
  `analysisFactAdapter` maps fact streams, fact pages, coverage, and
  provenance into application-owned models and translates the review's
  refusals (not found, capability not in run, rejected continuation) into
  typed outcomes without changing the shared problem mapping.
- **Views**: run status with node list, fact-stream list, and a fact browser
  with previous/next paging (continuations kept in memory per selection), a
  match-clock range form, coverage list, provenance with shortened, copyable
  digests, bounded payload summaries with the full payload as text, and one
  worded message per explicit state, derived by a pure state function.
- **Refresh**: run status and the stream list refresh every 10 seconds while
  the run is requested or running and the page is visible; an open fact page
  is not replaced but offers a reload when the stream's count or coverage
  changed.
- **Online only**: TanStack Query keeps data in memory; the service worker
  already leaves `/api/` unhandled, and tests prove analysis responses never
  reach Cache Storage or browser storage.
- **Evidence**: Vitest unit tests, the Playwright `simulated` project with
  scripted run, stream, and fact responses, and a new chained
  `platform-analysis` project that opens the review of the run created by the
  upload flow on the real platform.

Design rationale: [research.md](research.md). Client models:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: TypeScript 5.9 (strict) on Node.js 24, as pinned by
`src/clients/web/package.json`.

**Primary Dependencies**: the Web UI's existing pinned dependencies (React 19,
React Router 7, TanStack Query 5, MUI 7, i18next and react-i18next,
`@microsoft/kiota-bundle`, Vite 7, Vitest 4, Testing Library,
`@playwright/test` 1.63.0, `@axe-core/playwright`). No new dependency. Browser
API: Clipboard API for copying digests (optional; the full value stays
selectable text).

**Storage**: none in the browser. Analysis data lives only in the in-memory
TanStack Query cache and component state; no persister, IndexedDB, local or
session storage, and no service-worker caching (FR-018).

**Testing**:

- `npm test` (Vitest): adapters (mapping, refusals, payload normalization),
  match-clock formatting and parsing, payload summary, state derivation,
  paging hook, refresh rules, components with Testing Library, i18n keys,
  service-worker classification of the analysis paths.
- `npm run test:e2e -- --project simulated`: `analysis-review.spec.ts` (run
  states, streams, three or more fact pages, ranges, every explicit state,
  refresh) and `analysis-review-access.spec.ts` (roles, axe, keyboard,
  offline, no cached data) against the routed simulated API.
- `npm run test:e2e` platform chain: new `platform-analysis` project
  (`analysis-review.spec.ts`) after `platform-upload`, against the AppHost
  `e2e` mode.

**Target Platform**: Chromium-based browsers (automated: Chromium on Linux),
as for the rest of the Web UI.

**Project Type**: web application (single-page application) consuming the
platform API.

**Performance Goals**: status changes of a running run visible within 15
seconds (SC-004) through a 10-second refresh; at most one fact page (platform
default page size) rendered at a time.

**Constraints**:

- Only the three platform reads above (FR-017); no run controls, playback,
  visualizations, comparison, or export (FR-021).
- Platform text rendered as text only; no `dangerouslySetInnerHTML`; the
  existing Content Security Policy and Trusted Types baseline unchanged
  (FR-020).
- English through translation keys; keyboard operable; WCAG 2.2 AA rule
  checks (FR-019).
- The shared `mapProblem` stays unchanged; review-specific outcomes are
  derived in the analysis adapters.

**Scale/Scope**: one new route and view composed of seven components, two
adapters (one new, one extended), one translation namespace, two simulated
browser specs, and one platform browser spec.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements the analysis review of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) and the member read side of [Segment And Match Analysis Scope](../../docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope) through [Capability-Scoped Result Access](../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access); stays online only per [Offline Mode](../../docs/architecture/client-applications.md#offline-mode). The design update is applied now (see Architecture Updates). |
| II. Source-area ownership | Pass | Changes only under `src/clients/web/`; no new `src/` child. |
| III. API-first control plane | Pass | The Web UI reads only through `/api/v1` operations via the generated client behind adapters; it holds no business logic beyond presentation (coverage and provenance come from the platform); no media. |
| IV. Evidence over claims | Pass | Unit, simulated browser, and platform browser tests (FR-022); nothing claims production readiness. |
| V. Focused, minimal changes | Pass | No new dependency, platform operation, or project; reuses the match context, failure alert, connectivity, and refresh patterns. |
| Technology and tooling | Pass | Needs Node.js 24, Kiota 1.36.0, Playwright 1.63.0 Chromium, the .NET SDK, and Docker for the platform project, all provided by `.github/actions/environment-setup` and `environment-verify` through the merged [Web UI Verification Environment](../archive/20261012-145505-web-ui-environment/spec.md); no environment spec is needed. |

Post-design re-check: Pass (no violations; Complexity Tracking is empty).

## Project Structure

### Documentation (this feature)

```text
specs/20261017-181118-web-ui-analysis-review/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── routes-and-roles.md
│   ├── analysis-adapters.md
│   └── review-states.md
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
src/clients/web/
├── src/
│   ├── router.tsx                                   # route /matches/:matchId/analysis-runs/:runId
│   ├── i18n/
│   │   ├── index.ts                                 # registers the analysis namespace
│   │   └── en/analysis.json                         # new namespace
│   ├── platform/
│   │   ├── models/analysis.ts                       # run status, streams, fact page, coverage, provenance
│   │   └── adapters/
│   │       ├── analysisRunAdapter.ts                # + getRun (getAnalysisRun)
│   │       └── analysisFactAdapter.ts               # listStreams, listFacts, review outcomes
│   └── features/
│       ├── lineage/AnalysisRunStates.tsx            # link to each run's review
│       └── analysis/
│           ├── AnalysisRunPage.tsx                  # route view: access, run, streams, selection
│           ├── RunStatusSection.tsx                 # run details and node list
│           ├── FactStreamList.tsx                   # streams with counts and coverage summary
│           ├── FactBrowser.tsx                      # facts, paging, reload notice, states
│           ├── FactRangeForm.tsx                    # match-clock range input
│           ├── CoverageList.tsx
│           ├── ProvenanceDetails.tsx                # shortened, copyable digests
│           ├── analysisKeys.ts                      # query keys and refresh interval
│           ├── reviewState.ts                       # explicit state derivation
│           ├── useFactPages.ts                      # in-memory continuation stack
│           ├── matchClock.ts                        # format and parse match time
│           └── payloadSummary.ts                    # bounded summary and full text
└── tests/
    ├── unit/
    │   ├── platform/adapters/analysisRunAdapter.test.ts      # + getRun
    │   ├── platform/adapters/analysisFactAdapter.test.ts
    │   ├── features/analysis/*.test.ts(x)
    │   └── serviceWorker/shellWorker.test.ts                 # analysis paths stay unhandled
    └── e2e/
        ├── support/simulatedApi.ts                           # run, stream, and fact routes
        ├── simulated/analysis-review.spec.ts                 # run status, fact browsing, explicit states
        ├── simulated/analysis-review-access.spec.ts          # roles, axe, keyboard, offline, no stored data
        └── platform/analysis-review.spec.ts
src/clients/web/playwright.config.ts                          # chained platform-analysis project
```

**Structure Decision**: the review is a new feature folder
`src/features/analysis/` beside `lineage`, following the Web UI's feature,
adapter, and model layering; the match view keeps its run list and only gains
a link per run.

## Architecture Updates

### Narratives

- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): the analysis review for coaches, the first part of "team and match review, analytics" for analysis results.
- [Match Data Pipeline → Segment And Match Analysis Scope](../../docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope): the member read side of the ordered fact stream, shown in the Web UI.
- [Analysts, Models, and Hardware → Capability-Scoped Result Access](../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access): the member fact read (added by the batch sibling) gains its Web UI consumer; no design change.
- [Client Applications → Offline Mode](../../docs/architecture/client-applications.md#offline-mode) and [Platform Implementation Profile → Client Technology](../../docs/architecture/platform-implementation.md#client-technology): unchanged rules (online only; the service worker holds no user data) that the review follows.

### Design updates (applied now, worded as decided but not implemented)

- `client-applications.md`, Web UI: a paragraph that the connected analysis review is decided as follows and not yet implemented: from a match, members who may read it open a run's review with run and node status, refreshed while the run is in progress, and browse each capability's indexed facts in match-time order with range filtering, forward and backward paging, coverage windows, and run-pinned provenance, with explicit empty, incomplete, and unavailable states; it is online only, holds analysis data only in memory, and offers no run controls, playback, visualizations, comparison, or export. Implementation turns it into present tense.
- `match-data-pipeline.md`, Segment And Match Analysis Scope: the sentence on the member read gains that the Web UI's analysis review is its first consumer (decided, not yet implemented). Implementation turns it into present tense.

### Status statements at merge

- `client-applications.md`, Web UI: "team and match review, analytics, and agent conversations for coaches (analysis review and agent conversations are not implemented yet)" → the analysis review is implemented as a connected workflow; analytics beyond the review (visualizations, comparison) and agent conversations are not implemented yet.
- `platform-implementation.md`, Architecture Reassessment ("the other Web UI client workflows (analysis review and agent conversations) … remain unsupported by implementation evidence") → only agent conversations remain; the analysis review joins the implemented Web UI workflows in "the online-only Web UI foundation exists as development evidence, including …".
- `platform-implementation.md`, Acceptance Evidence: the Web UI browser-test bullet gains the analysis review (`simulated` and `platform` projects).
- `platform-implementation.md`, Client Technology: re-check that "The online-only Web UI foundation is implemented as development evidence" still describes the review accurately (no change expected).
- `match-data-pipeline.md`, Segment And Match Analysis Scope: the member-read sentence ("decided, but not yet implemented, to be readable by authorized members as well") is corrected together with the batch sibling's statement; the Web UI review is described as implemented once both have merged.
- Any other statement in `docs/architecture/` that presents the Web UI's analysis review, or the display of analysis results to members, as planned, future, or excluded.

The documentation task MUST make these narratives and every status statement
this feature affects true for the merged state, re-reading
`docs/architecture/` (and `AGENTS.md` "Current State", `src/clients/README.md`, and the root
`README.md`) at implementation time; the rewrites
above are examples, not the whole job.

## Complexity Tracking

No violations.
