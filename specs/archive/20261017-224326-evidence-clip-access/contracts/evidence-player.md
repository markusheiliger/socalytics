# Contract: Evidence Player (Web UI)

Extends the analysis review of
[Web UI Connected Analysis Review](../../20261017-181118-web-ui-analysis-review/contracts/routes-and-roles.md). No
route, navigation item, or search parameter is added; the player is a dialog of the review route
`/matches/:matchId/analysis-runs/:runId`.

## Entry point

- Every fact row of `FactBrowser` gains a "Play evidence" button (translation key `analysis:evidence.play`) with an
  accessible name that includes the fact's match-clock range. It is shown to every member who sees the fact list.
- Activating it opens `EvidencePlayerDialog` with focus inside the dialog; closing returns focus to the button and
  releases the media elements, their sources, and every resolution in memory.

## Platform access

- `analysisEvidenceAdapter.resolve(runId, request)` calls `resolveAnalysisEvidenceMedia` through the generated client
  and maps the response to application models in `src/platform/models/analysis.ts`; the player never calls the
  generated client directly.
- The player requests `fact` with `contextBeforeMs` and `contextAfterMs` of `5000`, and sets `retryFailed` only from
  "Try again" when the current part is `failed`.
- Outcomes: resolution, `notFound` (`404`), `rateLimited` (`429`, with the retry delay), `storageUnavailable` (`503`),
  `offline`, and `error`.

## States and messages

| State | Message (key under `analysis:evidence.`) | Controls |
| --- | --- | --- |
| `resolving` | `resolving` | Close |
| `playing` | `playing`, plus `insideFact` or `context` for the current position | Native controls, Close |
| `preparing` | `preparing` with the window and the next automatic attempt | Close; after 20 automatic attempts: Try again |
| `partUnavailable` | `failed` with window and reason, or `notCovered` with window | Continue with the next playable part, Try again for `failed`, Close |
| `shortened` | `shortened` with the covered window (shown with any other state) | — |
| `renewing` | `renewing` | Close |
| `notFound` | `notFound` | Close |
| `rateLimited` | `rateLimited` with the delay | Try again after the delay, Close |
| `offline` | `offline` (connection required) | Close |
| `error` | `error` (also for `storageUnavailable`) | Try again, Close |

Unknown part states and failure reasons are shown with their raw values.

## Playback rules

- Pieces play in order from `startOffsetMs` to `endOffsetMs`, measured from the media's first seekable time; the next
  piece is loaded in the alternate video element while the current one plays.
- A piece is not started when its link expires within 30 seconds; the player resolves again first. A media error, or a
  stall close to `expiresAt`, triggers one re-resolution and resumes at the current match-time position.
- Video elements use `crossorigin="anonymous"`, `preload="auto"`, and `playsinline`; autoplay starts muted only when
  the browser blocks unmuted playback.
- No resolution, link, or media is written to browser storage, history state, or the service worker.

## Accessibility

- The dialog has a title naming the capability and the fact's range, traps focus, closes with Escape, and announces
  state changes through a polite live region.
- All controls are keyboard operable with visible focus; axe rule checks run for every state.
