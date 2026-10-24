# Existing Member Read Integration

This is a client-consumption contract, not a new HTTP or schema contract.
The authoritative wire shapes remain the committed OpenAPI snapshot at
`src/platform/SocAlytics.Platform.Api/openapi/v1.json`. Regenerate Kiota
through the existing build; never hand-edit generated models.

## Operations and Owning Adapters

| Existing operation/path | Application adapter | Use |
| --- | --- | --- |
| `GET /api/v1/matches/{matchId}` | `matchAdapter.get` | Fresh match identity and owning team. |
| `GET /api/v1/teams/{teamId}` | `teamAdapter.get` | Fresh owning-team context through existing member authorization. |
| `listMatchAnalysisRuns`: `GET /api/v1/matches/{matchId}/analysis-runs` | `analysisRunAdapter.listByMatch` | Paged selection history only. |
| `getAnalysisRun`: `GET /api/v1/analysis/runs/{runId}` | `analysisRunAdapter.getRun` | Both authoritative run statuses, lineage flags and node input coverage. |
| `listAnalysisRunFactStreams`: `GET /api/v1/analysis/runs/{runId}/fact-streams` | `analysisFactAdapter.listStreams` | Whole-run counts, output coverage, contracts and provenance. |
| `GET /api/v1/matches/{matchId}/recording-set-versions/{recordingSetVersionId}` | `recordingLineageAdapter.getSet` | Exact pinned recording-set identity, finalization and member lineage. |

Use existing session, connectivity, minimum-version, and problem middleware.
Comparison itself issues no mutation and obtains no grants or media URLs.
Do not call fact-page, raw-result, registry-current-profile, or object-storage
operations to reconstruct counts or pinned provenance.

## Load Order and Identity Validation

1. Validate route/query selection syntax locally, then freshly read the
   selected match and its owning team. Existing `useMatchContext` is useful
   for entry visibility, but its cached data cannot satisfy the comparison
   freshness barrier.
2. Read both selected statuses in parallel. Validate returned run IDs against
   requested IDs, match/team against the fresh context, and state against
   the explicit terminal allowlist. Do not follow `supersededBy` and silently
   substitute its run.
3. For a valid pair read both stream lists and each distinct exact recording
   set in parallel. A shared recording-set ID may share one in-generation
   request. Verify returned set ID/match/team and stream-to-node/workflow
   identity consistency.
4. Publish ready data only after all required reads and validation succeed
   for the active member/pair/generation. Handle failure through typed
   `PlatformFailure` or existing not-found outcomes, not empty substitutes.

The reads are not a cross-request transaction. Terminal results are immutable,
but current/superseded labels are observations made during this load.
Do not promise an atomic two-run snapshot. Manual refresh and return reload
the observations and keep selected identities fixed.

## Adapter Mapping Requirements

`AnalysisRunStatus` gains required explicit `isCurrent` and nullable successor
metadata. Null successor is valid; incomplete non-null successor is not.
Map `nodes[].inputCoverage` with upstream node identity, policy, optional
minimum fraction, and all five counts. Preserve null/absent reporting status
without synthesizing zeros. Unknown raw policies/states are not silently
interpreted as supported values.

Require an actual stream `items` array; `[]` is valid, missing/null is
incompatible. Required identity, contract and provenance fields must exist.
Counts must be nonnegative safe integers. Positive facts without any accepted
coverage are incompatible. Missing optional model digest stays absent and
gets an explicit not-reported label. Contradicting node/workflow attribution
is not repaired by guessing.

Retain the existing match summary mapper's tested compatibility behavior for
legacy fields; comparison truth comes from the stricter run-status mapping.
Update run-status and stream fixtures to the committed contract and preserve
existing review tests for genuine empty, partial, and unavailable analysis.

## Error and Cache Contract

- Existing `getRun`/`listStreams` concealed 404 outcomes remain not found.
  Recording-set or context read failures also prevent ready state.
- A disclosed 403 clears both sides and displays not permitted. A 401
  triggers existing session-end behavior and clears all session data.
- Incompatible fields render an incompatible state, never zero/empty.
  Network/server failures use existing failure wording and explicit retry.
- Comparison requests use unconditional reads; do not send an ETag without a
  separately authorized current-generation body. No ETag cache is introduced.
- Dedicated comparison query keys include member, match, ordered pair and
  generation; no previous-data placeholder. Retire the generation and remove
  completed/pending comparison state on access loss, offline, pair change,
  or unmount. Transport cancellation is helpful but generation checks remain
  necessary when a response arrives late.
- No comparison result populates shared run/stream/lineage caches. A stale
  successful pair cannot reappear on window return or reconnect before both
  sides are authorized again.
