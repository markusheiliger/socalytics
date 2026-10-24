# Contract: `requestAnalysisRun`

The authoritative machine-readable form is the committed OpenAPI snapshot, refreshed from the platform's
`/openapi/v1.json`; this file states what the operation must contain there.

## Operation

| Item | Value |
| ---- | ----- |
| Method and path | `POST /api/v1/matches/{matchId}/analysis-runs` |
| Operation id and tag | `requestAnalysisRun`, `Analysis runs` |
| Route group | `MapMemberApi()` (policy `ActiveMember`, `SessionAntiforgeryFilter`) |
| Authorization | `TeamPermission.Write` on the match's team through `IAccessAuthorizer` (Club Admin, Coach) |
| Required header | `Idempotency-Key`: 1 to 255 characters from `U+0021` to `U+007E` |
| Not applicable | `If-Match` (no versioned resource); request body (none; a body offers no choice) |

## Responses

| Status | When | Body |
| ------ | ---- | ---- |
| `202 Accepted` | A run was created or already exists | `AnalysisRunRequestAcceptedDto` |
| `400` | Missing or malformed `Idempotency-Key` | problem, violation on `Idempotency-Key` |
| `401` | Not authenticated | problem |
| `403` | Viewer of the owning team | problem |
| `404` | Unknown match, or member without a role on the owning team | problem |
| `409` | `season-archived`; `no-finalized-recording-set`; `workflow-unresolvable` (category in the problem extension); `idempotency-key-reused` | problem |
| `503` | `analysis-dependency-unavailable` (Registry or recording-set lineage read failed; retryable, nothing recorded) | problem |

`202` carries `Location: /api/v1/analysis/runs/{runId}` and the body below. The linked `getAnalysisRun` read already
supports conditional reads (`ETag`, `304`).

## `AnalysisRunRequestAcceptedDto`

```json
{
  "runId": "uuid",
  "created": true,
  "state": "Requested",
  "currentRunId": null,
  "recordingSetVersionId": "uuid",
  "workflowId": "string",
  "workflowVersion": "string",
  "statusLink": "/api/v1/analysis/runs/{runId}"
}
```

`currentRunId` is present and different from `runId` only when the returned run is not the match's current run (for
example, a superseded run returned after the default workflow was changed back). `state` is the run's state when the
answer is built; a replay reports the originally recorded `created` flag and run with the run's state read at replay
time.

## Behavioral guarantees

- One run per recording-set version, workflow, and workflow version; an existing run is returned in any state.
- A same-key replay by the same member for the same match returns the recorded answer (or the recorded `rejected`
  refusal) without re-evaluation and records no audit event; another member or match gets `409 idempotency-key-reused`.
- Receipts: message type `analysis-requested`, outcomes `run-created`, `run-exists`, `rejected` with categories
  `no-finalized-recording-set`, `not-configured`, `invalid-definition`; visible through
  `/api/v1/operations/analysis/notification-receipts` and counted in the operations summary like finalization receipts.
- Audit: one `analysis.run.request` event per successful request identity.
