# Data Model: Accepted Fact Indexing and Capability-Scoped Fact Queries

## Fact Artifact (object in the attempt's result location)

Contract: [`contracts/analysis/fact-artifact/v1/fact-artifact.schema.json`](contracts/fact-artifact.md).
Recognized by the manifest artifact `mediaType` `application/vnd.socalytics.facts+json`.

| Field | Type | Rules |
| --- | --- | --- |
| `contractVersion` | semantic version | `1.0.0` supported |
| `capability` | `{ id, version }` (common `capabilityRef`) | equals manifest `provenance.capability` and the node snapshot |
| `schemaVersion` | semantic version | equals manifest `provenance.schemaVersion` and the node's pinned output schema version |
| `outputType` | `foundational-facts` \| `soccer-concepts` | equals manifest `outputType` |
| `facts` | array | in artifact order; total across all fact artifacts of a result equals manifest `factCount`; at most 50,000 per result (configurable) |
| `facts[].kind` | common `key` | capability-owned label |
| `facts[].startMs` | integer ≥ 0 | match time, inclusive |
| `facts[].endMs` | integer | `> startMs`, exclusive; `[startMs, endMs)` inside the manifest's `validityWindow` |
| `facts[].confidence` | number 0..1 | required |
| `facts[].payload` | JSON object | capability-owned; at most 16 KiB serialized (configurable); not interpreted |

Additional properties are rejected at every envelope level; `payload` content is
open. The artifact is at most 16 MiB (configurable) and is read only after its
manifest digest check passed; the bytes parsed must hash to that digest.

## Indexed Fact (`socalytics.analysis_indexed_facts`, new, immutable)

| Column | Type | Source |
| --- | --- | --- |
| `accepted_result_id` | uuid, FK `analysis_accepted_results(id)` | acceptance |
| `ordinal` | integer ≥ 0 | position across the result's fact artifacts in manifest artifact order, then fact order |
| `run_id`, `node_id` | uuid | the attempt's job |
| `team_id`, `match_id` | uuid | the run |
| `capability_id`, `capability_version`, `schema_version`, `output_type` | text | fact artifact header (equal to the node snapshot) |
| `kind` | text | fact |
| `start_ms`, `end_ms` | bigint | fact; check `start_ms >= 0 AND end_ms > start_ms` |
| `confidence` | double precision | fact; check `0..1` |
| `payload` | jsonb | fact; check `jsonb_typeof(payload) = 'object'` |

- Primary key `(accepted_result_id, ordinal)`.
- Index `(run_id, node_id, start_ms, end_ms, accepted_result_id, ordinal)` for
  ordered, range-filtered keyset reads.
- Immutability trigger `socalytics.reject_immutable_change` on update/delete;
  `UPDATE`, `DELETE` revoked from `socalytics_app`.
- No `version` column: one entry in `PersistedTableClassifications` (immutable,
  identity by `(accepted_result_id, ordinal)`).
- Rows are inserted only by the acceptance unit of work of
  `CompleteAttemptHandler`; results accepted before the migration have no rows.

## Validated Result Manifest (existing, extended)

`ValidatedResultManifest` gains `Facts`: the ordered list of parsed facts
(kind, range, confidence, raw payload JSON) of all fact artifacts, empty when
`factCount` is 0 and no fact artifact exists.

Verifier check 12 (after the existing eleven checks), in order:

1. Each artifact with the fact media type: size ≤ limit, else
   `result-facts-too-large`.
2. Read the object (storage unavailable → retry as today); SHA-256 of the bytes
   equals the manifest digest, else `result-artifact-invalid`.
3. Parse and validate against `FactArtifactV1`, header equals manifest and node
   snapshot, else `result-facts-invalid`.
4. Each payload ≤ limit and running total ≤ facts-per-result limit, else
   `result-facts-too-large`.
5. Each range inside the validity window, else `result-facts-out-of-window`.
6. Total facts equal `factCount`, else `result-facts-count-mismatch`
   (also when `factCount > 0` without any fact artifact).

## Fact Stream (read model)

One per workflow node of the run:

| Field | Source |
| --- | --- |
| `nodeKey` | node snapshot |
| `capability` `{ id, version }`, `schemaVersion`, `outputType`, `executionScope` | node snapshot |
| `required` | whether the node is in the run's requested-capability closure |
| `factCount` | count of indexed facts of the node in the run |
| `provenance` | workflow id and version (run); Analyst profile id and digest, image digest, model digest (node snapshot) |
| `coverage` | counts per state over the node's coverage windows |

## Coverage Window (read model)

| Field | Rule |
| --- | --- |
| `startMs`, `endMs` | segment job: nominal window `[(n-1)·D, n·D)` from the run's pinned segmentation policy; match job and skipped nodes: `[0, end of the finalized recording set's mapped match-time coverage)` |
| `state` | job with accepted result → `accepted`; job `Failed` → `failed`; job `Blocked` → `blocked`; node not required by the run → `skipped`; otherwise `missing` |

Windows are ordered by `startMs` and limited to those overlapping the requested
range. No segment, recording, mapping, policy, or storage identifier is
exposed.

## Fact Page (read model)

`run` (`runId`, `matchId`, `teamId`, `state`), `stream` (as Fact Stream without
counts), `range` (as requested), `items` (facts: `acceptedResultId`, `ordinal`,
`kind`, `startMs`, `endMs`, `confidence`, `payload`), `coverage`, and
`continuationToken` (absent on the last page).

## Options

| Section | Key | Development default | Validation |
| --- | --- | --- | --- |
| `Analysis:Results` | `MaxFactArtifactBytes` | 16 MiB | 1 KiB .. `MaxObjectBytes` |
| `Analysis:Results` | `MaxFactPayloadBytes` | 16 KiB | 256 B .. 1 MiB |
| `Analysis:Results` | `MaxFactsPerResult` | 50,000 | 1 .. 1,000,000 |
| `Analysis:Facts` | `DefaultPageSize` | 100 | 1 .. `MaxPageSize` |
| `Analysis:Facts` | `MaxPageSize` | 500 | 1 .. 1,000 |

## Simulated Analyst (Analyst Manager)

- `valid` mode: `facts.json` (fact media type) with 3 deterministic facts in the
  job window and `factCount: 3`; the consumed coverage of match-level jobs moves
  to a supporting artifact `inputs.json` (`application/json`).
- New mode `invalid-facts` (`SimulatedResultModes.InvalidFacts`): one fact
  outside the window, so acceptance fails with `result-facts-out-of-window`.
