# Contract: Analysis Adapters

Application-owned adapters over the generated Kiota client
(`src/clients/web/src/platform/adapters/`). Components never call the
generated client directly. Models: [data-model.md](../data-model.md).

## `analysisRunAdapter` (extended)

```ts
export interface AnalysisRunAdapter {
  listByMatch(matchId: string, page?: PageRequest): Promise<Page<AnalysisRunSummary>>; // existing
  getRun(runId: string): Promise<RunOutcome>;
}

type RunOutcome =
  | { kind: 'found'; run: AnalysisRunStatus }
  | { kind: 'runNotFound' };
```

- `getRun` calls `getAnalysisRun`; `404` → `runNotFound`; other failures are
  thrown as `PlatformFailure` through the shared mapping.
- Missing required fields in the DTO → `PlatformFailure('incompatible')`.

## `analysisFactAdapter` (new)

```ts
export interface AnalysisFactAdapter {
  listStreams(runId: string): Promise<StreamsOutcome>;
  listFacts(request: FactRequest): Promise<FactsOutcome>;
}

interface FactRequest {
  runId: string;
  nodeKey: string;
  capabilityId: string;
  capabilityVersion: string;
  schemaVersion: string;
  fromMs?: number;
  toMs?: number;
  continuationToken?: string;
}

type StreamsOutcome =
  | { kind: 'found'; streams: FactStreamSummary[] }
  | { kind: 'runNotFound' };

type FactsOutcome =
  | { kind: 'found'; page: FactPage }
  | { kind: 'runNotFound' }
  | { kind: 'capabilityNotInRun' }
  | { kind: 'continuationRejected' };
```

- `listStreams` calls `listAnalysisRunFactStreams`; `listFacts` calls
  `listAnalysisRunFacts` and always sends `nodeKey` with the capability id,
  capability version, and schema version (FR-013); `pageSize` is not sent
  (platform default).
- The page's `range` echoes the request: `fromMs` and `toMs` are `null` in the
  platform's answer when the request did not set them and map to an absent
  bound; only a bound that is present but not a finite number is
  `incompatible`. The review never reads the page's `range` for display.
- Problem translation, local to these adapters:

  | Platform answer | Outcome |
  | --- | --- |
  | `404` with code `analysis-capability-not-in-run` | `capabilityNotInRun` |
  | `404` otherwise | `runNotFound` |
  | `400` whose field errors name `continuationToken` | `continuationRejected` |
  | `400` with code `analysis-capability-ambiguous` | thrown as `programmingError` (never expected, because `nodeKey` is always sent) |
  | any other failure | thrown `PlatformFailure` from the shared mapping |

- Payloads are normalized to `JsonValue` (research R4); a value that is not
  representable as JSON → `PlatformFailure('incompatible')`.
- Weak `ETag`s and `If-None-Match` are not used by the review; every refresh is
  a plain `GET`.
- The adapters keep no cache; TanStack Query holds results in memory only.

## Query keys (`features/analysis/analysisKeys.ts`)

| Key | Data | Refresh |
| --- | --- | --- |
| `['analysis', 'run', runId]` | `RunOutcome` | every 10 s while the run is in progress and the page is visible |
| `['analysis', 'streams', runId]` | `StreamsOutcome` | with the run |
| `['analysis', 'facts', runId, nodeKey, fromMs, toMs, token]` | `FactsOutcome` | never automatically; reload on request |
