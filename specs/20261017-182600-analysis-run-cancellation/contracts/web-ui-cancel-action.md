# Contract: Web UI Cancel Action

Applies to the match page's run list,
`src/clients/web/src/features/lineage/AnalysisRunStates.tsx`.

## Visibility

| Member | Run state | Shown |
| --- | --- | --- |
| Club Admin, or Coach of the owning team (`canEditTeamMatches`) | `Requested`, `Running` | Cancel button |
| Same | any other state | No button |
| Viewer of the owning team | any | No button |

The platform's answer stays authoritative; a refused request shows the refusal.

## Dialog (`CancelRunDialog`)

- Title and text from `lineage` translation keys: work in progress stops,
  results already accepted remain, the cancellation cannot be undone.
- Optional multiline reason field, at most 500 characters, with a counter; an
  over-long reason disables confirmation with an explanation.
- Buttons: dismiss (default focus) and confirm; `Escape` dismisses; focus
  returns to the cancel button after closing.
- While the request runs, confirmation is disabled and progress is announced.

## Adapter (`analysisRunAdapter.cancel`)

```ts
cancel(runId: string, version: number, reason?: string): Promise<AnalysisRunSummary>
```

Sends `If-Match: "<version>"` and `{ reason }` through the generated client
with the existing `withIfMatch` and `withJson` helpers of
`src/clients/web/src/platform/adapters/kiota.ts`; maps the
`AnalysisRunStatusDto` to `AnalysisRunSummary` (including `version`,
`cancelledBy`, `cancellationReason`); failures go through the existing
`PlatformFailure` mapping (for example `conflict`, `notPermitted`,
`validation`).

## Outcomes

| Platform answer | Web UI |
| --- | --- |
| `200` | Dialog closes; the run shows `Cancelled`, the account name, end time, and reason; status announced; run list refetched |
| `409`, `412` | Existing conflict message; run list refetched; the action stays only if the run is still unfinished |
| `403`, `404` | Existing not-permitted or not-found message; run list refetched |
| `400` | Reason field shows the validation message |
| Network failure | Existing failure alert with reload |

## Cancelled run display

`Cancelled · by <accountName> · <end time> · <reason>`, with the reason
rendered as text (never markup); runs without a reason omit that part.
