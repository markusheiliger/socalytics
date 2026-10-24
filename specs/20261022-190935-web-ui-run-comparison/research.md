# Research: Web UI Analysis Run Comparison

## R1. Existing reads cover the feature without API changes

**Decision**: Consume `getAnalysisRun`, `listMatchAnalysisRuns`,
`listAnalysisRunFactStreams`, the existing match/team reads, and
`getRecordingSetVersion` through application adapters. Do not call
`listAnalysisRunFacts` or fetch result objects for comparison.

**Rationale**: The committed OpenAPI document and
`src/platform/SocAlytics.Platform.Api/Analysis/AnalysisRunEndpoints.cs`
already provide run identity, team/match ownership, current/superseded fields,
workflow, and node input coverage. Stream summaries already provide all
required contracts, fact counts, output coverage, and provenance. The
recording lineage adapter already exposes `getSet(matchId, id)` with
finalization time and ordered recording/mapping digests.

**Alternatives considered**: A new comparison endpoint or count computation
from fact pages would duplicate existing control-plane reads and violate
FR-016. Downloading whole lineage is unnecessary for two exact recording sets.

## R2. Close application-model gaps, not platform-contract gaps

**Decision**: Extend `AnalysisRunStatus` and the `getRun` mapping to retain
`isCurrent`, `supersededBy`, `supersededAt`, and node `inputCoverage`.
Keep nullable/not-reported input coverage distinct from an explicitly empty
array. Comparison requires an explicit current flag and structurally valid
non-null supersession fields; missing required metadata is incompatible.
Keep the existing summary mapper's tested legacy tolerance for older list
fixtures; never use its default `isCurrent: false` as comparison truth.

**Rationale**: `src/clients/web/src/platform/adapters/analysisRunAdapter.ts`
currently drops these run-status fields, despite the API reporting them.
`AnalysisRunStatus` is distinct from `AnalysisRunSummary`; bolting summary
defaults onto the status read would invent lineage. Node input coverage
contains upstream identity, coverage mode, optional minimum fraction, and
accepted/missing/failed/blocked/skipped counts.

The stream adapter currently maps an omitted `items` collection to `[]`.
Comparison needs an explicitly empty list to be distinguishable from missing
metadata. Require a present array in the shared stream mapping, with
regression fixtures updated to the actual API shape. Validate compared counts
as nonnegative safe integers. An invalid count is incompatible, not zero.

**Alternatives considered**: Reading every page of match summaries to recover
status metadata adds requests, loses node input coverage, and inherits silent
defaults. A second comparison-only generated client would violate the existing
adapter boundary and duplicate mapping.

## R3. Unique capability/scope alignment is not equivalence

**Decision**: Index each side by the tuple `(capabilityId, executionScope)`.
Pair only a group with exactly one stream on each side. Otherwise emit every
stream in that group as an individually attributed unmatched entry. Use
`nodeKey` as within-run identity; reject duplicate node identities as
incompatible metadata. Preserve deterministic ordering using left stream
order, then right-only entries in their reported order.

**Rationale**: Different versions, node names, schemas, output types, and
provenance must be visible on an aligned pair. Including those fields in
alignment would hide contract changes; excluding ambiguity checks would guess
matches for repeated capabilities. Tuple encoding must be collision-safe,
for example nested maps rather than delimiter concatenation.

**Alternatives considered**: Node-only matching hides renamed nodes.
Capability-only matching conflates scopes. Semantic payload matching and
count-delta rankings exceed this scope.

## R4. Availability is independent of quantity

**Decision**: Derive descriptive availability per stream from its own
coverage. `accepted > 0` plus all other counts zero is complete reported
coverage; only then does `factCount === 0` describe complete zero-fact results.
Any missing/failed/blocked/skipped count makes coverage incomplete, even with
zero facts. No accepted units means no accepted results; zero expected units
is an explicit no-expected-units case, not a complete result. Show skipped
counts and explanations independently. A missing counterpart is not a stream.

**Rationale**: These distinctions implement FR-010 and avoid presenting failed
or cancelled work as successful zero analysis. Counts sum reported execution
units, not minutes, equal inputs, accuracy, or quality. Input coverage is a
different vector belonging to a node's pinned dependencies, not the stream's
output coverage.

**Alternatives considered**: A single empty/results boolean would conflate
all of these states. A positive fact count cannot establish complete coverage.

## R5. Revalidation is a display barrier, not background cache invalidation

**Decision**: Use a dedicated comparison load scope keyed by member,
match, ordered pair, and request generation. A fresh generation validates
match/team context and both run statuses before requesting streams and sets.
Only a fully successful current generation becomes visible. No previous-data
placeholder, persisted query cache, or stale successful pair is shown while
pending or on failure. Drop the completed pair on offline/access loss and
unmount; use zero query retention and remove comparison-scoped data. Guard
late responses even if the transport cannot abort.

**Rationale**: `SessionProvider` clears the global query cache on session end,
but a 404/403 needs comparison-specific cleanup. `ConnectivityProvider`
invalidates queries after probing `/alive`; that does not guarantee both
authorized reads finished before old cached results render. Existing
`AnalysisRunPage` disables focus refetch and retains query data, so copying
that page verbatim would not meet FR-012 through FR-015.

The comparison hook owns focus/visibility return and history-return triggers,
coalesces duplicate lifecycle signals, and immediately gates display. Normal
window focus is not a continuous authorization subscription. Refusals preserve
the existing concealed-404 behavior; the view cannot claim a run exists when
the platform conceals it.

**Alternatives considered**: Shared query invalidation alone can redisplay
cached results after reconnect. Polling terminal runs adds an unnecessary
workflow. Clearing all application caches on any comparison failure would
disrupt unrelated views.

## R6. History selection must not silently truncate

**Decision**: Page candidates using `AnalysisRunAdapter.listByMatch`.
Offer an explicit load-more action while continuation remains, and do not
claim insufficient total history until pages are exhausted. Preserve a
review-origin left ID through navigation and errors; direct pair URLs are
validated independently of which candidate pages are loaded. Do not silently
select defaults or replace an ID absent from the current page.

**Rationale**: `AnalysisRunStates.loadAllRuns` stops at twenty pages without
reporting truncation. Reusing it as a complete comparison eligibility index
would hide older terminal runs. Paged selection uses existing contracts and
does not require changing match/triage history behavior in this feature.

**Alternatives considered**: Removing a shared limit or fetching all history
would broaden the change and still make entry unbounded. Selecting the newest
two would violate explicit historical selection.

## R7. Evidence fits the installed verification environment

**Decision**: Use existing Vitest and Playwright tooling. Extend
`tests/e2e/support/simulatedApi.ts` and the existing
`analysisRunFixture`/`seedAnalysisRun`/`supersedeAnalysisRun` patterns for rich
comparison fixtures. Add simulated comparison and access specs. Extend
`tests/e2e/platform/run-supersession.spec.ts` to cancel its second run too,
then exercise match and review entry, both links, and current/superseded
metadata against real member operations.

**Rationale**: The existing platform supersession test already creates its
own match, uploads and finalizes twice, makes the first run terminal, and
cleans up active seasons. Making the second run terminal yields a reliable
eligible pair without depending on successful Analyst execution. The existing
platform analysis flow does not guarantee accepted fact streams. Deterministic
rich-result semantics belong in simulated fixtures; platform evidence proves
connected authorization, adapter wiring, identities, lineage, and navigation.
Do not claim a real-platform completed rich-result comparison from this.

Use `tests/e2e/support/axe.ts`, `keyboard.ts`, existing fixture guards and
shell-worker tests to cover accessibility and non-persistence. All required
tools already exist in `.github/actions/environment-setup/action.yml` and
`environment-verify/action.yml`; no environment change is needed.

**Alternatives considered**: A new scripted Manager protocol, test-only
endpoint, production provider, or new browser project would expand the scope
without being necessary for the stated connected navigation evidence.

## R8. Architecture is updated as design now and evidence at implementation

**Decision**: Add the comparison design to Client Applications as decided,
not yet implemented; name the terminal-history and member-result boundaries
in the owning job-processing and Analyst narratives. The implementation
documentation task must remove stale blanket exclusions of comparison,
update the platform evidence inventory, and preserve every unrelated deferral.

**Rationale**: The current client narrative explicitly says comparison is
unimplemented and that review offers no cross-run comparison. Leaving either
statement after delivery would violate constitution Principle I. No endpoint,
execution-state, retention, or production decision changes.

**Alternatives considered**: Updating only the new paragraph would leave
contradicting status statements elsewhere in the architecture.
