# Data Model: Web UI Materialized Segment Management

Client view models in `src/clients/web/src/platform/models/segments.ts`. Nothing is persisted; all values live in the
in-memory query cache and are discarded when the match page is left or the session ends (FR-008).

## MaterializedSegment

| Field | Type | Notes |
| --- | --- | --- |
| `recordingVersionId` | string (GUID) | Shown shortened, full value on demand |
| `timelineMappingDigest` | string | On demand only |
| `segmentationPolicyDigest` | string | On demand only |
| `segmentNumber` | number | Displayed as the interval within the recording version |
| `state` | `'absent' \| 'materializing' \| 'available' \| 'failed'` | Unknown platform values are kept as reported text |
| `mediaSizeBytes` | number \| undefined | Shown only when reported (available) |
| `failureReason` | string \| undefined | Shown only when reported (failed); rendered as text |
| `availableAt` | ISO timestamp \| undefined | Shown only when reported |
| `inUse` | boolean | A waiting, ready, or claimed job or an ongoing materialization keeps it |

Identity: (`recordingVersionId`, `timelineMappingDigest`, `segmentationPolicyDigest`, `segmentNumber`). Order is the
platform's order and is never re-sorted.

## SegmentList

`{ matchId: string; segments: MaterializedSegment[] }` — the whole list, not paged.

## SegmentSummary (derived, `segmentSummary.ts`)

| Field | Rule |
| --- | --- |
| `total` | `segments.length` |
| `absent`, `materializing`, `available`, `failed` | count per state |
| `other` | segments whose state is not one of the four |
| `inUse` | count of `inUse === true` |
| `purgeable` | count of segments with `inUse === false` and `state` in (`available`, `failed`); an unknown state or `materializing` is never purgeable |

The purge action is offered only while `purgeable > 0` (FR-009).

## PurgeOutcome

`{ matchId: string; purged: number; inUse: number }` as returned by the platform; shown as returned (the list is then
re-read, it is not adjusted locally).

## Area states (UI state machine)

`loading` → (`list` | `empty` | `notPermitted` | `error`); `list` → `refreshing` → `list`; `list` → `confirming` →
`purging` → (`outcome` | `purgeFailed`) → `refreshing` → `list`. `offline` overlays any state and disables actions.
