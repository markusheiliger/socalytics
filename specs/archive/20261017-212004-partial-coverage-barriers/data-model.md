# Data Model: Partial-Coverage Match Barriers

## Coverage Policy (Domain value)

| Field | Type | Rules |
| --- | --- | --- |
| `Mode` | `complete` \| `partial` | Default `complete`. |
| `MinimumAcceptedFraction` | decimal or null | Only with `partial`; `0 < f <= 1`. |

`RequiredAccepted(expected) = max(1, ceil(f × expected))`, `1` when `f` is null. A partial policy is effective only on
an edge from a segment-scoped upstream node to a match-scoped dependent node (a *partial barrier*); elsewhere it is
evaluated as `complete`.

## Capability Declaration (existing, `analyst_capability_declaration.manifest`)

`upstream[i].coverage` (Analyst manifest 1.1.0, optional): `{ mode: "partial", minimumAcceptedFraction? }`.
Validation on import and seed: refused as `manifest-invalid` with the validation error `partial-coverage` when the capability's
`executionScope` is `segment`, the entry's `kind` is `optional`, or the fraction is out of range.

## Resolved Workflow Edge (Application) and Run Edge (Domain)

`ResolvedWorkflowEdge` and `RunEdgeState` gain the edge's `CoveragePolicy`, resolved from the dependent node's
declaration entry for the upstream node's capability id (complete when absent).

## `analysis_dependency_edges` (migration `NNNN_analysis_partial_coverage.sql`)

| Column | Type | Rules |
| --- | --- | --- |
| `coverage_mode` | `text NOT NULL DEFAULT 'complete'` | `CHECK (coverage_mode IN ('complete', 'partial'))` |
| `minimum_accepted_fraction` | `numeric NULL` | `CHECK (minimum_accepted_fraction IS NULL OR (coverage_mode = 'partial' AND minimum_accepted_fraction > 0 AND minimum_accepted_fraction <= 1))` |

Existing rows become `complete` with no fraction. The table keeps its aggregate-child triggers; no new table.

## `analysis_logical_jobs.failure_reason` (same migration)

The check gains `upstream-coverage-below-minimum` next to `attempts-exhausted` and `upstream-required-failed`.

## Run snapshot JSON (`analysis_run_snapshots.snapshot`)

Edges with a partial policy gain `"coverage": { "mode": "partial", "minimumAcceptedFraction": f | null }`; complete
edges are serialized as today, so their digests are unchanged.

## Input Snapshot Entry (`analysis_logical_job_inputs.inputs`, JSON)

| Field | Existing | Change |
| --- | --- | --- |
| `upstreamNodeKey`, `edgeKind`, `acceptedResultIds` | yes | unchanged |
| `status` | `accepted`, `absent`, `failed`, `not-applicable` | adds `partial` (partial barrier with at least one unit not accepted) |
| `coverageMode` | — | `complete` (default when missing) or `partial` |
| `minimumAcceptedFraction` | — | copied from the policy for partial barriers |
| `coverage` | — | partial barriers only: ordered `[{ segmentId, state }]`, state `accepted`, `failed`, or `blocked` |

Written once at readiness; never updated.

## State transitions

```text
match-scoped job behind a partial barrier
  Waiting --(all upstream segment jobs final AND accepted >= required)--> Ready (snapshot with coverage)
  Waiting --(all upstream segment jobs final AND accepted <  required)--> Blocked (upstream-coverage-below-minimum)
  Waiting --(otherwise)--> Waiting
dependent node: Waiting --(its job blocked by the barrier)--> Blocked (blocked_by = upstream node)
run outcome: unchanged policy; any Failed/Blocked node prevents Completed
```

## Upstream Read (Manager)

Per dependency page: `upstream.coverage` (`mode`, `minimumAcceptedFraction`) for every dependency (`complete` when no
policy); `coverage` entries from the snapshot for partial barriers, from today's mapping otherwise.

## Coverage Report (result manifest 1.1.0 `inputCoverage`)

`[{ capability { id, version }, schemaVersion, units: [{ segmentId | match: true, state }] }]`; required per partial
barrier and then equal to the snapshot coverage (set equality, no duplicates); optional for other dependencies, whose
reported `accepted` units must be exactly the units of the snapshot's accepted results; rejection reason
`result-coverage-mismatch`.

## Node Input Coverage (run status read)

`AnalysisNodeStatusView.InputCoverage`: null or `[{ upstreamNodeKey, coverageMode, minimumAcceptedFraction,
accepted, failed, blocked, skipped, missing }]`, summed over the node's jobs with input snapshots.
