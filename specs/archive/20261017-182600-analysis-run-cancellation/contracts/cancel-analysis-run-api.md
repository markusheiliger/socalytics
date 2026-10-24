# Contract: Cancel Analysis Run (`/api/v1`)

Member operation in the platform OpenAPI document
(`src/platform/SocAlytics.Platform.Api/openapi/v1.json`), mapped with
`MapMemberApi()` (policy `ActiveMember`, `SessionAntiforgeryFilter`).

## Operation

`POST /api/v1/analysis/runs/{runId}/cancellation`

- **operationId**: `cancelAnalysisRun`
- **Tag**: `Analysis runs`
- **Headers**: `If-Match: "<version>"` (required; the run's `version` from
  `getAnalysisRun` or `listMatchAnalysisRuns`), the anti-forgery header of the
  session.
- **Body** (`application/json`, optional):

```json
{ "reason": "Wrong recordings were finalized." }
```

`reason`: string or null, at most 500 characters after trimming, no control
characters except line breaks.

## Responses

| Status | When | Body |
| --- | --- | --- |
| `200` | Cancelled now, or already cancelled with the current version or the version before the cancellation | `AnalysisRunStatusDto` of the run |
| `400` | Invalid reason | Problem details, field violation `reason` |
| `401` | No session | Problem details |
| `403` | Viewer of the owning team | Problem details |
| `404` | Run missing or member without a role on the owning team | Problem details |
| `409` | Run is `Completed`, `PartiallyCompleted`, or `Failed` | Problem details, code `analysis-run-terminal`, extension `state` |
| `412` | `If-Match` is not an accepted version | Problem details with the current version |
| `428` | `If-Match` missing | Problem details |

## Additive read fields

`AnalysisRunSummaryDto` (in `listMatchAnalysisRuns`) and
`AnalysisRunStatusDto` (in `getAnalysisRun` and this operation) gain:

```json
{
  "state": "Cancelled",
  "terminatedAt": "2026-10-17T18:40:00Z",
  "cancelledBy": { "memberId": "6d0b…", "accountName": "coach.anna" },
  "cancellationReason": "Wrong recordings were finalized."
}
```

`cancelledBy` and `cancellationReason` are null unless the run is
`Cancelled`. Node `state` may be `Cancelled`; `AnalysisJobCountsDto` gains
`cancelled`.

## Side effects (one transaction)

- Run → `Cancelled`; unfinished nodes and jobs → `Cancelled`; active attempts
  → `Cancelled` (`run-cancelled`).
- Pending or failed `analysis.ready-work` outbox rows of the run →
  `discarded`.
- One `analysis.run-state-changed` message
  ([contract](../../../../contracts/analysis/run-state-changed/v1/run-state-changed.schema.json),
  unchanged; new valid example `cancelled.json`), without the reason.
- One audit event `analysis.run-cancelled`.

## Effects on Manager operations (unchanged contracts)

After the commit, for jobs and attempts of the run:

| Manager call | Answer (existing problem codes) |
| --- | --- |
| Claim | `claim-obsolete` (job not `Ready`) |
| Heartbeat, result grants, upstream read, completion | `attempt-obsolete` (attempt not `Active`) |
