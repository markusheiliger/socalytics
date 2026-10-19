# Research: Accepted Fact Indexing and Capability-Scoped Fact Queries

## R1 — Where facts come from

- **Decision**: Facts live in result artifacts whose manifest `mediaType` is
  `application/vnd.socalytics.facts+json`. Each such artifact follows the new
  `fact-artifact` v1 contract. Artifacts with other media types stay opaque
  supporting artifacts. The `result-manifest` v1 contract is unchanged.
- **Rationale**: The manifest already lists artifacts with name, digest, size,
  and media type, and the Manager already uploads them through per-object
  grants. A media type marks fact artifacts without a manifest change, keeps
  the Docker test Analyst (`summary.json`, `factCount: 0`) valid, and lets a
  result split facts across several artifacts.
- **Alternatives considered**: a fixed artifact name (`facts.json`) — brittle
  and collides with existing test names whose content is not in the new
  format; a new manifest field listing fact artifacts — needs a result-manifest
  revision and changes in both solutions; inlining facts in the manifest —
  violates the bounded manifest size and the "references only" design.

## R2 — Payload validation depth

- **Decision**: The platform validates only the envelope (clarification
  2026-10-17): header (`contractVersion`, capability id and version,
  `schemaVersion`, `outputType`), and per fact `kind` (common `key`),
  `startMs`/`endMs` (non-negative integers, `endMs > startMs`), `confidence`
  (0..1), and `payload` (a JSON object at most 16 KiB serialized).
- **Rationale**: Capability payload semantics belong to the capability owners
  ([Contracts And Compatibility → Semantic Ownership](../../../docs/architecture/contracts-and-compatibility.md#semantic-ownership));
  the Analyst manifest declares only `output.schemaVersion`, so there is no
  payload schema to validate against yet.
- **Alternatives considered**: importing per-capability payload schemas from
  Analyst images — needs registry and manifest changes beyond this feature;
  no validation — would index unbounded or malformed data.

## R3 — When and how indexing happens

- **Decision**: `ResultManifestVerifier` reads and parses fact artifacts as a
  twelfth ordered check, after the artifact digest check (10) and the validity
  and fact-count check (11), outside the unit of work, like the manifest read.
  It returns the parsed facts in `ValidatedResultManifest`. The second pass of
  `CompleteAttemptHandler`, which re-checks the attempt under locks, inserts
  the facts with `IIndexedFactStore` in the same unit of work as
  `IAcceptedResultStore.InsertAsync` and `IAcceptedResultManifestStore.InsertAsync`.
- **Rationale**: One transaction gives atomicity without a follow-up worker
  (clarification 2026-10-17). The existing completion idempotency (stored
  outcome per retry key and fingerprint) returns the stored outcome on replays,
  so facts are never inserted twice; the primary key
  `(accepted_result_id, ordinal)` is a second guard. Bytes are re-read from
  storage after the digest check, and the parsed bytes' SHA-256 must equal the
  manifest digest, so the parsed content is exactly the attested object.
- **Alternatives considered**: an outbox-driven indexing worker — needs an
  "indexed" visibility flag and recovery logic for partial indexes; streaming
  insert during verification — would write outside the locked unit of work.

## R4 — Rejection reasons

- **Decision**: New reasons in `ResultRejectionReasons`:
  `result-facts-invalid` (unparseable, contract-invalid, or header mismatch with
  the manifest or node snapshot), `result-facts-out-of-window` (a fact's range
  not inside the validity window), `result-facts-count-mismatch` (total differs
  from `factCount`), and `result-facts-too-large` (artifact, payload, or count
  limit). Storage unavailability while reading a fact artifact is
  `StorageUnavailable`, which retries the completion as today.
- **Rationale**: Named reasons let tests and the Manager's diagnostics
  distinguish failures; they follow the existing `result-*` naming and the
  completion outcome reason column.
- **Alternatives considered**: reusing `result-artifact-invalid` and
  `result-validity-invalid` — hides which rule failed.

## R5 — Storage layout

- **Decision**: `socalytics.analysis_indexed_facts` with columns
  `accepted_result_id` (FK to `analysis_accepted_results`), `ordinal`,
  `run_id`, `node_id`, `team_id`, `match_id`, `capability_id`,
  `capability_version`, `schema_version`, `output_type`, `kind`, `start_ms`,
  `end_ms`, `confidence`, `payload jsonb`; primary key
  `(accepted_result_id, ordinal)`; read index
  `(run_id, node_id, start_ms, end_ms, accepted_result_id, ordinal)`;
  immutability trigger and revoked `UPDATE`/`DELETE` as in migration 0017; no
  `version` column, so a `PersistedTableClassifications` entry.
- **Rationale**: Mirrors the immutable accepted-result tables; the read index
  serves keyset pagination in the required order for one node of one run.
- **Alternatives considered**: storing facts as one JSON document per result —
  cannot paginate or range-filter across results; a separate time-series store
  — a new technology the architecture does not adopt.

## R6 — Selecting a stream

- **Decision**: The facts read takes `capabilityId`, `capabilityVersion`, and
  `schemaVersion` (all required) and an optional `nodeKey`. It resolves the
  run's node snapshot(s) with that capability and version. None →
  `404` problem `analysis-capability-not-in-run`; schema version differs from
  the node's pinned output schema → the same problem; more than one node and
  no `nodeKey` → `400` problem `analysis-capability-ambiguous`.
- **Rationale**: Workflow graphs require unique node keys but not unique
  capabilities; the edge case must be explicit (FR-019).

## R7 — Ordering and pagination

- **Decision**: Order by `start_ms, end_ms, accepted_result_id, ordinal`.
  Keyset continuation token: an opaque, base64url-encoded JSON of the last key
  plus a hash of the selection (run, node, schema version, range) so a token
  used with another selection is rejected as invalid. Page size default 100,
  maximum 500 (`Analysis:Facts` options).
- **Rationale**: Stable under concurrent acceptance of new results of a running
  run: facts inserted later only appear after or before the cursor by key, never
  twice. Matches the `items`/`continuationToken` convention.
- **Alternatives considered**: offset pagination — duplicates and gaps while a
  run is still accepting results.

## R8 — Range filter

- **Decision**: `fromMs`/`toMs` optional; when both are present
  `toMs > fromMs`; one side may be omitted (open). A fact is selected when
  `start_ms < toMs AND end_ms > fromMs` (overlap, clarification 2026-10-17).
- **Rationale**: Overlap returns concepts that span the window boundary, which
  a reviewer of a time window expects.

## R9 — Coverage for members

- **Decision**: Coverage entries come from the node's logical jobs in the run,
  not from an input snapshot: a job with an accepted result → `accepted`;
  job state `Failed` → `failed`; `Blocked` → `blocked`; other states →
  `missing`. A workflow node that the run does not require (outside the
  requested capabilities' closure, so it has no jobs) reports a single
  `skipped` entry for the whole match. Each segment job's window is its
  nominal half-open window (`(n-1)·D, n·D`) from the run's pinned segmentation
  policy; a match-scope job's window is `[0, end of the finalized recording
  set's mapped match-time coverage)`. Entries are ordered by window start and
  limited to windows that overlap the requested range. Coverage is
  recomputed per request; it is not paginated and is returned on every page.
- **Rationale**: Members need the run's current state, not a job's frozen
  snapshot; using match-time windows hides segment and recording identifiers
  (FR-016). The state names match `CoverageBuilder`; only `skipped` is derived differently, because members read the run's current state rather than a job's input snapshot.
- **Alternatives considered**: segment ids in coverage — exposes segmentation;
  merging adjacent windows of the same state — loses the per-unit accounting
  that SC-004 measures.

## R10 — Provenance

- **Decision**: Provenance comes from the run (workflow id and version) and the
  node snapshot (Analyst profile id and digest, image digest, model digest when
  present, capability and schema versions), never from Manager-reported data.
- **Rationale**: Acceptance already guarantees every accepted result matches
  the run-pinned snapshot, so the snapshot is the authoritative provenance for
  all facts of the node.

## R11 — Authorization and HTTP behavior

- **Decision**: Both operations use `MapMemberApi()` (`ActiveMember` policy,
  `SessionAntiforgeryFilter`), `IAccessAuthorizer.AuthorizeTeamResourceAsync`
  with the `analysis-run` scope source and `TeamPermission.Read` (Club Admin,
  Coach, Viewer). Denied or missing runs → `404`; validation errors →
  `400` via `ProblemResults`. Responses carry a weak ETag over the run's
  version basis, the selection, and the page cursor; `If-None-Match` → `304`.
  Reads are not audited, like `getAnalysisRun`.
- **Rationale**: Reuses the established member-read pattern and the
  [representation conventions](../../../docs/architecture/contracts-and-compatibility.md#representation-conventions)
  for views.

## R12 — Simulated Analyst facts

- **Decision**: In `valid` mode, `SimulatedResultWriter` writes `facts.json`
  with media type `application/vnd.socalytics.facts+json` and three
  deterministic facts per job inside the job's window (for segments: at
  window start, middle, and the last second; for the match: three spread
  concepts), each with kind `simulated-observation` or `simulated-concept`
  (by output type), confidence 0.9, and a small payload; `factCount` equals 3.
  The new `invalid-facts` mode writes a fact outside the window. The writer
  keeps `consumedCoverage` for match-level lineage evidence in a separate
  supporting artifact so the fact artifact stays contract-valid.
- **Rationale**: Gives deterministic, countable evidence for indexing and
  reads without real capabilities (FR-023).

## R13 — Environment

- **Decision**: No new tooling. The feature uses .NET 10, Docker-backed
  PostgreSQL/RustFS/NATS, and SoftHSM2, which the environment actions already
  provide; the unmerged environment spec `20261017-163801-analyst-media-environment`
  is not needed.
