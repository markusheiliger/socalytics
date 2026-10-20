# Contract: Review States

The fact browser shows exactly one state at a time, decided by
`deriveFactViewState` (`src/clients/web/src/features/analysis/reviewState.ts`)
in the order below; the first matching row wins. Every state has its own
translation key in the `analysis` namespace and, where data exists, shows the
coverage windows (FR-012, FR-014).

| Order | State | Condition | What the member sees |
| --- | --- | --- | --- |
| 1 | `connectionRequired` | the browser is offline (connectivity state) | the connection-required message; no analysis data |
| 2 | `notFound` | run outcome `runNotFound`, or the run's match differs from the route | the not-found view |
| 3 | `notPermitted` | the member has no role on the team, or the platform refuses with `403` | the not-permitted view |
| 4 | `capabilityNotInRun` | facts outcome `capabilityNotInRun`, or the `stream` node key is not in the stream listing | message and the run's stream list |
| 5 | `invalidRange` | the address or form range is malformed, negative, or not increasing | range explanation; no fact request |
| 6 | `error` | any other failure | failure alert with reload |
| 7 | `loading` | a request for the current selection is pending without data | progress indicator, announced |
| 8 | `facts` | the page has facts | facts, paging, provenance, coverage; plus the incomplete note when the run is requested or running or a coverage window is not `accepted` |
| 9 | `noFactsInRange` | no facts and a range is set | "no facts overlap this range" with coverage |
| 10 | `inProgress` | no facts and the run is requested or running | "analysis is still in progress; no facts yet" with coverage |
| 11 | `incomplete` | no facts and a coverage window is not `accepted` | "analysis is incomplete" with the non-accepted windows |
| 12 | `completeWithoutFacts` | no facts and every coverage window is `accepted` | "analysis is complete and produced no facts" with coverage |

Independent of the state:

- When the run's refresh reports a different fact count or coverage counts
  for the selected stream than the open page was loaded with, a "new results
  are available" notice with a reload action appears (research R6).
- When a continuation is rejected, paging restarts at the first page and a
  note explains it.
- Unknown run, node, coverage, tier, scope, or output-type values are shown
  raw (FR-016).
