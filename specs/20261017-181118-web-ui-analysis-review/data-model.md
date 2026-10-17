# Data Model: Web UI Connected Analysis Review

Client-side, application-owned models in
`src/clients/web/src/platform/models/analysis.ts`. They are produced by the
adapters ([contracts/analysis-adapters.md](contracts/analysis-adapters.md))
from the generated client's DTOs and held only in memory. The platform remains
the system of record; nothing is persisted in the browser.

## AnalysisRunStatus

From `getAnalysisRun`.

| Field | Type | Notes |
| --- | --- | --- |
| `runId` | string | |
| `teamId`, `matchId`, `recordingSetVersionId` | string | `matchId` must equal the route's match (FR-003) |
| `workflowId`, `workflowVersion` | string | |
| `state` | `RunState` | known values `Requested`, `Running`, `PartiallyCompleted`, `Completed`, `Failed`, `Cancelled` (matched case-insensitively); unknown values kept raw |
| `failureReason` | string \| undefined | |
| `createdAt` | Date | |
| `runningAt`, `terminatedAt` | Date \| undefined | |
| `requestedCapabilities` | string[] | |
| `nodes` | `NodeStatus[]` | in platform order |

Derived: `inProgress = state ∈ {Requested, Running}` drives refresh (FR-006).

## NodeStatus

| Field | Type | Notes |
| --- | --- | --- |
| `nodeKey` | string | |
| `capabilityId`, `capabilityVersion` | string | |
| `tier` | string | e.g. `LowLevel`, `HighLevel`; raw fallback |
| `executionScope` | string | e.g. `Segment`, `Match`; raw fallback |
| `state` | string | e.g. `Waiting`, `Active`, `Completed`, `Failed`, `Blocked`; raw fallback |
| `blockedByNodeKey` | string \| undefined | shown when present |
| `jobs` | `{ waiting, ready, claimed, completed, failed, blocked: number }` | |

## FactStreamSummary

From `listAnalysisRunFactStreams`, one per capability node.

| Field | Type | Notes |
| --- | --- | --- |
| `nodeKey` | string | the selection key in the address (`stream`) |
| `capabilityId`, `capabilityVersion`, `schemaVersion` | string | passed to the fact read with `nodeKey` (FR-013) |
| `outputType`, `executionScope` | string | raw fallback |
| `required` | boolean | |
| `factCount` | number | |
| `provenance` | `Provenance` | |
| `coverage` | `{ accepted, missing, failed, blocked, skipped: number }` | counts per state |

## FactSelection

The member's current choice; mirrored in the address.

| Field | Type | Rules |
| --- | --- | --- |
| `runId` | string | |
| `stream` | `FactStreamSummary` | resolved from the `stream` node key; absent key → `capabilityNotInRun` |
| `range` | `{ fromMs?: number; toMs?: number }` | integers, `fromMs ≥ 0`, `toMs > fromMs` when both set; otherwise `invalidRange` (no request) |

Any change of `FactSelection` discards the continuation stack.

## FactPage

From `listAnalysisRunFacts`.

| Field | Type | Notes |
| --- | --- | --- |
| `run` | `{ runId, matchId, state }` | state at read time |
| `stream` | `{ nodeKey, capability, schemaVersion, outputType, executionScope, provenance }` | |
| `range` | `{ fromMs, toMs }` | the effective range the platform reports |
| `items` | `Fact[]` | platform order |
| `coverage` | `CoverageWindow[]` | full coverage for the range on every page |
| `continuationToken` | string \| undefined | absent on the last page |

## Fact

| Field | Type | Notes |
| --- | --- | --- |
| `acceptedResultId` | string | with `ordinal`, the React key |
| `ordinal` | number | |
| `kind` | string | text only |
| `startMs`, `endMs` | number | half-open |
| `confidence` | number | 0..1, shown as a percentage |
| `payload` | `JsonValue` | normalized from the generated client (research R4) |

## CoverageWindow

| Field | Type | Notes |
| --- | --- | --- |
| `startMs`, `endMs` | number | half-open match time |
| `state` | string | `accepted`, `missing`, `failed`, `blocked`, `skipped`; raw fallback |

## Provenance

| Field | Type | Notes |
| --- | --- | --- |
| `workflow` | `{ id, version }` | |
| `analystProfile` | `{ id, digest }` | |
| `imageDigest` | string | |
| `modelDigest` | string \| undefined | shown only when present |

## Continuation stack (client state)

`useFactPages` keeps `tokens: (string | undefined)[]` for the current
selection; `tokens[0]` is `undefined` (first page). Next pushes the current
page's `continuationToken`; Previous pops. Cleared on selection change, on
reload, and on `continuationRejected`. Never stored outside component memory.

## Review state

`deriveFactViewState` returns one of the states in
[contracts/review-states.md](contracts/review-states.md) from connectivity,
failures, the run's state, the selection, and the loaded page.
