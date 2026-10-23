# Contract: What the Web UI Shows per Platform Answer

The platform operation is defined by
[the explicit request contract](../../20261023-015644-explicit-analysis-rerun/contracts/request-analysis-run.md) and is
not changed by this feature. This file is the Web UI's own mapping from answers to what the member sees; the unit tests
of `requestAnalysisOutcome.ts` enforce it. Texts are in `src/clients/web/src/i18n/en/lineage.json` under
`requestAnalysis.*`; all are plain language and never name a workflow or version before the answer.

## Visibility and state of the action

| Condition | Action |
| --------- | ------ |
| Club Admin of the club or Coach of the owning team, recordings known finalized | Shown, enabled |
| Same member, lineage loaded with no recording-set version | Shown, disabled, reason in text |
| Same member, lineage loading or unavailable | Shown, disabled |
| Request in flight | Shown, pending, not activatable |
| Viewer, Analyst, other team, other club | Not shown, nothing sent |

## Answers

| Platform answer | Outcome key | Member sees | Key afterwards | Follow-up |
| --------------- | ----------- | ----------- | -------------- | --------- |
| `202`, `created: true` | `created` | A new run was requested; it becomes the current run | dropped | Re-read runs and lineage; link to the run's review |
| `202`, `created: false`, no or same `currentRunId` | `exists` | No new run; this analysis already exists | dropped | Re-read; link to the run |
| `202`, `created: false`, different `currentRunId` | `existsOtherCurrent` | As `exists`, and another run is the current one | dropped | Re-read; links to both runs |
| `409 no-finalized-recording-set` | `noFinalizedSet` | Upload and finalize recordings first | dropped | Re-read lineage |
| `409 workflow-unresolvable`, category `not-configured` | `workflowNotConfigured` | No usable default workflow; a Club Admin must set it in the Registry administration | dropped | none |
| `409 workflow-unresolvable`, category `invalid-definition` | `workflowInvalid` | The default workflow's definition is not usable; a Club Admin must resolve it | dropped | none |
| `409 workflow-unresolvable`, other category | `workflowUnresolvable` | No usable default workflow; a Club Admin must resolve it (no invented detail) | dropped | none |
| `409 season-archived` | `seasonArchived` | Changes are no longer possible for an archived season | dropped | none |
| `409 idempotency-key-reused` | `keyReused` | The request could not be processed; start a new request | dropped | none |
| `403` | `forbidden` | Not allowed to request analysis for this match | dropped | Re-read session, match, runs |
| `404` | `notFound` | The match is no longer available | dropped | Re-read session, match, runs |
| `401` | (existing session handling) | Sign-in flow; no run data stays visible | dropped | Existing session-ended handling |
| `503 analysis-dependency-unavailable` | `temporary` | Temporary; repeat the request | kept (same confirmation) | Repeat sends the same key |
| No response at all (network failure) | `unreachable` | Requesting analysis needs a connection | kept (same confirmation) | Nothing queued; repeat sends the same key |
| `400` (missing or malformed key) or any other status | `generic` | The request failed, with the platform's reference when present | dropped | none |

## Rules

- On `temporary` and `unreachable` the confirmation dialog stays open showing the alert text with the confirm action
  re-enabled for the repeat (same key); closing the dialog drops the key.
- A refusal never changes the run list; only a `202` triggers the re-read (and `403`, `404`, `noFinalizedSet` re-read
  to reflect current access and data).
- The `202` outcome stays shown if the following re-read fails; the existing list unavailable state offers its retry; no
  second request is sent.
- Outcome and refusal texts are announced through `role="status"` (accepted) or `role="alert"` (refused) and are never
  conveyed by colour alone.
- Nothing about a request, outcome, or key is written to storage, the service-worker cache, the console, or analytics.
