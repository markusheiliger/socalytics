# Feature Specification: Web UI Connected Analysis Review

**Feature Branch**: `20261017-181118-web-ui-analysis-review`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Web UI Connected Analysis Review: coaches and viewers can see what analysis produced for a match. Today the Web UI's match page only lists analysis runs with their states (`src/clients/web/src/features/lineage/AnalysisRunStates.tsx`), and the API offers `getAnalysisRun` (run and node status with job counts) and `listMatchAnalysisRuns`; the batch's spec 1 adds member-facing, team-authorized fact reads with coverage and provenance. This feature adds a connected analysis review in the Web UI: from a match, an authorized member (Club Admin, or Coach or Viewer of the owning team) opens a run and sees its state, timestamps, failure reason, workflow, and each node's capability, tier, scope, state, blocked-by reason, and job counts; for each capability with accepted results the member browses its facts in match-time order with time ranges, confidence, a readable payload summary, and provenance (workflow, Analyst profile, image and model digests), filters by match-time range, pages through results, and sees explicit coverage per segment (accepted, missing, failed, blocked, skipped) and explicit empty, incomplete, unavailable, loading, and error states. Runs still in progress refresh their status. Everything goes through application-owned adapters over the generated Kiota client, follows the existing role navigation, i18n, accessibility, and PWA rules (online only; the service worker never caches analysis data). Out of scope: run cancellation and other run controls (spec 3), video playback and clips, tactical visualizations such as heatmaps or pitch diagrams, comparison across runs, agent conversations, offline mode, export, and new API operations beyond what spec 1 provides (unless the plan proves a small read is missing, in which case it belongs to this spec and its plan updates the OpenAPI snapshot). Depends on `specs/archive/20261017-175648-fact-indexing-queries` (batch sibling), Web UI Foundation, Web UI Match Workflow and Recording Upload, Installable Web UI with Service Worker Updates, Durable Analysis Workflow (all others merged, archived under specs/archive/). Implements docs/architecture/client-applications.md#web-ui ("team and match review, analytics … for coaches (analysis review … not implemented yet)") and the member read side of docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope."

## Clarifications

### Session 2026-10-17

- Q: Should the chosen capability and match-time range be part of the review's address, so a reload or shared link opens the same selection? → A: Yes: the run, the chosen capability (by its node key), and the range are in the address; the page position is not.
- Q: How should members move through long fact lists? → A: Page by page with next and previous; the continuations of pages already visited are kept in memory for the current selection only.
- Q: Should members be able to filter facts by kind in addition to the match-time range? → A: No: only the match-time range filters facts, because the platform's fact read has no kind filter and filtering a single page in the browser would misrepresent the stream.

## User Scenarios & Testing *(mandatory)*

The actors are the club members who may read a match: Club Admins, and Coaches and Viewers of the team that owns the match. The review is a connected Web UI view over platform reads that already exist or that [Accepted Fact Indexing and Capability-Scoped Fact Queries](../20261017-175648-fact-indexing-queries/spec.md) adds. It implements the analysis review of [Client Applications → Web UI](../../../docs/architecture/client-applications.md#web-ui) and the member read side of [Segment And Match Analysis Scope](../../../docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope), and follows [Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access).

### User Story 1 - Open a run and see how its analysis went (Priority: P1)

A Coach opens a match, sees its analysis runs as today, and opens one run. The run review shows the run's state, when it was requested, started, and ended, its failure reason if any, the workflow and its version, and one entry per capability node of the workflow with the capability and its version, tier, execution scope, state, the node that blocks it (if blocked), and how many of its jobs are waiting, ready, claimed, completed, failed, or blocked. While the run is still requested or running, the review refreshes its status by itself and stops refreshing once the run has ended.

**Why this priority**: It turns the match page's state line into an understandable account of what the analysis did and where it stopped; it needs only the existing run read and is useful on its own, also for runs that have no facts yet.

**Independent Test**: For a run of the development workflow in each run state, a Viewer of the team opens the run from the match and sees the run details and every node with its counts; while the run runs, the displayed state changes without a reload; a member without a role on the team sees the not-found or not-permitted view.

**Acceptance Scenarios**:

1. **Given** a match with analysis runs, **When** a member who may read the match views it, **Then** each run in the existing run list offers a way to open its review.
2. **Given** an opened run, **When** the review loads, **Then** it shows the run's state, request, start, and end times as available, its failure reason if any, its workflow id and version, and one entry per capability node with capability id and version, tier, execution scope, node state, blocking node (when blocked), and job counts by state.
3. **Given** a run that is requested or running, **When** the review stays open, **Then** the run and node status refresh periodically without a reload and stop refreshing once the run is completed, partially completed, failed, or cancelled.
4. **Given** a run that does not exist, belongs to another match, or that the member may not read, **When** the member opens its review address, **Then** the Web UI shows the not-found view (or the not-permitted view where the platform refuses with "forbidden") and discloses nothing about the run.
5. **Given** the platform cannot be reached or answers with an error, **When** the review loads or refreshes, **Then** the review shows an explicit, recoverable error state with a reload action and does not present previously loaded data as current.

---

### User Story 2 - Browse a capability's facts with coverage and provenance (Priority: P1)

From the run review, the Coach chooses one of the run's capabilities and browses its facts in match-time order. Each fact shows its match-time range, its kind, its confidence, and a readable summary of its payload, with the full payload available on request. The view shows where the results came from (workflow, Analyst profile, image digest, and model digest where present) and, as a separate list, the coverage of the match: which parts have accepted analysis and which are missing, failed, blocked, or skipped. The Coach pages through long fact lists and can limit the view to a match-time range, for example the second half.

**Why this priority**: This is the first time a member sees what the analysis found; it is the user value the batch's fact indexing exists for.

**Independent Test**: For a run with accepted facts of a capability spread over several pages and with mixed coverage, a Coach opens the capability, sees the first page in time order with coverage and provenance, moves through all pages and sees every fact exactly once, applies a match-time range and sees only overlapping facts with coverage limited to that range, and clears the range again.

**Acceptance Scenarios**:

1. **Given** an opened run, **When** the member views the run's capabilities, **Then** each capability node is listed with its capability id and version, schema version, output type, number of indexed facts, provenance, and coverage counts per state, as the platform reports them.
2. **Given** a capability with indexed facts, **When** the member opens it, **Then** the Web UI shows its facts in the order the platform returns them, each with its match-time range in a readable match-clock format, kind, confidence as a percentage, and a payload summary, plus the capability's provenance and its coverage windows with their states.
3. **Given** more facts than one page holds, **When** the member moves to the next page and back, **Then** the next facts are shown in the same order, the previous page shows the same facts as before, no fact is shown twice across pages, and the member can tell when the last page is reached.
4. **Given** a match-time range entered by the member, **When** the member applies it, **Then** only facts overlapping the range are shown, coverage is limited to the range, paging restarts at the first fact of the range, and an invalid range (end not after start, negative or malformed times) is explained before any request is sent.
5. **Given** a fact payload, **When** the member asks for its details, **Then** the full payload is shown exactly as returned, as plain text, without interpreting or executing any part of it.
6. **Given** provenance digests, **When** they are displayed, **Then** they are shown in a shortened, recognizable form with the full value available to read and copy.

---

### User Story 3 - Understand empty, incomplete, and unavailable analysis (Priority: P2)

A Viewer opens a run that is still running, one that failed part-way, and one whose capability has no accepted results yet. Instead of an empty list that reads as "nothing happened", the review says explicitly whether the run is still in progress, which parts of the match are covered, missing, failed, blocked, or skipped, and that no facts exist yet or for the chosen range. Facts accepted so far are shown while the run continues, and the coverage and counts update as the run's status refreshes.

**Why this priority**: The architecture requires that unavailable or incomplete analysis is reported explicitly rather than inferred; without this, members draw wrong conclusions from partial results. It refines Story 2's views.

**Independent Test**: For a running run, a partially completed run with a failed segment, a run whose capability has no facts, and a range without facts, the member sees distinct, worded states, and for the running run the counts and coverage change after the status refresh without a reload.

**Acceptance Scenarios**:

1. **Given** a capability with no accepted results yet in a running run, **When** the member opens it, **Then** the view states that the run is still in progress and that no facts are available yet, with its coverage, rather than an unexplained empty list.
2. **Given** a capability whose coverage includes failed, blocked, missing, or skipped windows, **When** the member views its facts, **Then** the view says that the facts are incomplete and lists those windows with their states and match-time ranges.
3. **Given** a capability with accepted coverage for the whole match but no facts (for example results accepted before fact indexing existed, or a capability that found nothing), **When** the member opens it, **Then** the view says that the analysis is complete and produced no facts.
4. **Given** a range without facts, **When** the member applies it, **Then** the view says that no facts overlap the range and still shows the coverage for it.
5. **Given** a running run, **When** its status refreshes, **Then** the capability list's fact counts and coverage summaries refresh with it, and an open fact list offers to reload rather than changing under the member's position.
6. **Given** the platform reports that the requested capability is not part of the run, **When** the member follows an outdated address, **Then** the view says so explicitly and offers the run's capability list.

---

### User Story 4 - Find and use the review safely in every supported setting (Priority: P3)

The review is reachable only for members who may read the match, uses the same navigation, language keys, and accessibility as the rest of the Web UI, works with keyboard and screen readers, and is online only: when the browser is offline or the installed app opens without a network, the review shows the connection-required state, and no analysis data is cached or stored in the browser.

**Why this priority**: These are the Web UI's existing quality rules applied to the new views; they protect access control, accessibility, and the PWA's no-local-data rule.

**Independent Test**: Automated browser tests navigate to the review by keyboard, run accessibility rule checks on every review state, check that the service worker serves no analysis response from its cache and that no analysis data appears in browser storage, and check that an offline browser shows the connection-required state.

**Acceptance Scenarios**:

1. **Given** a member who is a Club Admin, or a Coach or Viewer of the owning team, **When** the member navigates from the match, **Then** the review is reachable; for any other member the review is not offered and its address yields the not-found or not-permitted view.
2. **Given** any state of the review (loading, data, empty, incomplete, unavailable, error), **When** automated accessibility rule checks run, **Then** they report no violations, and all controls are operable by keyboard with visible focus and announced state changes.
3. **Given** the browser is offline, **When** the member opens or refreshes the review, **Then** the connection-required state is shown and no analysis data from earlier visits is displayed.
4. **Given** the member viewed analysis data, **When** browser storage and the service-worker caches are inspected, **Then** they contain no analysis responses.

### Edge Cases

- A node that the run's requested capabilities do not include is shown with the state the platform reports, and its fact stream shows zero facts with skipped coverage.
- A run that ends while the member reads its facts stops refreshing; coverage and counts show the final state after the last refresh.
- A continuation that the platform rejects (for example because the selection changed) restarts paging at the first page with an explanation instead of failing silently.
- A payload that is very large, deeply nested, or contains markup is summarized within a bounded length, and its full text is shown as text only.
- Facts with identical time ranges keep the platform's order.
- Very long matches (for example with extra time) display match-clock times beyond 90 minutes correctly.
- A Coach or Viewer whose team role is revoked while the review is open receives the not-found or not-permitted view at the next refresh or page request.
- A session that ends while the review is open follows the Web UI's existing signed-out handling.
- A run state, node state, tier, scope, or coverage state that the Web UI does not know is shown with its raw value instead of being hidden.
- Two runs of the same match are never mixed: each review shows only its own run's status, facts, and coverage.
- An address whose range is malformed or invalid shows the range explanation and no facts until the member corrects or clears it; an address whose node key is not part of the run shows the "capability not part of the run" state.

## Requirements *(mandatory)*

### Functional Requirements

#### Navigation and access

- **FR-001**: The match view MUST offer, for each analysis run it lists, a way to open that run's review; the review MUST be a distinct, addressable view of one run of one match, and its address MUST also carry the chosen fact stream (by node key) and match-time range, so a reload or shared link restores that selection (not the page position).
- **FR-002**: The review MUST be offered only to members who may read the match (Club Admins, and Coaches and Viewers of the owning team), consistent with the Web UI's role navigation; the platform's decision stays authoritative, and a refusal MUST show the not-found or not-permitted view without disclosing the run.
- **FR-003**: A run that does not exist, or whose match differs from the match in the review's address, MUST be shown as not found.

#### Run and node status

- **FR-004**: The review MUST show the run's state, request time, start time, end time, and failure reason where present, and the workflow id and version.
- **FR-005**: The review MUST list every capability node of the run with its node key, capability id and version, tier, execution scope, state, blocking node where the node is blocked, and its job counts by state (waiting, ready, claimed, completed, failed, blocked).
- **FR-006**: While the run is requested or running, the review MUST refresh the run and node status and the capability list periodically and only while the view is visible, and MUST stop once the run reaches a final state; refreshing MUST NOT reset the member's position in a fact list.

#### Fact streams and facts

- **FR-007**: The review MUST list the run's fact streams with, for each, the capability id and version, schema version, output type, number of indexed facts, provenance, and coverage counts per state, as the platform reports them.
- **FR-008**: For a chosen fact stream, the review MUST show its facts in the platform's order, each with its half-open match-time range in a readable match-clock format, kind, confidence as a percentage, and a bounded, readable summary of its payload; the full payload MUST be available on request as plain text exactly as returned.
- **FR-009**: The review MUST page through facts page by page with next and previous, using the platform's continuation and keeping the continuations of visited pages in memory for the current selection only; it MUST show each fact at most once across pages, indicate whether more facts exist, and restart at the first page whenever the selection (stream or range) changes or the platform rejects the continuation.
- **FR-010**: The member MUST be able to limit the facts to a match-time range and clear it again; the review MUST validate the range before requesting it (start not negative, end after start, well-formed times) and explain invalid input. The match-time range is the only fact filter; facts are not filtered by kind or any other field.
- **FR-011**: The review MUST show the stream's provenance (workflow id and version, Analyst profile id and digest, image digest, and model digest where present), displaying digests in a shortened form with the full value available to read and copy.
- **FR-012**: The review MUST show the stream's coverage windows for the current selection as a list of match-time windows with their states (`accepted`, `missing`, `failed`, `blocked`, `skipped`), so that the member can tell which parts of the match the facts cover.
- **FR-013**: The review MUST request facts with the stream's node key, capability id and version, and schema version as listed, so that a capability that appears in several nodes is never ambiguous.

#### Explicit states

- **FR-014**: The review MUST distinguish, with its own worded message, at least: loading; facts available; no facts yet because the run is still in progress; analysis incomplete (coverage contains missing, failed, blocked, or skipped windows); analysis complete without facts; no facts in the chosen range; capability not part of the run; not found or not permitted; connection required (offline); and error with a reload action.
- **FR-015**: Facts accepted so far MUST be shown while a run is still in progress, marked as incomplete, together with the run's state.
- **FR-016**: Values the Web UI does not know (states, tiers, scopes, output types) MUST be shown with their raw value rather than hidden or failing.

#### Platform access, client rules, and quality

- **FR-017**: The Web UI MUST reach the platform only through application-owned adapters over the generated client, using the run read (`getAnalysisRun`), the match run list (`listMatchAnalysisRuns`), and the fact reads added by [Accepted Fact Indexing and Capability-Scoped Fact Queries](../20261017-175648-fact-indexing-queries/spec.md); it MUST NOT add other platform operations unless the plan shows a small read is missing.
- **FR-018**: The review MUST be online only: analysis responses MUST NOT be cached by the service worker or stored in browser storage, and an offline browser or an installed app without network MUST show the connection-required state for the review.
- **FR-019**: All new user-facing text MUST come from translation keys with English as the only shipped language, and all new views MUST be operable by keyboard with visible focus, announce status changes to assistive technology, and pass automated WCAG 2.2 AA rule checks.
- **FR-020**: The review MUST render all platform-provided text (kinds, payloads, failure reasons, identifiers) as text, never as markup, and MUST keep the Web UI's existing client security baseline.
- **FR-021**: The review MUST offer no run controls (cancel, retry, rerun), no media playback, clips, or download, no visualizations beyond lists and textual coverage, no comparison across runs, and no export.
- **FR-022**: Automated tests MUST cover the adapters' mapping of run, stream, fact, coverage, and provenance data, every explicit state, paging and range filtering, refresh while running, role-based access, accessibility rule checks, the online-only rule, and a browser test against the running platform that opens a run's review from a match.

### Key Entities *(include if feature involves data)*

- **Run Review**: The view of one analysis run of one match: run status, node statuses, and fact streams.
- **Node Status**: One capability node of the run with its capability, tier, scope, state, blocking node, and job counts, as the platform reports it.
- **Fact Stream Summary**: One capability node's indexed facts as listed by the platform: capability, schema version, output type, fact count, provenance, and coverage counts.
- **Fact Page**: One page of a stream's facts for the current selection (stream and optional range), with the continuation, provenance, and coverage windows.
- **Fact**: One fact as shown: match-time range, kind, confidence, payload summary, and full payload.
- **Coverage Window**: A match-time window with its state (`accepted`, `missing`, `failed`, `blocked`, `skipped`).
- **Provenance**: Workflow id and version, Analyst profile id and digest, image digest, and model digest where present.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Coach goes from a team's match to the first page of a chosen capability's facts in at most 3 navigation steps after the match view.
- **SC-002**: In automated tests, paging through a stream of at least three pages shows 100% of its facts exactly once and in the platform's order.
- **SC-003**: In automated tests, each of the explicit states of FR-014 is shown with its own message in 100% of its scenarios, and no scenario with incomplete coverage presents an empty list without a coverage statement.
- **SC-004**: While a run is in progress, a status change becomes visible in the open review within 15 seconds without a reload.
- **SC-005**: Automated accessibility rule checks report zero violations for every review state, and every review action is reachable by keyboard.
- **SC-006**: In automated tests, members without read access to the match are refused the review in 100% of cases, and no analysis response is found in service-worker caches or browser storage after a review session.

## Assumptions

- The review uses the platform reads as they exist: the run read and the match run list, and the run fact-stream listing and capability fact read that the batch sibling adds; no new platform operation is expected.
- Fact pages use the platform's default page size; the member pages forward and back through visited pages and can restart at the first page. Jumping to arbitrary pages is not offered because the platform's continuation is forward-only.
- The refresh interval for runs in progress follows the match view's existing run refresh (10 seconds), paused while the page is hidden.
- Match time is shown as a match clock (minutes and seconds from the start of the match timeline) and entered in the same format; the platform's values are milliseconds of match time.
- A payload summary shows the top-level fields of the payload with bounded lengths; nested values are summarized by their size. The capability-specific meaning of payload fields is not interpreted, because payload schemas are capability-owned and not validated by the platform.
- This feature supersedes the earlier rule that the match view shows no analysis results (Web UI Match Workflow and Recording Upload, FR-023) only for the review; the match view's run list stays as it is apart from the link to each run's review.
- Out of scope: run cancellation and other run controls (the batch's next feature, Analysis Run Cancellation, adds the cancel action), video playback and clips, heatmaps, pitch diagrams, and other tactical visualizations, comparison across runs, agent conversations, offline mode, export, and new platform operations.
- Dependencies: [Accepted Fact Indexing and Capability-Scoped Fact Queries](../20261017-175648-fact-indexing-queries/spec.md) (batch sibling, not yet merged), and the merged [Web UI Foundation](../20261012-150657-web-ui-foundation/spec.md), [Web UI Match Workflow and Recording Upload](../20261013-112844-web-ui-match-upload/spec.md), [Installable Web UI with Service Worker Updates](../20261015-194113-web-ui-pwa-updates/spec.md), and [Durable Analysis Workflow](../20261005-130703-durable-analysis-workflow/spec.md). The Web UI environment ([Web UI Verification Environment](../20261012-145505-web-ui-environment/spec.md)) already provides Node.js, Kiota, and Playwright; no new framework, SDK, or tool is expected.
