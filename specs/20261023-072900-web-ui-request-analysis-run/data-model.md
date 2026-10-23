# Data Model: Web UI Request of a New Analysis Run

No persisted data and no new platform entity. Everything below lives in page memory and disappears with the page
(FR-015).

## Analysis run request (in memory)

| Field | Type | Notes |
| ----- | ---- | ----- |
| `matchId` | string (UUID) | The match whose page is shown |
| `idempotencyKey` | string | `crypto.randomUUID()` per confirmation, 36 characters of `U+0021..U+007E`; never shown or stored |
| `status` | `idle` \| `confirming` \| `pending` \| `accepted` \| `refused` | Not persisted |

Transitions:

```text
idle -> confirming              (member chooses the action)
confirming -> idle              (cancel or dismiss; nothing sent, key dropped)
confirming -> pending           (confirm; key created on the first send)
pending -> accepted             (202: created or exists; key dropped)
pending -> refused              (definite refusal; key dropped)
pending -> confirming           (network failure or 503; key kept, the member may repeat; closing drops it)
accepted | refused -> idle      (member closes the explanation)
```

## Request outcome (in memory)

`AnalysisRunRequestOutcome` (platform model `recordings.ts`), from the `202` answer:

| Field | Type | Notes |
| ----- | ---- | ----- |
| `runId` | string | The created or existing run |
| `created` | boolean | `false` means this analysis already existed; a replay reports the originally recorded value |
| `state` | string | Run state as the platform reports it |
| `currentRunId` | string \| undefined | Present only when the returned run is not the match's current run |
| `workflowId`, `workflowVersion` | string | Shown after the fact only, never promised before |

## Refusal (in memory)

`PlatformFailure` (existing class) with a kind and code; `requestAnalysisOutcome.ts` maps it to one of the message keys
in [contracts/ui-outcomes.md](contracts/ui-outcomes.md) plus whether the same key may be repeated.

## Existing entities used unchanged

- **Analysis run summary** (`AnalysisRunSummary`): the run list item with `isCurrent`, `supersededBy`, state.
- **Recording lineage**: `recordingSetVersions` empty means "no finalized recording set" for the disabled state only.
- **Session member**: `canEditTeamMatches(member, teamId)` for visibility only.

## Validation rules

- No input is collected; the request has no body and no choice of workflow, profile, image, or version.
- The key is generated per confirmation and reused only for a repeat of that confirmation after an unknown or temporary
  result.
