# Feature Specification: Web UI Analysis Fact Timeline

**Feature Branch**: No branch created; specification authored on the existing checkout.

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Club Admins, Coaches, and Viewers authorized for the owning team see, in the connected analysis review of a run, a match-time timeline of the accepted fact streams of each capability node instead of only paged lists, so they can see where in the match facts occur, where coverage windows leave gaps, and jump from a timeline position to the existing fact list at that match-time range. Read-only, online-only, memory-only; uses only the existing member fact stream, fact, and coverage reads and the run-pinned provenance already shown; per-stream density over match time at a selectable zoom level; coverage windows and explicit missing, incomplete, empty, and unavailable states kept distinct from zero facts; keyboard-operable and screen-reader-accessible alternatives to the graphic; removal of the data when access is lost or the view is left; tests in the simulated and platform browser projects. Out of scope: spatial visualizations, capability-specific payload rendering, video playback, run comparison, export, sharing, agent or MCP operations, new API endpoints, offline local data."

## User Scenarios & Testing *(mandatory)*

The actors are the club members who may read a match: Club Admins, and Coaches and Viewers of the team that owns the match. The timeline is a view inside the connected analysis review of [Web UI Connected Analysis Review](../20261017-181118-web-ui-analysis-review/spec.md), over the member fact reads of [Accepted Fact Indexing and Capability-Scoped Fact Queries](../20261017-175648-fact-indexing-queries/spec.md). It implements the first named visualization of [Client Applications → Web UI](../../../docs/architecture/client-applications.md#web-ui) and follows [Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access): unavailable or incomplete analysis is reported explicitly and never inferred as zero.

### User Story 1 - See where in the match facts occur (Priority: P1)

A Coach opens the review of a run and sees, above the capability's fact list, a timeline of the match: one lane per capability node (fact stream) of the run, with the density of accepted facts over match time at a chosen zoom level. The Coach sees at a glance that a capability's facts cluster in the first half, and which streams have facts at all, without paging through lists.

**Why this priority**: It is the user value of the feature: replacing "only paged lists" with a picture of where facts occur.

**Independent Test**: For a run whose streams have facts spread unevenly over several pages, a Coach opens the review and sees one lane per stream whose density bins match the facts' match-time ranges; changing the zoom level changes the bin size and the visible match-time extent; the totals shown agree with the stream's reported fact count.

**Acceptance Scenarios**:

1. **Given** an opened run with fact streams, **When** the review shows its timeline, **Then** it shows one lane per stream, labelled with the capability id and version and the node key, over a shared match-time axis in the match-clock format of the review, with the density of facts per match-time bin.
2. **Given** a fact whose half-open match-time range spans several bins, **When** density is computed, **Then** the fact is counted in every bin its range overlaps, and a fact is never counted twice in one bin.
3. **Given** the timeline, **When** the member selects a zoom level, **Then** the bin size and the visible match-time extent change accordingly, the axis labels follow, and the member can move the visible extent earlier and later without leaving the zoom level.
4. **Given** a stream with more facts than the review loads for the timeline, **When** the timeline is shown, **Then** it states explicitly how many of the reported facts the density is based on and marks the density as partial; it never presents partial density as complete.
5. **Given** matches longer than 90 minutes (for example with extra time), **When** the timeline is shown, **Then** the axis and labels display match-clock times beyond 90 minutes correctly.

---

### User Story 2 - See where coverage leaves gaps, distinct from zero facts (Priority: P1)

A Viewer looks at the timeline of a run that failed part-way. Each lane shows the stream's coverage windows (`accepted`, `missing`, `failed`, `blocked`, `skipped`) along the match-time axis, so a bin without facts inside an accepted window reads as "analysed, no facts", while a bin inside a missing, failed, blocked, or skipped window reads as "not analysed" and a bin outside every reported window reads as "no coverage reported". The Viewer is never shown a zero where the analysis simply did not run.

**Why this priority**: The architecture forbids inferring zero facts from missing results; a timeline that blurs this would mislead members more than the lists do.

**Independent Test**: For a run with a failed segment, a skipped window, an accepted window without facts, and a stream without coverage, the Viewer sees four visibly and textually distinct bin states, and for a running run the lane says analysis is still in progress instead of showing zero.

**Acceptance Scenarios**:

1. **Given** a stream with coverage windows, **When** the timeline is shown, **Then** each window is drawn in its lane with its state, and the state is conveyed by text, pattern, or shape as well as by colour.
2. **Given** a bin inside an `accepted` window with no facts, **When** it is shown, **Then** it is marked as analysed without facts, distinct from a bin with facts and from a bin in a non-accepted window.
3. **Given** a bin partly inside accepted and partly inside non-accepted windows, **When** it is shown, **Then** it is marked as partially covered and not as zero.
4. **Given** a bin outside every coverage window the platform reports, **When** it is shown, **Then** it is marked as having no reported coverage, not as zero.
5. **Given** a run that is requested or running, **When** a lane has no accepted facts yet, **Then** the lane states that the run is in progress and shows no density, and the lane refreshes with the run's status refresh without moving the member's selection.
6. **Given** a stream whose analysis is complete and produced no facts, **When** its lane is shown, **Then** the lane states that the analysis is complete without facts and shows its accepted windows.
7. **Given** a capability that is not part of the run, a lane whose facts cannot be loaded, or the platform being unreachable, **When** the timeline is shown, **Then** that lane (or the timeline) shows the review's existing explicit state for it (capability not part of the run, unavailable, error with reload action) and no density.

---

### User Story 3 - Jump from a timeline position to the fact list (Priority: P1)

The Coach selects a position or a span on a lane, for example a dense cluster, and chooses "Show facts". The review opens the existing fact list of that stream limited to that match-time range, so the Coach reads the facts behind the picture.

**Why this priority**: It connects the overview to the facts the member already trusts, and it is how the picture becomes actionable.

**Independent Test**: A Coach selects a bin on a lane and jumps; the fact list shows exactly the facts overlapping that bin's range, the address carries the stream and range as the review's existing address does, and returning to the timeline keeps the zoom level and visible extent.

**Acceptance Scenarios**:

1. **Given** a lane, **When** the member selects a bin or a span of bins, **Then** the selection is shown with its match-time range, its fact count (or its coverage state when it is not analysed), and a "Show facts" action.
2. **Given** a selection, **When** the member chooses "Show facts", **Then** the review shows the existing fact list of that stream with exactly that range as the match-time range filter, paging restarts at its first fact, and the address carries the stream and range.
3. **Given** a selection in a window that is not analysed, **When** the member chooses "Show facts", **Then** the fact list shows the existing no-facts-in-range state with the coverage for the range, not an empty list without explanation.
4. **Given** the member returns from the fact list to the timeline, **When** the timeline is shown again, **Then** its zoom level and visible extent are as the member left them and the data is reloaded under a fresh authorized read.

---

### User Story 4 - Use the timeline with keyboard and screen reader (Priority: P2)

A member who cannot use a pointer, or who uses a screen reader, gets the same information from an accessible alternative to the graphic: a table-like listing per stream of match-time bins with their fact count or coverage state, operable by keyboard, including selecting a bin, changing the zoom level, moving the extent, and jumping to the fact list. The graphic itself is operable by keyboard with visible focus.

**Why this priority**: The Web UI's accessibility rules apply to every new view, and a graphic without a text alternative would exclude members.

**Independent Test**: Automated browser tests operate the timeline with the keyboard only, run accessibility rule checks on every timeline state, and compare the alternative listing with the graphic's data for equality.

**Acceptance Scenarios**:

1. **Given** the timeline, **When** a member uses only the keyboard, **Then** they can reach every lane, move between bins, select a bin or span, change the zoom level, move the extent, and jump to the fact list, with visible focus and announced state changes.
2. **Given** the timeline, **When** a member chooses the text alternative, **Then** every lane's bins are listed with their match-time range and fact count or coverage state, and the listing is consistent with the graphic for the same zoom level and extent.
3. **Given** any timeline state (loading, data, partial, in progress, incomplete, complete without facts, unavailable, error, connection required), **When** automated accessibility rule checks run, **Then** they report no violations.
4. **Given** a change of the data (refresh, zoom, extent), **When** it completes, **Then** assistive technology is told in a concise status message, without moving focus.

---

### User Story 5 - Keep the timeline online-only, memory-only, and scoped to access (Priority: P2)

The timeline obeys the review's online-only rule: it reads through the existing authorized member reads, keeps its data in memory only, and removes it when the member leaves the view, goes offline, signs out, or loses access.

**Why this priority**: These are the Web UI's existing quality and security rules applied to the new view; they protect access control and the no-local-data rule.

**Independent Test**: After a review session, browser storage and service-worker caches contain no timeline or fact data; an offline browser shows the connection-required state; a member whose role is revoked while the timeline is open loses the data at the next read.

**Acceptance Scenarios**:

1. **Given** a member who is not authorized for the owning team, **When** the member opens the review address, **Then** the existing not-found or not-permitted view is shown and no timeline is offered.
2. **Given** the browser is offline or goes offline, **When** the timeline is open or opened, **Then** the connection-required state replaces it and no earlier data is displayed.
3. **Given** the member leaves the review, signs out, or the platform refuses a read as forbidden or not found, **When** this happens, **Then** all loaded timeline data and the density derived from it are discarded, and returning requires fresh authorized reads.
4. **Given** the member viewed a timeline, **When** browser storage and service-worker caches are inspected, **Then** they contain no fact, density, or coverage data.

### Edge Cases

- A stream with zero indexed facts and no coverage windows: the lane says no coverage is reported, not "zero facts".
- A stream whose coverage windows extend beyond or fall short of the other streams' extent: the shared axis covers the union of all reported windows and facts, and every lane shows the parts it has no coverage for as "no reported coverage".
- No lane has any coverage window or fact (the extent cannot be determined): the timeline states that the match-time extent is unavailable and offers the existing capability list.
- Two nodes of the same capability: each has its own lane, identified by node key, and their data is never merged.
- A run that ends while the timeline is open: refreshing stops, and the lanes show the final state after the last refresh.
- The run's status refreshes while the member has a selection or has moved the extent: the selection, zoom level, and extent are kept, and changed data is announced.
- A continuation rejected by the platform while loading a lane's facts: the lane restarts its loading once, and if it fails again shows the unavailable state for that lane without showing partial density as complete.
- A coverage state or output type the Web UI does not know: shown with its raw value and treated as not analysed.
- Facts with identical or overlapping ranges, and facts that start before or end after the visible extent: counted in every overlapping bin of the visible extent only.
- Very large streams: loading is bounded, the density is marked partial, and the lane says how many facts it is based on (see FR-009).
- Two runs of the same match are never mixed: the timeline shows only the opened run.

## Requirements *(mandatory)*

### Functional Requirements

#### Timeline content

- **FR-001**: The analysis review of a run MUST offer a match-time timeline with one lane per fact stream of the run (identified by node key, capability id and version), over one shared match-time axis in the review's match-clock format, available to every member who may open the review.
- **FR-002**: Each lane MUST show the density of the stream's accepted facts per match-time bin at the selected zoom level, counting a fact in every bin its half-open match-time range overlaps and at most once per bin, together with the stream's reported total fact count.
- **FR-003**: The member MUST be able to select among a fixed set of zoom levels (bin sizes and visible extents) and to move the visible extent earlier and later; the zoom level and extent MUST persist while the member moves between the timeline and the fact list within the same review visit.
- **FR-004**: The timeline MUST show each stream's coverage windows with their state (`accepted`, `missing`, `failed`, `blocked`, `skipped`) on the lane, and MUST convey every state by text, pattern, or shape in addition to colour.
- **FR-005**: The timeline MUST show the run-pinned provenance of a lane's stream (workflow id and version, Analyst profile id and digest, image digest, model digest where present) in the lane's detail in the same shortened form with the full value available, as the review already does.

#### Explicit states (never inferred as zero)

- **FR-006**: The timeline MUST keep these bin and lane states distinct, each with its own worded label: facts present; analysed without facts (inside accepted coverage); facts beyond the loading bound not shown (a bin of a partial lane after the last loaded fact); partially covered; not analysed (inside missing, failed, blocked, or skipped coverage); no reported coverage; run in progress without facts yet; analysis complete without facts; capability not part of the run; unavailable; error; connection required. A count of zero MUST be shown only for a bin fully inside accepted coverage.
- **FR-007**: Values the Web UI does not know (coverage states, output types, run and node states) MUST be shown with their raw value and treated as not analysed, never hidden and never as zero.
- **FR-008**: When the match-time extent cannot be determined from any reported coverage window or fact, the timeline MUST state that explicitly and offer the existing capability list rather than draw an empty axis.
- **FR-009**: The timeline MUST bound how many facts it loads per stream for density, using the existing fact read page by page; when a stream has more indexed facts than were loaded, the lane MUST say how many of the reported facts the density is based on and mark it as partial, and MUST NOT present it as complete. The same bound MUST apply to the memory held for all lanes together.

#### Navigation to the fact list

- **FR-010**: The member MUST be able to select a bin or a span of bins on a lane, see its match-time range and its fact count or coverage state, and jump to the existing fact list of that stream limited to exactly that match-time range through the review's existing address (stream by node key and range), restarting paging at the first fact.
- **FR-011**: A jump into a range that is not analysed or has no facts MUST land on the fact list's existing explicit state for it, including the coverage of the range.
- **FR-012**: The timeline MUST NOT add filters other than match time, MUST NOT interpret fact payloads, and MUST NOT render capability-specific content.

#### Refresh, access, and data handling

- **FR-013**: While the run is requested or running, the timeline MUST refresh with the review's existing periodic status refresh, paused while the page is hidden, MUST stop when the run reaches a final state, and MUST keep the member's selection, zoom level, and extent across refreshes, announcing changed data without moving focus.
- **FR-014**: The timeline MUST be read-only and online only: it MUST use only the existing member reads of fact streams (`listAnalysisRunFactStreams`) and facts (`listAnalysisRunFacts`) and the run-pinned provenance they return, through the review's application-owned adapters over the generated client; it MUST NOT add or require any new platform operation or any other platform read.
- **FR-015**: Timeline data and everything derived from it MUST be held in memory only and MUST NOT be cached by the service worker or stored in browser storage; it MUST be discarded when the member leaves the review, goes offline, signs out, or the platform refuses a read as forbidden or not found, and a later visit MUST read afresh.
- **FR-016**: Every platform-provided text (node keys, capability ids, kinds, identifiers, failure reasons) MUST be rendered as text, never as markup, and the review's client security baseline MUST be kept.
- **FR-017**: The timeline MUST offer no run controls, media playback or clips, spatial visualizations (heatmaps, pitch views), comparison across runs, export, sharing, agent or MCP operations.

#### Accessibility and language

- **FR-018**: The timeline MUST provide a text alternative to the graphic that lists, for every lane, every bin at the current zoom level and extent with its match-time range and its fact count or coverage state, and the alternative MUST present the same data as the graphic.
- **FR-019**: All timeline controls (lane focus, bin movement, selection, zoom, extent, jump) MUST be operable by keyboard with visible focus, announce status changes to assistive technology, and pass the automated WCAG 2.2 AA rule checks in every timeline state.
- **FR-020**: All new user-facing text MUST come from translation keys with English as the only shipped language.

#### Tests

- **FR-021**: Automated tests MUST cover the timeline's derivation of density and coverage state per bin (including overlap counting, partial coverage, partial density, and unknown values), every explicit state of FR-006, zoom and extent movement, the jump to the fact list with its range, refresh while running, role-based access, keyboard operation and the text alternative, accessibility rule checks, and the online-only and memory-only rules, in the `simulated` browser project and in the `platform` browser project against the running platform that opens a run's review and uses the timeline.

### Key Entities *(include if feature involves data)*

- **Timeline**: The view of one run's fact streams over a shared match-time axis, with a zoom level, a visible extent, and at most one selection.
- **Lane**: One fact stream (node key, capability id and version) with its reported fact count, coverage windows, provenance, and bins.
- **Density Bin**: A match-time interval of the current zoom level in one lane, with a fact count or a distinct non-count state; derived in memory from loaded facts and coverage windows.
- **Coverage Window**: A match-time window with its state, as the platform reports it.
- **Timeline Selection**: A bin or span of bins on one lane, with its match-time range, used to jump to the fact list.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Coach goes from the opened run review to the fact list of a chosen time range in at most 3 interactions (select, "Show facts", and the review's existing open step), without typing a range.
- **SC-002**: In automated tests, for streams of at least three pages of facts, the density bins sum to the facts overlapping the visible extent (accounting for multi-bin facts) in 100% of scenarios, and a stream that exceeds the loading bound is marked partial in 100% of scenarios.
- **SC-003**: In automated tests, each explicit state of FR-006 is shown with its own label in 100% of its scenarios, and no bin outside accepted coverage is ever shown as a count of zero.
- **SC-004**: In automated tests, a jump from a selection shows exactly the facts overlapping the selected range and carries stream and range in the address in 100% of scenarios.
- **SC-005**: Automated accessibility rule checks report zero violations for every timeline state, every timeline action is reachable by keyboard, and the text alternative equals the graphic's data in 100% of scenarios.
- **SC-006**: While a run is in progress, a status change becomes visible in the open timeline within 15 seconds without a reload and without losing the member's selection, zoom level, or extent; this applies to the status and states (run state, counts, in progress), not to the full density of large lanes, which keep their previous data until the reload completes.
- **SC-007**: In automated tests, members without read access are refused in 100% of cases, and no timeline, fact, or coverage data is found in service-worker caches or browser storage after a review session or after access loss.

## Assumptions

- The timeline's data comes only from the two existing member reads; match-time density is derived in the browser from the facts it loads page by page, within the bound of FR-009, and the platform's reported fact count is the reference for "partial".
- The shared match-time extent is the union of all coverage windows and loaded facts the platform reports; the platform returns no separate match duration, and none is added.
- The set of zoom levels and the loading bound are planning decisions; they are development values and not production values.
- The timeline lives inside the existing review view (above the fact list); the review's address keeps carrying the run, the chosen stream, and the range, and the timeline's zoom level and extent need not be part of the address.
- The refresh interval follows the review's existing run refresh (10 seconds), paused while the page is hidden.
- Match time is shown as the review's match clock; platform values are milliseconds of match time.
- Out of scope: spatial visualizations such as heatmaps or pitch views (they need spatial-aggregation results that do not exist), capability-specific payload rendering, video playback (Evidence Clip Access), run comparison, export, sharing, agent or MCP operations, new API endpoints, and offline local data.
- Dependencies: the implemented [Web UI Connected Analysis Review](../20261017-181118-web-ui-analysis-review/spec.md) and [Accepted Fact Indexing and Capability-Scoped Fact Queries](../20261017-175648-fact-indexing-queries/spec.md); it depends on no unimplemented spec.
- Documentation: when merged, [Client Applications → Web UI](../../../docs/architecture/client-applications.md#web-ui) must no longer say the review offers no visualizations or that visualizations are not implemented, and must describe this timeline as the first implemented visualization; [Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access) must name the timeline as a consumer of the member fact reads.
