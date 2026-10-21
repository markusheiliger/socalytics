# Contract: Routes and Roles (additions)

Extends the Web UI's route contracts of
[Web UI Foundation](../../20261012-150657-web-ui-foundation/contracts/routes-and-roles.md)
and
[Web UI Match Workflow and Recording Upload](../../20261013-112844-web-ui-match-upload/contracts/routes-and-roles.md).
"Reader" means a Club Admin, or a Coach or Viewer of the match's team.

| Route | View | Allowed for | Platform operations |
| --- | --- | --- | --- |
| `/matches/:matchId` | Match view, unchanged except that each analysis run in the run list links to its review ("Open analysis review") | Reader | as before |
| `/matches/:matchId/analysis-runs/:runId` | Analysis review: run status and nodes, fact streams, and for the selected stream the fact browser with range form, paging, coverage, and provenance | Reader | `GET /api/v1/matches/{matchId}` (match context), `GET /api/v1/analysis/runs/{runId}` (`getAnalysisRun`), `GET /api/v1/analysis/runs/{runId}/fact-streams` (`listAnalysisRunFactStreams`), `GET /api/v1/analysis/runs/{runId}/facts` (`listAnalysisRunFacts`) |

## Search parameters of the review

| Name | Meaning | Rules |
| --- | --- | --- |
| `stream` | node key of the selected fact stream | absent: no stream selected; unknown: `capabilityNotInRun` state |
| `fromMs` | inclusive start of the match-time range | non-negative integer; malformed: `invalidRange` state |
| `toMs` | exclusive end of the match-time range | integer greater than `fromMs`; malformed: `invalidRange` state |

Selecting a stream or applying or clearing a range replaces the search
parameters (one history entry per selection change); paging does not change
the address.

## Rules

- The view loads the match through the match context and shows the
  not-permitted view when the member has no role on the match's team, as the
  match view does.
- A run that the platform does not return (`404`), or whose `matchId` differs
  from `:matchId`, shows the not-found view.
- No navigation item is added; the review is reached from the match view, and
  its back link returns to the match.
- The review offers no run controls, playback, clips, downloads, export,
  visualizations beyond lists, or cross-run comparison (FR-021).
