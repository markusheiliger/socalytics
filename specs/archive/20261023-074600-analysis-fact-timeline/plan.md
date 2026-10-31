# Implementation Plan: Web UI Analysis Fact Timeline

**Branch**: `20261023-074600-analysis-fact-timeline` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/archive/20261023-074600-analysis-fact-timeline/spec.md`

## Summary

Add a match-time timeline to the connected analysis review of a run: one lane per fact stream over a shared axis, with per-bin fact density at one of four fixed zoom levels, the stream's coverage windows kept distinct from zero facts, a keyboard-operable graphic with a text-alternative table, and a "Show facts" jump into the existing fact list with exactly the selected range. The timeline is read-only, online-only, and memory-only. It uses only the existing `listAnalysisRunFactStreams` and `listAnalysisRunFacts` reads through the review's adapters; density is derived in the browser from facts loaded page by page within a development bound (research R1, R2). No platform, contract, or OpenAPI change.

## Technical Context

**Language/Version**: TypeScript 5 with React (Node.js 24, Vite), as in `src/clients/web`

**Primary Dependencies**: Existing only: React, MUI, TanStack Query, react-router, i18next, the generated Kiota client; no new dependency

**Storage**: N/A (memory only; no browser storage, no service-worker caching)

**Testing**: Vitest unit tests; Playwright `simulated` and `platform` projects with `@axe-core/playwright`

**Target Platform**: Browsers supported by the Web UI; online only

**Project Type**: Web application (client area `src/clients/web`)

**Performance Goals**: A status change visible within 15 s while a run is in progress (existing 10 s refresh); loading bounded to 5 000 facts per stream and 20 000 per timeline (development values, research R2)

**Constraints**: Read-only; no new platform operation (FR-014); payloads never interpreted or retained (FR-012); English only (FR-020)

**Scale/Scope**: One review view, about 6 new components, 2 pure derivation modules, one lane-loading hook, one i18n namespace extension

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design: unchanged, all pass.*

| Principle / constraint | Status |
| ---------------------- | ------ |
| I. Architecture is the design authority | Pass: conforms to [Client Applications → Web UI](../../../docs/architecture/client-applications.md#web-ui) and [Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access); the `## Architecture Updates` section below names the narrative changes and a documentation task is required |
| II. Source-area ownership | Pass: all code is in `src/clients/web` |
| III. API-First control plane | Pass: the Web UI consumes the two existing member reads through application-owned adapters; no business logic or database access; no video bytes |
| IV. Evidence over claims | Pass: unit tests and the `simulated` and `platform` browser projects (FR-021) |
| V. Focused, minimal changes | Pass: no new dependency or abstraction beyond the timeline's derivation and components |
| Environment | Pass: React, MUI, Vitest, Playwright, and axe already exist in `environment-setup` and `environment-verify`; no environment spec is needed (research R8) |

## Architecture Updates

**Narratives**:

- [Client Applications → Web UI](../../../docs/architecture/client-applications.md#web-ui): the connected analysis review paragraph gains the match-time fact timeline as its first implemented visualization (lanes per fact stream, density at fixed zoom levels, coverage windows distinct from zero facts, text alternative, jump to the range-filtered fact list, online-only and memory-only).
- [Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access): the paragraph on the member fact reads names the timeline as a second consumer of `listAnalysisRunFacts` and `listAnalysisRunFactStreams`, with density derived in the browser from bounded page-by-page reads and no new operation.

**Design updates**: None applied to `docs/architecture/` now. The plan's decisions (no new platform operation, browser-derived density, development loading bound of 5 000 facts per stream and 20 000 per timeline, four fixed zoom levels, in-memory-only data) are Web UI implementation choices within the existing design; the architecture states them as implemented at merge through the documentation task below. The loading bound and zoom levels are development values and MUST be worded as such, not as production values.

**Status statements at merge**:

- `client-applications.md`, Web UI summary list (around line 87): "visualizations, the Coach Agent conversations and consultations are not implemented yet" is false for the timeline; it must say the fact timeline is implemented and that other visualizations (for example spatial ones) and the Coach Agent conversations and consultations are not.
- `client-applications.md`, connected analysis review paragraph (around line 105): "the current implementation offers no run controls, visualizations, cross-run comparison, or export" must name the timeline and keep only the visualizations that still do not exist (spatial visualizations, heatmaps, pitch views) as absent.
- `client-applications.md`, run comparison paragraph (around line 125): "no … visualizations" describes the not yet implemented comparison view and stays true; the documentation task re-reads it and leaves it unless the wording would now contradict the timeline.
- `analysts-models-and-hardware.md`, Capability-Scoped Result Access (around lines 345 to 349): "the Web UI's connected analysis review is its consumer" must name the review's fact timeline as a consumer too.
- Outside `docs/architecture/`, `AGENTS.md` (Current State: "no run controls, visualizations, comparison, or export" in the connected analysis review) and any `README.md` or `src/clients/web` README statement of the review's scope are corrected in the same documentation task.

**Documentation task requirement**: `/speckit-tasks` MUST include a documentation task that makes the narratives above and every status statement the feature affects in `docs/architecture/` true for the merged state, re-reading the documents at implementation time (the listed rewrites are examples, not the whole job), including planned, future-tense, or unimplemented wording outside the sections named here, and that passes `node .github/scripts/check-markdown.mjs`.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261023-074600-analysis-fact-timeline/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/
└── tasks.md             # /speckit-tasks, not created by /speckit-plan
```

No `contracts/` folder: the feature adds no external interface (FR-014); it consumes existing operations whose contracts are unchanged.

### Source Code (repository root)

```text
src/clients/web/
├── src/
│   ├── platform/
│   │   └── models/analysis.ts                    # unchanged types reused
│   ├── features/analysis/
│   │   ├── analysisKeys.ts                       # timeline query key prefix and lane key
│   │   ├── AnalysisRunPage.tsx                   # offers the timeline, jump and return, discards data
│   │   └── timeline/
│   │       ├── timelineBins.ts                   # pure: zoom levels, extent, bin and coverage derivation
│   │       ├── timelineLane.ts                   # pure: lane state, partial and bound rules
│   │       ├── useLaneFacts.ts                   # pages one stream within the allowance, restart once
│   │       ├── AnalysisTimeline.tsx              # zoom, extent, selection, status announcements
│   │       ├── TimelineLane.tsx                  # one lane: roving-focus bins, state label, detail
│   │       └── TimelineTextAlternative.tsx       # per-lane table from the same derived data
│   └── i18n/en/analysis.json                     # timeline keys
└── tests/
    ├── unit/features/analysis/
    │   ├── analysisKeys.test.ts                  # timeline key case
    │   └── timeline/                             # derivation, lane state, components
    └── e2e/
        ├── support/simulatedApi.ts               # timeline and coverage fixtures
        ├── simulated/analysis-timeline.spec.ts
        └── platform/analysis-timeline.spec.ts
```

**Structure Decision**: The timeline is a folder under the existing `features/analysis` review feature; pure derivation is separate from rendering so the bin rules are unit-testable (FR-021). The existing `analysisFactAdapter` is used as is.

## Design Notes

- **Loading and bound** (R1, R2): per lane, follow continuation tokens without a range until the last page or the allowance `min(5 000, floor(20 000 / lanes))`; keep only `startMs`, `endMs`; coverage from the first page.
- **Bins and states** (R3, R4): four zoom levels of 30 bins; a zero count appears only for a fully accepted bin; unknown states are raw and not analysed.
- **Accessibility** (R5): DOM cells with roving tabindex, pattern and glyph per state, one polite live region, a text-alternative table from the same data.
- **Refresh and discard** (R6): lane queries are keyed by the stream summary's count and coverage so they reload when the existing 10 s refresh reports changes; selection, zoom, and extent stay in page state; queries are removed on unmount, offline, sign-out, and forbidden or not found.
- **Jump and return** (R7): "Show facts" uses the page's `changeRange`; "Back to timeline" reopens with the kept zoom and extent and reads afresh.

## Complexity Tracking

No constitution violations.
