# Research: Analysis Result Read for Jobs

Builds on Analysis Result Manifests and Validated Acceptance
(`specs/20261013-125502-analysis-result-manifests`: result locations,
`analysis_result_object_declarations`, `analysis_accepted_result_manifests`,
`IObjectStorage` single-object operations, `ResultPublisher`, simulated result
modes), Analyst Manager Work Execution
(`specs/archive/20261012-141233-manager-work-execution`: `AttemptRunner`,
`Runtime/Simulated/`, `SocAlytics.Platform.EndToEnd.Tests`), and Durable
Analysis Workflow (`analysis_logical_job_inputs` with `InputSnapshotEntry`:
`UpstreamNodeKey`, `EdgeKind`, `Status` = `accepted`, `absent`, `failed`, or
`not-applicable`, and `AcceptedResultIds`).

## R1. Read operation

- **Decision**: `GET /api/v1/analysis/attempts/{attemptId}/upstream-results`,
  with operation id `listAnalysisUpstreamResults`, policy `AnalystManager`
  (DPoP).
  - **Query**: `fencingToken`, `capabilityId`, `capabilityVersion`,
    `schemaVersion`, optional `segmentIds` (repeatable) or
    `matchStartMs`/`matchEndMs`, `pageSize` (at most
    `Analysis:Reads:MaxPageSize`, development value 100), and
    `continuationToken`.
  - **Checks**: the attempt must be current (the fencing token equals the
    current attempt's, and the attempt is not completed) and owned by the
    authenticated Manager. The capability and schema version must belong to an
    upstream node of the job's dependency edges.
  - **Order**: results are sorted by segment window start, then by
    accepted-result id. The continuation token is opaque and encodes the last
    position (FR-005).
- **Rationale**: A `GET` reflects a read with no side effects. The fencing
  token in the query keeps fenced attempts from reading (FR-004). It is a
  token, not a secret, so it may appear in the query.
- **Refusals**:
  - `409 attempt-obsolete` for a fenced or completed attempt;
  - `403` for another Manager's attempt (audited);
  - `400 upstream-dependency-unknown` for a capability or schema version that
    is not an upstream dependency (audited as out of scope);
  - `400` for invalid query fields;
  - `503 object-storage-unavailable` when grants cannot be presigned.
  - Revoked or ended registrations are refused by the existing authentication
    (`401`).

## R2. Results and coverage from the input snapshot

- **Decision**: The results are exactly the `AcceptedResultIds` of the job's
  snapshot entry for that upstream node, joined with
  `analysis_accepted_results` and `analysis_accepted_result_manifests`
  (FR-002). The manifest is returned as stored at acceptance: it is read from
  object storage and checked against the accepted manifest checksum before it
  is returned.
- **Coverage** has one entry per expected upstream segment of the run (or one
  entry for a match-scope upstream):
  - `accepted` when a snapshot result covers it;
  - otherwise the snapshot entry's status: `absent` → `missing`,
    `not-applicable` → `skipped`, and `failed` → `failed`. For a `failed` or
    `absent` node, each segment's upstream logical job state is used:
    `Blocked` → `blocked`, `Failed` → `failed`, anything else → `missing`.
  - A result accepted after the snapshot is reported as `missing`, never as
    `accepted`, so coverage and lineage stay equal to the job's inputs (the
    clarification).
- **Rationale**: Acceptance already checks lineage against the snapshot (spec
  3, R3), so reading from the same snapshot makes staging and acceptance agree
  by construction.

## R3. Read grants

- **Decision**: `IObjectStorage.PresignGetObject(objectKey, expiresAt)`, a new
  single-object presigned `GET`.
  - **Coverage**: one grant per artifact and one for the manifest.
  - **Expiry**: the earlier of now plus `Analysis:Reads:GrantLifetime`
    (development value `00:10:00`) and the attempt's current lease expiry
    (FR-003).
  - Grants are never logged or audited. They are counted with
    `socalytics.analysis.read_grants_issued` (FR-013).

## R4. Envelope contract

- **Decision**: `contracts/analysis/upstream-results/v1/upstream-results.schema.json`
  (JSON Schema 2020-12), with a release `1.0.0`, valid and invalid examples,
  and an index row.
- **Top-level fields**: `contractVersion`, `upstream` (capability id and
  version, `schemaVersion`, `edgeKind`), `results[]`, `coverage[]`, and
  `continuationToken`.
- **Each entry of `results[]`**: `acceptedResultId`, scope (`segmentId` with
  window, or `match`), `manifestChecksum`, `manifest` (the embedded
  result-manifest v1 object, by `$ref`), and `grants[]` (`name`, `url`,
  `expiresAt`).
- **Each entry of `coverage[]`**: `segmentId` (or `match`), and `state`
  (`accepted`, `missing`, `failed`, `blocked`, or `skipped`).
- **Forbidden**: `additionalProperties: false` everywhere, and no credentials
  other than the grant URLs.

## R5. Manager staging

- **Decision**: `Work/UpstreamStager` runs in `AttemptRunner` before launch
  for jobs with upstream dependencies, which the job snapshot declares:
  1. **Read**: read every upstream dependency, all pages.
  2. **Size check**: sum the manifest sizes and declared artifact sizes. Refuse
     when the total exceeds `Work:Staging:MaxBytesPerAttempt` (development
     value 2 GiB) or the host's free space on the staging volume
     (`staging-limit-exceeded`, `staging-disk-full`).
  3. **Download**: download each manifest and artifact with its grant into
     `{attemptDir}/inputs/{capabilityId}/{acceptedResultId}/`. Verify the
     manifest's SHA-256 against `manifestChecksum` and each artifact's SHA-256
     against the manifest (`staging-digest-mismatch`).
  4. **Coverage**: write `{attemptDir}/inputs/{capabilityId}/coverage.json`.
  5. **Renew grants**: re-read on a `403` from storage while the attempt is
     current.
  6. **Retries**: retry storage unavailability with bounded backoff until the
     execution timeout (`staging-storage-unavailable`).
  - A read refused as obsolete stops the attempt without completion. Any other
    staging failure completes the attempt as failed, and the Analyst is not
    launched (FR-008). The completion uses the attempt-completion contract's
    `failure.category`: `input-unavailable` for a digest mismatch, unavailable
    storage, or a refused read, and `resource-exhausted` for the staging limit
    or free space. The precise `staging-*` reason stays in the Manager's local
    diagnostics and logs, because the contract carries only sanitized
    categories.
- **Cleanup**: the attempt directory, including the inputs, is deleted when
  the attempt ends. At start, the Manager deletes every attempt directory it
  finds, because attempts never survive a restart (FR-014).
- **Storage downloads**: they use the same storage `HttpClient` as result
  uploads (spec 3), so no platform credentials go to storage.

## R6. Simulated Analyst

- **Decision**: For match-scope jobs, the simulated Analyst reads
  `inputs/*/coverage.json` and the staged manifests. It builds its
  `upstreamLineage` from them (accepted result id, capability, schema version,
  and manifest checksum) and records the consumed coverage in its artifact.
- **New mode**: `ignore-inputs` writes an empty lineage to test the lineage
  rejection (FR-011).

## R7. Test strategy

- **Contracts**: envelope schema and examples.
- **Platform integration tests** (PostgreSQL and RustFS):
  - snapshot-bound results and coverage mapping, including an optional result
    accepted after the snapshot;
  - paging stability, range filters, every refusal, and lease-bounded grant
    expiry;
  - audit of out-of-scope and foreign reads, with no grant URL in logs or
    audit.
- **Manager tests**:
  - `UpstreamStager` with a fake platform and fake storage: the happy path,
    digest mismatch, limit and disk refusals, renewal, an obsolete refusal,
    storage retries, and cleanup at end and at start;
  - the simulated lineage from the staged inputs.
- **End-to-end**: the development workflow's match node stages its
  segment-level results, and its accepted lineage equals the staged results.
  `ignore-inputs` is rejected with `result-lineage-mismatch` (FR-012).
