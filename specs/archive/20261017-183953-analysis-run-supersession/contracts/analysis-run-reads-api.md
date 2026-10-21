# Contract: Analysis Run Reads with Supersession (`/api/v1`)

Additive changes to the existing member reads in the platform OpenAPI document
(`src/platform/SocAlytics.Platform.Api/openapi/v1.json`). No new operation; the
authorization of both reads (`MapMemberApi()`, team-scoped read access, not
found for members without a role on the owning team) is unchanged.

## Affected operations

- `GET /api/v1/analysis/runs/{runId}` (`getAnalysisRun`) → `AnalysisRunStatusDto`
- `GET /api/v1/matches/{matchId}/analysis-runs` (`listMatchAnalysisRuns`) →
  `AnalysisRunSummaryPageDto` of `AnalysisRunSummaryDto`
- `POST /api/v1/analysis/runs/{runId}/cancellation` (`cancelAnalysisRun`, from
  the Analysis Run Cancellation feature) returns the same `AnalysisRunStatusDto`
  and therefore the same additive fields.

## Additive fields on `AnalysisRunSummaryDto` and `AnalysisRunStatusDto`

| Field | Type | Meaning |
| --- | --- | --- |
| `isCurrent` | `boolean` | The run is the match's current run (newest run of the latest finalized recording-set version that has a run) |
| `supersededBy` | object or `null` | The superseding run: `runId`, `recordingSetVersionId`, `recordingSetFinalizedAt` |
| `supersededAt` | date-time or `null` | When the run was superseded |
| `cancellationCause` | `"member"`, `"superseded"`, or `null` | Why a `Cancelled` run was cancelled; `null` for other states |

Example of a run cancelled by supersession:

```json
{
  "runId": "0192…a1",
  "state": "Cancelled",
  "terminatedAt": "2026-10-17T19:05:00Z",
  "cancellationCause": "superseded",
  "cancelledBy": null,
  "cancellationReason": null,
  "isCurrent": false,
  "supersededBy": {
    "runId": "0192…b7",
    "recordingSetVersionId": "0192…c3",
    "recordingSetFinalizedAt": "2026-10-17T19:04:58Z"
  },
  "supersededAt": "2026-10-17T19:05:00Z"
}
```

Example of a completed run superseded later (state and version unchanged):

```json
{
  "state": "Completed",
  "cancellationCause": null,
  "isCurrent": false,
  "supersededBy": { "runId": "0192…b7", "recordingSetVersionId": "0192…c3", "recordingSetFinalizedAt": "2026-10-17T19:04:58Z" },
  "supersededAt": "2026-10-17T19:05:00Z"
}
```

A member cancellation reports `cancellationCause: "member"` with the
cancellation feature's `cancelledBy` and `cancellationReason`; when such a run
is superseded later it also reports `supersededBy` and `supersededAt`.

## Unchanged contracts

- `contracts/analysis/run-state-changed/v1`: unchanged; a cancellation by
  supersession emits the same shape as the cancellation feature's
  `cancelled.json` example. Consumers learn the cause and the superseding run
  from the reads.
- Analyst Manager operations: unchanged; after a supersession, claims answer
  `claim-obsolete` and heartbeats, result grants, upstream reads, and
  completions answer `attempt-obsolete`, as for a member cancellation.
- `cancelAnalysisRun` for a run cancelled by supersession: the cancellation
  feature's idempotent answer (`200` for the current version or the version
  before the cancellation, otherwise `412`); the run keeps cause `superseded`.

## Reconciliation

The existing Club Admin reconciliation operation and the startup and
reconnect reconciliation also apply supersession; the audit event
`analysis.publication-reconciled` gains the detail `runsSuperseded`.
