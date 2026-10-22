# Feature Specification: Web UI Analysis Run Comparison

**Feature Branch**: No branch created; specification authored on the existing checkout.

**Created**: 2026-10-22

**Status**: Draft

**Input**: User description: "Let Club Admins and authorized Coaches and Viewers compare two terminal analysis runs of the same match, including a current run and its superseded history, without manually reconciling separate review pages. Add a connected, read-only comparison reached from the match or run review, using existing member run, fact-stream, coverage, provenance, and recording-lineage reads through application-owned adapters. Keep contract and input differences, missing results, incomplete coverage, and genuine zero-fact results distinguishable. Online only, in-memory data, no new comparison endpoints, semantic matching, quality scoring, run controls, media changes, export, sharing, agents, or MCP operations."

## User Scenarios & Testing *(mandatory)*

The actors are Club Admins and Coaches and Viewers authorized to read the
match's owning team. Comparison describes two runs' reported results and
lineage, not football performance or analysis quality. Terminal means
`Completed`, `PartiallyCompleted`, `Failed`, or `Cancelled`; current and
superseded are separate lineage attributes, not execution states.

### User Story 1 - Compare two finished runs from a match (Priority: P1)

A Coach opens a match or an existing run review and chooses two different
terminal runs of that match. One connected view identifies both runs, their
recording sets and workflows, and their fact streams. Either run can be current
or superseded. Each side keeps a link to its existing detailed review.

**Why this priority**: It removes the need to reconcile two review pages while
preserving each result's historical context.

**Independent Test**: From a match with a completed current run and a
superseded terminal run, select both and verify their identities, stream
summaries, and detailed-review links without leaving the comparison.

**Acceptance Scenarios**:

1. **Given** at least two readable terminal runs of a match, **When** a Club Admin, authorized Coach, or authorized Viewer starts comparison from the match, **Then** they can select two distinct eligible runs and open their comparison.
2. **Given** the member is reviewing a terminal run, **When** they start comparison, **Then** that run is retained as one side and they choose another terminal run of the same match.
3. **Given** two eligible selected runs, **When** comparison loads, **Then** both sides show run identity, execution state, current or superseded status, superseding run when reported, recording-set identity and available finalization context, workflow identity and version, and their stream summaries.
4. **Given** a comparison, **When** the member follows either detailed-review link, **Then** the existing review opens for exactly that side's match and run without changing the other run's attribution.
5. **Given** fewer than two eligible runs, or an in-progress run review, **When** the member reaches the comparison entry, **Then** the view explains eligibility and does not silently choose an in-progress run, create a run, or replace a selected historical run with the current run.

---

### User Story 2 - Understand differences without false equivalence (Priority: P1)

A Viewer sees the streams offered by both runs, their facts counted
independently, coverage counts, and pinned Analyst provenance. Streams absent
from one workflow remain visible. Changed contracts, recording sets, or
provenance are explicit; a zero count never conceals missing analysis.

**Why this priority**: Side-by-side counts are useful only when members can
tell what was analyzed, under which contract, and how complete the results are.

**Independent Test**: Compare fixtures with matching and unmatched streams,
changed contracts and recording sets, incomplete coverage, no accepted
results, and accepted zero-fact results. Verify distinct labels and the
original counts and provenance on each side.

**Acceptance Scenarios**:

1. **Given** streams in either run, **When** comparison loads, **Then** every stream is represented with its node identity, capability identity and version, output schema version and type, execution scope, fact count, coverage counts, and pinned profile identity and digest, image digest, and model digest where present.
2. **Given** one unique stream per side for the same capability identity and execution scope, **When** the streams are aligned, **Then** both remain independently attributed and differences in capability version, schema version, output type, node identity, or provenance are explicitly identified rather than treated as equivalent contracts.
3. **Given** a stream exists on only one side, or multiple streams make alignment ambiguous, **When** comparison renders, **Then** all such streams remain visible as unmatched with an explanation; an absent counterpart is not shown as a zero-fact stream.
4. **Given** different recording sets, workflows, or reported input coverage, **When** the member compares the runs, **Then** those differences are identified and no equal-input claim is inferred from equal counts or a shared match.
5. **Given** streams with no accepted results, accepted results with zero facts, or accepted results alongside missing, failed, blocked, or skipped coverage, **When** comparison renders, **Then** each state is distinguishable, partial results retain an incomplete-coverage explanation, and zero facts alone never means complete analysis.
6. **Given** any difference in counts or coverage, **When** it is displayed, **Then** wording describes reported quantities and limitations without calling either run better, more accurate, or an improvement in football performance or analysis quality.

---

### User Story 3 - Keep comparison authorized and explicitly connected (Priority: P1)

A member can trust that each side is currently readable and belongs to the
selected match. Loading, refusals, lost access, lack of connectivity, and
failures are explicit rather than looking like empty analysis.

**Why this priority**: Historical results remain protected by current
authorization, and a failed read must not become a misleading comparison.

**Independent Test**: Open comparison as each permitted role and an unrelated
member; attempt cross-match and duplicate selections; revoke team access
while open; simulate offline operation and one-side failures.

**Acceptance Scenarios**:

1. **Given** a selection containing a run from another match, an unreadable run, the same run twice, or a nonterminal run, **When** comparison is opened directly or from navigation, **Then** it refuses the pair with an appropriate explicit state and does not disclose unauthorized run details.
2. **Given** one side is still loading or its read fails, **When** the view renders, **Then** that side is explicitly loading, unavailable, or in error, not empty or zero; no completed comparison is claimed and retry is available for recoverable failures.
3. **Given** either side loses access or the session ends, **When** the next authorized read detects this, **Then** comparison details are removed from display and reusable in-memory comparison state, and the existing not-found, forbidden, or signed-out handling is followed without revealing whether a concealed run exists.
4. **Given** a comparison was previously loaded, **When** the browser goes offline, **Then** it shows a connection-required state rather than the earlier results; reconnecting requires successful authorized reads before comparison is shown again.
5. **Given** an older terminal run becomes superseded while comparison is open, **When** the member refreshes or returns to the comparison, **Then** current and superseded labels are re-read without replacing either selected run or changing its accepted historical results.

---

### User Story 4 - Use an accessible comparison without storing analysis (Priority: P2)

The member uses the comparison with the Web UI's existing language,
navigation, keyboard, and assistive-technology conventions, without retaining
analysis data for offline use.

**Why this priority**: The connected comparison must preserve the existing
Web UI's accessibility and no-local-analysis-data rules.

**Independent Test**: Complete selection and detailed-review navigation by
keyboard, inspect accessible labels in all states, and inspect browser
storage after viewing and leaving comparison.

**Acceptance Scenarios**:

1. **Given** selection, populated comparison, unmatched, empty, incomplete, loading, unavailable, access-loss, offline, or error states, **When** accessibility checks and keyboard navigation are exercised, **Then** controls have accessible names and visible focus, state changes are announced, side attribution does not depend on color, and automated WCAG 2.2 AA checks report no violations.
2. **Given** comparison has been used, **When** browser storage and service-worker caches are inspected, **Then** they contain no comparison responses, analysis results, coverage, or provenance.

### Edge Cases

- Two terminal runs use the same recording-set version: both remain eligible,
  and neither is called superseded unless the platform reports it.
- The current run is still in progress: two older terminal runs can still be
  compared; current status does not override eligibility.
- A failed or cancelled run has accepted results from before termination:
  retain those results' summaries and distinguish their incomplete coverage.
- Both workflows have no streams, or one has none: explain that explicitly;
  do not omit either run's identity or treat missing metadata as an empty list.
- A workflow repeats a capability in the same execution scope: preserve every
  node and refuse speculative one-to-one alignment.
- Provenance has no model digest: show that no model digest is reported, not
  an invented digest or an unexplained blank. An otherwise required metadata
  field is missing: show unavailable or incompatible metadata, not a default.
- Equal contracts, counts, or recording sets do not establish equal full input
  snapshots, equivalent outputs, or equal quality.
- A newer selection completes loading before an older request: no data from
  the abandoned pair can appear under the new pair's headings.
- A read returns an unfamiliar state or contract: preserve the reported
  identity and make unsupported information explicit; an unknown run state
  is not assumed terminal.
- Long identifiers and digests remain fully inspectable without breaking
  side attribution or keyboard use.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The Web UI MUST offer a connected, read-only comparison from the match and from a terminal run's existing review to Club Admins and authorized Coaches and Viewers of the owning team.
- **FR-002**: Comparison MUST require exactly two distinct runs of the selected match, each in `Completed`, `PartiallyCompleted`, `Failed`, or `Cancelled`; current and superseded terminal runs MUST be selectable without favoring or hiding history. Invalid pairs MUST be refused, including direct navigation.
- **FR-003**: Starting from a terminal run review MUST preserve that run as one side. Eligibility and insufficient eligible runs MUST be explained; the workflow MUST NOT silently substitute runs.
- **FR-004**: Each side MUST identify its run, execution state, platform-reported current and superseded status and superseding run where available, recording-set version and available finalization context, and workflow identity and version. Supersession MUST NOT be presented as a change to terminal execution state or accepted results.
- **FR-005**: Each side MUST retain a link to the existing detailed review for that exact run and match; detailed fact inspection remains in that review.
- **FR-006**: Comparison MUST account for every stream reported for either run, including streams with no accepted results. Each entry MUST show node identity, capability identity and version, output schema version and type, execution scope, fact count, and separate accepted, missing, failed, blocked, and skipped coverage counts as reported.
- **FR-007**: Each stream MUST show its run-pinned Analyst profile identity and digest, image digest, and model digest where reported; absence of model provenance MUST be explicit, and full identifiers and digests MUST be inspectable.
- **FR-008**: Streams MUST align only where capability identity and execution scope identify a unique stream on each side. Missing or ambiguous counterparts MUST remain unmatched and visible, never fabricated as zero-valued counterparts. Node, capability-version, schema, and output-type differences MUST remain explicit even on aligned rows; alignment MUST NOT claim semantic equivalence.
- **FR-009**: Comparison MUST explicitly identify differences in recording-set identity, workflow, stream contracts, pinned provenance, and input coverage reported by existing reads. Information those reads do not establish MUST be described as unverified or unavailable rather than inferring equal inputs.
- **FR-010**: No accepted results, accepted zero-fact results, incomplete coverage, skipped analysis, and a stream absent from the other workflow MUST have distinguishable presentations. A complete zero-fact result MUST require accepted coverage with no missing, failed, blocked, or skipped expected units; zero expected or accepted units MUST NOT by themselves establish a genuine complete zero-fact result.
- **FR-011**: Counts MUST be presented per run as descriptive quantities, with coverage context. The view MUST NOT label count differences, coverage differences, newer runs, or changed provenance as improvements in football performance, accuracy, or analysis quality.
- **FR-012**: Every load, refresh, retry, and return to comparison MUST validate both run identities, match ownership, eligibility, and current member access using the existing authorized reads. The view MUST NOT treat a previously readable historical run as permanently authorized. A detected access loss or ended session MUST hide comparison details and clear reusable comparison data using existing refusal and session conventions.
- **FR-013**: The view MUST distinguish loading, unavailable or not found, access loss or forbidden where disclosed by the platform, offline, incompatible metadata, and recoverable error states. Failed or missing reads MUST NOT become zero counts, absent streams, or successful comparisons. A partial load MAY identify which side is pending or failed but MUST NOT claim a completed comparison.
- **FR-014**: Selected run identities MUST remain fixed until the member changes them. Refresh and return to comparison MUST re-read current and superseded metadata; outdated responses from an earlier selection MUST NOT populate a later pair.
- **FR-015**: Comparison MUST operate online only, retain analysis data only in memory, and never persist or service-worker-cache comparison responses, analysis results, coverage, or provenance. Offline operation MUST hide previously loaded comparison data; reconnection MUST revalidate both sides before displaying it.
- **FR-016**: Comparison MUST consume only existing member run, fact-stream, coverage, provenance, and recording-lineage reads through application-owned adapters. It MUST NOT introduce comparison API endpoints, fetch raw result objects, or derive comparison counts by downloading and reconciling fact payloads.
- **FR-017**: Comparison MUST follow existing role navigation, translation-key conventions with English as the shipped language, and accessibility conventions; all controls MUST be keyboard operable, both sides and state changes MUST be accessible without color alone, and automated WCAG 2.2 AA checks MUST pass.
- **FR-018**: Automated evidence MUST cover stream alignment and ambiguity, every result-availability distinction, contract and input differences, role and team boundaries, invalid pairs, selection races, historical/current metadata refresh, offline and access-loss behavior, no persistent analysis data, and accessibility. Connected browser evidence MUST exercise entry and detail links in both simulated and real-platform verification settings using the existing prerequisites.
- **FR-019**: Delivery documentation MUST correct relevant implementation-status statements throughout `docs/architecture/`, including the three architecture sections named under Dependencies and Architecture Scope, so comparison is described as implemented only when backed by development evidence. Deferred visualizations, evaluation, production values, and production readiness MUST remain explicitly deferred.
- **FR-020**: Scope MUST exclude semantic payload or event matching, cross-match comparison, tactical charts, evaluation or quality scoring, run creation or rerun controls, media playback changes, export, sharing, agent or MCP operations, offline storage, and new comparison endpoints. Existing unrelated review and match actions MUST retain their behavior.

### Key Entities

- **Comparison Selection**: Two distinct terminal run identities and their
  shared match, with independently authorized left and right sides.
- **Run Context**: Run state, current and superseded metadata, recording-set
  lineage, workflow, and link to the run's detailed review.
- **Stream Comparison Entry**: One uniquely aligned pair of streams or one
  unmatched stream, preserving node, capability, contract, scope, fact count,
  coverage counts, and result-availability distinctions on each side.
- **Pinned Provenance**: The workflow, Analyst profile identity and digest,
  image digest, and optional model digest belonging to one run's stream.
- **Comparison Availability**: Explicit per-side loading and failure
  information plus the pair's eligibility, authorization, and connection state;
  never a substitute for analysis results.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For each permitted role, a member can reach comparison from the match or a terminal run review and identify both runs' state, recording set, and workflow in one view without opening a second review page.
- **SC-002**: In the acceptance fixtures, 100% of streams reported by either run appear exactly once per side, with counts, coverage, contracts, and provenance attributed to the correct run; ambiguous alignment produces zero guessed pairings.
- **SC-003**: All acceptance fixtures for unmatched streams, changed contracts or inputs, no accepted results, incomplete coverage, skipped analysis, and genuine complete zero-fact results have distinguishable explanations, with zero cases that substitute missing information with a successful zero result.
- **SC-004**: All tested cross-match, duplicate, nonterminal, unknown-state, and unauthorized selections are refused; after detected access loss, session end, or offline transition, zero comparison result details remain displayed.
- **SC-005**: Both detailed-review links open the correct run in every connected acceptance scenario, including superseded history and same-recording-set runs.
- **SC-006**: All comparison controls are operable by keyboard and all tested states pass automated WCAG 2.2 AA checks; inspection after comparison finds zero persisted analysis responses or result metadata.
- **SC-007**: Every comparison fixture presents fact and coverage quantities without a performance or quality ranking, including cases in which the newer run has more facts, fewer facts, or less coverage.

## Assumptions

- This is a whole-run metadata comparison, not a fact browser, range-filtered
  comparison, or event reconciliation. Members use each side's existing review
  for detailed facts, ranges, coverage windows, and payloads.
- A deterministic unique capability-and-scope alignment is a navigation aid,
  not a claim that workflows, contracts, or inputs are equivalent. Repeated
  capabilities with ambiguous alignment remain unmatched in this first scope.
- Authorization follows the existing platform reads and disclosure rules.
  Access loss becomes known on the next read, not through a new continuous
  authorization channel; returning to the view and retrying trigger fresh reads.
- Terminal accepted results remain historical, while current and superseded
  labels can change. Refresh does not require a run execution-state polling
  workflow or new subscriptions.
- Optional provenance absent from the platform is reported honestly. The
  comparison does not attempt to reconstruct unavailable full input snapshots
  or invent additional contract identities.
- Development evidence uses the existing simulated Analyst and Web UI
  verification setup; this feature adopts no production profile or readiness
  claim and requires no new verification tooling.

### Dependencies and Architecture Scope

- [Web UI Connected Analysis Review](../archive/20261017-181118-web-ui-analysis-review/spec.md): existing run reviews, stream summaries, coverage, provenance, and application-owned read adapters.
- [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md): authorized stream identities, indexed fact counts, coverage, and run-pinned provenance.
- [Analysis Run Supersession](../archive/20261017-183953-analysis-run-supersession/spec.md): immutable readable history and authoritative current and superseded metadata.
- [Web UI Match Workflow and Recording Upload](../archive/20261013-112844-web-ui-match-upload/spec.md): match navigation, recording-set context, and lineage reads.
- [Web UI Foundation](../archive/20261012-150657-web-ui-foundation/spec.md), [Web UI Verification Environment](../archive/20261012-145505-web-ui-environment/spec.md), and [Installable Web UI with Service Worker Updates](../archive/20261015-194113-web-ui-pwa-updates/spec.md): implemented application, accessibility, adapter, online-only, and verification prerequisites; their implemented transitive prerequisites remain in force.
- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): implements the comparison portion of connected analysis review, leaving visualizations and other deferred workflows out of scope.
- [Job Processing → Run State And Recovery](../../docs/architecture/job-processing.md#run-state-and-recovery): consumes terminal runs and immutable accepted historical results without altering execution or supersession.
- [Analysts, Models, and Hardware → Capability-Scoped Result Access](../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access): consumes currently authorized member result metadata without crossing run boundaries or exposing storage layout.
