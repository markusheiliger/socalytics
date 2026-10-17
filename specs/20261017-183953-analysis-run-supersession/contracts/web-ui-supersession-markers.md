# Contract: Web UI Supersession Markers

Applies to the match page's run list,
`src/clients/web/src/features/lineage/AnalysisRunStates.tsx`, for every member
who may read the match (Club Admins, and Coaches and Viewers of the owning
team).

## Adapter (`analysisRunAdapter`)

`listByMatch` and the cancellation feature's `cancel` map the additive fields
of `AnalysisRunSummaryDto` / `AnalysisRunStatusDto` into `AnalysisRunSummary`:

```ts
isCurrent: boolean;
supersededBy?: { runId: string; recordingSetVersionId: string; recordingSetFinalizedAt: Date };
supersededAt?: Date;
cancellationCause?: 'member' | 'superseded';
```

No new adapter method and no browser storage.

## Run line

| Run | Shown in the run line |
| --- | --- |
| Current run | The existing line plus the text marker "Current run" |
| Superseded, ended before supersession | The existing line (state, member cancellation details if any) plus "Superseded by the run of the recording set finalized {time} · {supersession time}" |
| Cancelled by supersession | State shown as "Cancelled (superseded)", no cancelling member, no reason, no cancel action, plus the superseded marker |
| Cancelled by a member, later superseded | The cancellation feature's member, time, and reason plus the superseded marker |
| Neither current nor superseded (another run of the current run's version) | The existing line only |

Markers are text with translation keys in `src/clients/web/src/i18n/en/lineage.json`
(English only), never conveyed by color alone, inside the line's existing
`role="status"` semantics. Superseded runs stay listed in their recording
set's group; nothing is hidden or collapsed. Times use the existing
`formatKickoff` formatting.

## Refresh

The existing refresh of the run list while any run is unfinished updates the
markers when a newer run supersedes an older one; no new polling or push
channel.
