# Data Model: Web UI Analysis Fact Timeline

All entities are in-memory TypeScript types in the Web UI; nothing is persisted, cached by the service worker, or stored in browser storage (FR-015). No platform entity, contract, or migration changes. Existing types `FactStreamSummary`, `Fact`, `CoverageWindow`, `Provenance`, and `FactPage` (`src/clients/web/src/platform/models/analysis.ts`) are reused unchanged.

## Zoom Level

| Field | Type | Rule |
| ----- | ---- | ---- |
| `id` | `'10s' \| '30s' \| '1m' \| '5m'` | Fixed set (research R3) |
| `binMs` | number | 10 000, 30 000, 60 000, 300 000 |
| `bins` | number | Always 30; visible extent is `bins × binMs` |

## Timeline View State (review page state)

| Field | Type | Rule |
| ----- | ---- | ---- |
| `open` | boolean | Open by default when the run review shows; closed by a jump, reopened by "Back to timeline"; closing discards all lane data |
| `zoom` | Zoom Level id | Default chosen once, after all lanes have left `loading`; a user-chosen zoom is kept across reloads, close, and reopen within the visit |
| `extentStartMs` | number | Floored to a multiple of `binMs` and clamped to `[floor(extentStart), extentEnd - bins*binMs]`; moves by half the visible extent; a user-chosen extent is kept across reloads |
| `selection` | Timeline Selection or none | At most one; cleared by zoom or run change, and when the extent moves so that it is no longer fully visible |
| `textAlternative` | boolean | Shows the per-lane tables |

## Lane

One fact stream of the run, never merged with another node of the same capability.

| Field | Type | Source |
| ----- | ---- | ------ |
| `nodeKey`, `capabilityId`, `capabilityVersion`, `schemaVersion` | string | `FactStreamSummary` |
| `reportedFactCount` | number | `FactStreamSummary.factCount` (reference for partial) |
| `provenance` | `Provenance` | `FactStreamSummary.provenance` |
| `facts` | `{ startMs, endMs }[]` | Loaded pages, payload dropped; at most the lane allowance (R2) |
| `loadedFactCount` | number | `facts.length` |
| `partial` | boolean | `loadedFactCount < reportedFactCount` |
| `windows` | `CoverageWindow[]` | First page's `coverage`, sorted by `startMs` |
| `state` | Lane State | Derived (below) |

### Lane State

`loading`, `ready` (bins shown; `partial` flag), `inProgress` (run state known and `Requested` or `Running`, no accepted facts yet), `completeWithoutFacts` (run final, every window `accepted`, no facts), `noCoverage` (no windows and no facts), `capabilityNotInRun`, `unavailable` (loading failed twice or continuation rejected twice), `error` (failure with reload), `connectionRequired`. Rules: `inProgress` wins over `completeWithoutFacts` and `noCoverage` while the run is in progress; a lane with facts is always `ready`; unknown window states are kept raw and treated as not analysed. Any run state other than the known `Requested` and `Running`, including `Failed`, `Cancelled`, and an unknown value, is final: it is never `inProgress`, and an unknown raw run or node state is shown in the lane detail and never as zero.

## Density Bin

| Field | Type | Rule |
| ----- | ---- | ---- |
| `startMs`, `endMs` | number | `[startMs, endMs)`, `endMs - startMs = binMs` |
| `count` | number | Facts overlapping the range, once per bin (R4) |
| `coverage` | `'accepted' \| 'notAnalysed' \| 'partial' \| 'none'` | From the lane's windows (R4) |
| `state` | `'facts' \| 'analysedNoFacts' \| 'notLoaded' \| 'partial' \| 'notAnalysed' \| 'noCoverage'` | `facts` when `count > 0`; a zero count appears only as `analysedNoFacts` (fully accepted bin up to the loaded horizon, or any fully accepted bin of a lane that is not partial); in a `partial` lane a fully accepted bin with count 0 that starts after the start of the last loaded fact (the loaded horizon) is `notLoaded` ("facts beyond the loading bound not shown"), also shown in the text alternative |
| `rawStates` | string[] | Unknown window states overlapping the bin, shown raw |

Invariant: for a lane that is not `partial`, the sum over all bins of the overall extent of `count`, with multi-bin facts counted per bin, equals the number of overlapping (fact, bin) pairs; for the visible extent only facts overlapping it count (SC-002).

## Overall Extent

`[min, max)` over all windows and loaded facts of all lanes, rounded out to the finest bin size; undefined when no lane has a window or a fact, which gives the "extent unavailable" state with the capability list (FR-008).

## Timeline Selection

| Field | Type | Rule |
| ----- | ---- | ---- |
| `nodeKey` | string | The lane |
| `fromBin`, `toBin` | number | A bin or a contiguous span in the visible extent (derived from `fromMs`/`toMs`) |
| `fromMs`, `toMs` | number | `[start of first bin, end of last bin)`; the range of the jump; stored as absolute match time and kept by a refresh |

The selection shows its match-clock range and the sum of its bin counts or, when a bin is not a count state, that bin's coverage state label. For a `partial` lane the summed count is based on loaded facts only and is marked as partial ("at least N, based on loaded facts"), or shows the `notLoaded` state when the selected bins start after the loaded horizon; "Show facts" still lists every fact of the range through the platform read.

## Constants (development values)

`MAX_FACTS_PER_STREAM = 5000`, `MAX_FACTS_TOTAL = 20000`, `BINS_PER_VIEW = 30`.
