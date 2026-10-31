# Research: Web UI Analysis Fact Timeline

No `NEEDS CLARIFICATION` remains. The decisions below resolve the planning choices the spec leaves open (zoom levels, loading bound, rendering, state handling).

## R1: Data source and loading

- **Decision**: Load each lane's facts page by page through the existing `AnalysisFactAdapter.listFacts` (no `fromMs`/`toMs`, continuation tokens followed until the last page or the bound), and take the lane's coverage windows from the first page's `coverage`. Lanes come from the already loaded `listStreams` result of the review page.
- **Rationale**: FR-014 allows only `listAnalysisRunFactStreams` and `listAnalysisRunFacts`. The facts read orders by match time and returns the coverage of the requested range with every page, so one pass without a range yields the facts and the whole-match windows. The request keeps the platform's default page size (the `AppHost` development value is 100, the maximum 500); the timeline does not depend on a page size and adds no request parameter.
- **Alternatives considered**: One range-limited read per visible extent (more requests, repeats on every pan, and partial density per extent is harder to explain); a new aggregation endpoint (out of scope, FR-014); reading payloads (not needed, never interpreted, FR-012).

## R2: Loading bound (FR-009)

- **Decision**: At most `5_000` facts per stream and `20_000` facts across all lanes of a timeline (development values, not production values; the constants live in `timeline/timelineBins.ts` and the allowance in `timeline/timelineLane.ts`). The per-lane allowance is `min(5_000, floor(20_000 / lane count))`, and paging for a lane stops once the loaded facts reach it. A lane whose reported `factCount` exceeds the loaded facts is `partial` and states "based on N of M facts".
- **Rationale**: A fixed, deterministic allowance bounds memory regardless of the order lanes finish, and the reported `factCount` of the stream summary is the reference for "partial" (spec Assumptions). Only `startMs` and `endMs` are retained per fact after a page is read; payloads are dropped immediately, so memory per fact is small.
- **Alternatives considered**: First-come budget (non-deterministic partial lanes); unbounded loading (violates FR-009); loading only the visible extent (see R1).

## R3: Zoom levels and extent

- **Decision**: Four fixed zoom levels of 30 bins each: 10 s bins over a 5-minute extent, 30 s over 15 minutes, 1 minute over 30 minutes, and 5 minutes over 150 minutes. The overall extent is the union of all coverage windows and loaded facts of all lanes, rounded out to the bin size; the visible extent is a window of `30 × bin size` that moves by half its width (earlier or later), is clamped to the overall extent, and starts at the overall start. The default zoom is the finest zoom level whose 30 bins cover the overall extent, else the widest (5 m). The default zoom is chosen once, after all lanes have left `loading`; a user-chosen zoom and extent are kept across reloads. `extentStartMs` is floored to a multiple of `binMs` and clamped to `[floor(extentStart), extentEnd - bins*binMs]`.
- **Rationale**: A constant bin count gives a constant lane width and a predictable text alternative (30 rows per lane). 150 minutes covers matches with extra time (FR/US1 scenario 5), and the match clock already formats minutes beyond 90 (`formatMatchClock`).
- **Alternatives considered**: Continuous zoom (no stable alternative listing, harder keyboard model); a fit-to-extent single level (hides short gaps).

## R4: Bin derivation

- **Decision**: A pure function derives, per bin `[s, e)`, the fact count (every fact with `startMs < e` and `endMs > s` counts once; a zero-length fact `startMs == endMs` counts in the bin containing `startMs`) and the coverage class from the lane's windows: `accepted` (the union of `accepted` windows covers the whole bin), `notAnalysed` (the bin lies fully inside windows of other states, unknown states included), `partial` (some but not all of the bin is covered by accepted windows, or by a mix of states), and `none` (no window overlaps the bin). The bin state is then `facts` when the count is above zero, else `analysedNoFacts` (count `0`, only when fully accepted), `partial`, `notAnalysed`, or `noCoverage`. Counts are kept next to the coverage class, so a `facts` bin inside non-accepted coverage still shows its coverage class in the detail. For a `partial` lane (loading stopped at the lane allowance, R2), the loaded horizon is the start of the last loaded fact: a bin that starts after the horizon and has count `0` is not `analysedNoFacts` but the distinct state `notLoaded`, labelled "facts beyond the loading bound not shown" and also shown in the text alternative; `analysedNoFacts` stays only for bins up to the horizon and for lanes that are not partial.
- **Rationale**: Implements FR-002, FR-004, and FR-006: a zero is only ever shown for a fully accepted bin, and never for a bin in a partial lane that may hold unloaded facts (FR-009).
- **Alternatives considered**: Deriving zero from the absence of facts (forbidden by the architecture's Capability-Scoped Result Access).

## R5: Rendering and accessibility

- **Decision**: Render the lanes as DOM (not canvas): each lane is a `role="group"` of button-like bin cells with a roving tabindex (arrow keys move between bins, Shift+arrow extends a span, Enter or Space selects, Escape clears), each cell carries an accessible name with range, count or coverage state, and a pattern plus a short glyph for the state besides colour. A "Show text alternative" toggle renders one `<table>` per lane with the same bins from the same derived data. Status changes use one polite live region. Zoom is a labelled button group; moving the extent uses "Earlier" and "Later" buttons. Existing MUI components, theme, and `@axe-core/playwright` are reused; no charting dependency is added.
- **Rationale**: A DOM rendering is directly keyboard- and screen-reader-operable and testable by role and text, and a chart library would add a dependency for a simple bin strip (constitution Principle V).
- **Alternatives considered**: Canvas/SVG chart libraries (extra dependency, accessibility work duplicated); a table only (no picture).

## R6: Refresh, memory, and access loss

- **Decision**: Lane data is a TanStack Query query per lane with `gcTime: 0`, `staleTime: Infinity`, no persistence, keyed by run, node key, fact count, and the coverage counts of the stream summary, so it reloads exactly when the existing 10-second streams refresh reports changed results (the same signal as the fact browser's "new results") and keeps the member's selection, zoom, and extent, which live in the review page's state. During a reload the lane keeps showing its previous data, marked as updating and announced politely, until the new pages are complete, so the status text (run state, counts, in progress) appears within 15 seconds independent of paging time. While the run is in progress and a lane has no accepted facts, it shows `inProgress` and no density. All timeline queries are removed (`removeQueries` on the `['analysis', 'timeline']` prefix) on unmount, when the connection is lost, and when a read is refused as forbidden or not found. A continuation the platform rejects restarts the lane's loading once; a second rejection makes the lane `unavailable`.
- **Rationale**: Reuses the review's refresh mechanism (FR-013) and its discard rules (FR-015) without new browser storage; the service worker already caches only the application shell.
- **Alternatives considered**: A separate polling interval (duplicates the refresh); keeping data across leaving the view (violates FR-015).

## R7: Jump to the fact list and return

- **Decision**: The timeline is open by default when the run review shows (the review's existing open step is opening the run review from the match page, so SC-001's three interactions are select, "Show facts", and that step). "Show facts" calls the review page's existing `changeRange` with the selection's `fromMs` and `toMs` for the lane's `nodeKey` (the address stays `?stream=…&fromMs=…&toMs=…`), closes the timeline (discarding its data), and the page shows a "Back to timeline" action that reopens it. Zoom level and extent live in the page state, so the reopened timeline restores them and reads afresh. A span's range is `[start of first bin, end of last bin)`.
- **Rationale**: Satisfies US3 (exactly that range, paging restarts, fresh authorized read on return) with no change to the address contract.
- **Alternatives considered**: Keeping the timeline mounted beside the list (data not discarded, no fresh read on return); putting zoom and extent in the address (spec Assumptions say they need not be).

## R8: Environment

- **Decision**: No new framework, SDK, or tool: React, MUI, TanStack Query, i18next, Vitest, Playwright, and axe are already installed and covered by the existing `environment-setup` and `environment-verify` actions. No environment spec is needed and none is a dependency.
- **Alternatives considered**: None.
