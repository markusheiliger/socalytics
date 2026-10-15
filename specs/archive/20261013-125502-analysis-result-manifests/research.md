# Research: Analysis Result Manifests and Validated Acceptance

Builds on Durable Analysis Workflow (`specs/archive/20261005-130703-durable-analysis-workflow`:
`CompleteAttempt`, `analysis_accepted_results`, the attempt-completion contract),
Recording Lineage and Upload (`specs/archive/20261005-130702-recording-lineage-upload`:
`IObjectStorage`, presigned grants that sign length and checksum,
`RustFsContainerFixture`, `JsonSchemaContractValidator` with embedded
`contracts/**/*.schema.json`), and Analyst Manager Work Execution
(`specs/archive/20261012-141233-manager-work-execution`: Manager `Work/`,
`Runtime/Simulated/`, `PlatformClient` with DPoP, the
`SocAlytics.Platform.EndToEnd.Tests` project).

## R1. Result write grants

- **Decision**: A new Manager-facing operation
  `POST /api/v1/analysis/attempts/{attemptId}/result-grants`, protected by the
  existing `AnalystManager` policy with DPoP.
  - **Request**: the fencing token and the objects, each with `name`,
    `sizeBytes`, and `digest` (`sha-256:<hex>`).
  - **Response**: the `resultLocation` and one grant per object, each with
    `name`, `method`, `url`, `requiredHeaders`, and `expiresAt`.
  - **Result location**: `results/{runId}/{logicalJobId}/{attemptId}/`,
    derived by the platform and unique per attempt.
  - **Object names**: relative names matching `^[a-z0-9][a-z0-9._-]{0,127}$`;
    the manifest is always `manifest.json`.
  - **Grants**: single-object presigned `PUT` requests that sign
    `Content-Length` and `x-amz-checksum-sha256`, the same storage enforcement
    as recording parts. A new method `IObjectStorage.PresignPutObject` mirrors
    `PresignUploadPart`.
  - **Expiry**: the earlier of now plus `Analysis:Results:GrantLifetime` and
    the attempt's current lease expiry.
  - **Declarations**: every declared object is stored (attempt, name, size,
    digest). Repeating a name with the same digest is idempotent (fresh grant,
    US1 scenario 4); a different digest is refused, which makes stored objects
    unreplaceable (US1 scenario 6).
- **Refusals**:
  - `409 attempt-obsolete` for fenced or completed attempts;
  - `403` for another Manager's attempt (audited, R6);
  - `400 result-objects-invalid` for duplicate names, invalid names or
    digests, or exceeding `MaxObjectsPerAttempt`, `MaxObjectBytes`, or
    `MaxAttemptBytes`;
  - `409 result-object-redeclared` for a known name with a different digest.
- **Revoked and draining Managers**: the existing Manager authentication
  admits only `Active` and `Unregistering` registrations. Revoked and other
  ended registrations get `401`, with its existing audit, before the handler
  runs. `Unregistering` Managers keep obtaining grants for their own current
  attempts, so a drain can finish its work.
- **Rationale**: This reuses the proven direct-to-storage pattern: storage
  refuses wrong bytes, and the platform never relays them (FR-002, FR-004).
- **Alternatives considered**:
  - A writable prefix grant (for example STS or a POST policy): it is not
    portable across S3 stores and weakens integrity.
  - Uploading results through the API: it violates FR-004.

## R2. Result-manifest contract

- **Decision**: `contracts/analysis/result-manifest/v1/result-manifest.schema.json`
  (JSON Schema 2020-12), with `releases/1.0.0.schema.json`, `examples/valid/`,
  `examples/invalid/` with `expectations.json`, and an index row in
  `contracts/README.md`, following the existing contract layout.
- **Fields**:
  - `contractVersion` (`1.0.0`);
  - `scope`: `teamId`, `matchId`, `recordingSetVersionId`, `runId`, `nodeId`,
    `logicalJobId`, and either `segment` (identity and half-open window) or
    `match`;
  - `provenance`: capability id and version, `schemaVersion`,
    `analystProfileId` and digest, `imageDigest`, optional `modelDigest`,
    `policyVersions`, workflow id and version;
  - `attempt`: `attemptId`, `attemptNumber`;
  - `artifacts`: each `name`, `digest`, `sizeBytes`, `mediaType`;
  - `validityWindow` (half-open, in match time) and `factCount` (an integer of
    at least 0);
  - `upstreamLineage`: each `acceptedResultId`, capability id and version,
    `schemaVersion`, `manifestChecksum`;
  - `outputType` (`foundational-facts` or `soccer-concepts`), `confidence`
    (0 to 1), and `evidenceReferences` (identifiers only).
- **Forbidden**: `additionalProperties: false` at every level; the invalid
  examples cover credentials, presigned URLs, and inline media, as the
  attempt-completion contract does.
- **Rationale**: The architecture names exactly these fields, and the platform
  validates the schema independently (FR-005, FR-006).

## R3. Acceptance pipeline

- **Decision**: `CompleteAttempt` keeps its existing checks: contract shape,
  fencing, idempotency, and body digests and lineage, which still return
  `409 completion-rejected` as today. A successful completion then runs
  `ResultManifestVerifier` **before** the unit of work:
  1. **Location**: the reported `manifestReference` must equal
     `<resultLocation>manifest.json`, and `manifest.json` must be declared.
  2. **Manifest object**: it must exist and be at most `MaxManifestBytes`
     (read with a bounded ranged `GET`). Its storage-attested SHA-256 must equal
     both the reported `manifestChecksum` and the declared digest.
  3. **Schema**: the manifest must be valid against the embedded
     `result-manifest` schema of its `contractVersion`, and that version must be
     supported.
  4. **Scope and provenance**: they must equal the Logical Job and run
     snapshots, and the attempt identity must equal the current attempt.
  5. **Lineage**: `upstreamLineage` must equal, as a set, the job's recorded
     upstream accepted results with their capability and schema versions and
     manifest checksums.
  6. **Artifacts**: every artifact name must be declared with the same digest
     and size, and storage must attest that digest.
  7. **Validity**: `validityWindow` must lie within the segment window or the
     match scope, and `factCount` must be at least 0.
- **After verification**: inside the unit of work, the platform re-checks that
  the attempt is current.
  - **On success**: it inserts the accepted result (as today) plus one
    immutable `analysis_accepted_result_manifests` row.
  - **On a failed check**: it records the completion outcome as `failed` with
    reason `result-<check>` and applies the existing failed-attempt
    transition, so the attempt budget decides on a retry (clarification). It
    writes the audit event in the same unit of work.
  - **Storage unavailable**: `503 object-storage-unavailable`, with no state
    change and nothing recorded, so the Manager retries the identical
    completion (FR-012).
- **Reason codes**: `result-manifest-missing`, `result-manifest-too-large`,
  `result-manifest-checksum-mismatch`, `result-manifest-schema-invalid`,
  `result-manifest-version-unsupported`, `result-scope-mismatch`,
  `result-provenance-mismatch`, `result-lineage-mismatch`,
  `result-artifact-invalid`, `result-validity-invalid`.
- **Rationale**: Storage reads stay out of database transactions, and the
  fencing re-check keeps acceptance safe against concurrent fencing. Ten
  concurrent identical completions still produce one outcome through the
  existing idempotency (SC-005).

## R4. Storage integrity evidence

- **Decision**: Add two methods to `IObjectStorage`:
  - `GetObjectChecksumAsync(key)`: `HeadObject` with checksum mode, returning
    the full-object SHA-256 and the length;
  - `ReadObjectAsync(key, maxBytes)`: a bounded read.
- **Conformance**: the store conformance probe gets single-object cases. A
  presigned `PUT` with a wrong body or length is refused, and the checksum is
  reported after a correct `PUT`. It runs against RustFS 1.0.1 in the
  integration tests.
- **Fallback**: if storage cannot report a checksum, acceptance fails closed
  with `result-artifact-invalid` or `result-manifest-checksum-mismatch`, as
  recording completion does.

## R5. Persistence

- **Decision**: One forward-only migration
  `NNNN_analysis_result_manifests.sql`, numbered at implementation time as the
  next free number on an up-to-date default branch.
  - `analysis_result_object_declarations`: `attempt_id`, `name`,
    `size_bytes`, `digest`, `declared_at`; primary key `(attempt_id, name)`;
    immutable through `socalytics.reject_immutable_change()`.
  - `analysis_accepted_result_manifests`: `accepted_result_id` (primary key,
    foreign key to `analysis_accepted_results`), `contract_version`,
    `output_type`, `validity_start_ms`, `validity_end_ms`, `fact_count`,
    `artifacts` (jsonb), `upstream_lineage` (jsonb); immutable.
  - Both tables are classified as immutable in `PersistedTableClassifications.cs`.
- **Rationale**: The existing immutable `analysis_accepted_results` table stays
  unchanged, and its existing rows remain valid.

## R6. Audit and metrics

- **Decision**: `AnalysisAuditEvents` with:
  - `analysis.result-grant-refused` (actor `AnalystManager`; reason
    `foreign-attempt`; revoked or ended registrations are audited by the
    existing Manager authentication);
  - `analysis.result-rejected` (actor `AnalystManager`; the reason codes of
    R3).
- **Event details**: attempt, Logical Job, run, and reason only, never URLs,
  signatures, or headers.
- **Not audited**: issued grants. They increment the counter
  `socalytics.analysis.result_grants_issued` and are logged with attempt
  identity and object names (FR-017, SC-007). A test asserts that no log or
  audit record contains `X-Amz-Signature` or the grant URL.

## R7. Manager

- **Decision**:
  - `PlatformClient.RequestResultGrantsAsync`, using the existing DPoP pattern.
  - `Work/ResultPublisher`, run after a successful simulated execution:
    1. read the Analyst's output (`manifest.json` and artifacts) from the
       attempt's result directory;
    2. validate `manifest.json` against the embedded `result-manifest` schema
       (`JsonSchema.Net`, already pinned in the Manager's
       `Directory.Packages.props`, now referenced by Core);
    3. on a violation, upload nothing and report a failed completion with the
       attempt-completion contract's `failure.category` `analyst-error`,
       keeping `result-manifest-schema-invalid` in the local diagnostics
       (clarification; the contract carries only sanitized categories);
    4. otherwise compute the SHA-256 digests, request grants, `PUT` each
       object, renew grants on `403` while the attempt is current, and then
       complete with the manifest reference and checksum.
  - Refused grants (`attempt-obsolete`, `403`) stop the attempt like a fenced
    heartbeat (FR-014).
  - Storage `PUT` requests use a separate `HttpClient` without platform
    credentials. Plain HTTP is accepted only for loopback storage hosts in the
    Development and Test environments, because the platform client itself stays
    HTTPS-only.
  - The Manager embeds `contracts/analysis/result-manifest/v1/*.schema.json`
    the same way the platform's Infrastructure project does.
- **Simulated Analyst modes**: per capability, `valid` (the default),
  `scope-mismatch`, `provenance-mismatch`, `lineage-mismatch`,
  `artifact-missing`, `validity-invalid`, and `contract-invalid`. The manifest
  and one small artifact are deterministic from the job, and lineage comes
  from the job's inputs.

## R8. Test strategy

- **Contracts tests**: the manifest schema and its examples, the index row,
  and meta-validation.
- **Platform integration tests** (PostgreSQL, RustFS):
  - grant issuance, every refusal, and lease-bounded expiry;
  - re-declaration rules and the conformance cases;
  - one test per acceptance check with only that check violated (SC-002);
  - storage unavailable, idempotent concurrent completions, a fenced attempt
    with stored objects, and the audit and log assertions.
- **Manager tests**:
  - `ResultPublisher` with a fake platform and fake storage;
  - schema-invalid manifests never uploaded;
  - grant renewal, and refusal that stops the attempt;
  - the simulated modes.
- **End-to-end**: `SocAlytics.Platform.EndToEnd.Tests` adds a RustFS container
  (same pinned image as `RustFsContainerFixture`). It runs the development
  workflow to accepted results for both nodes, then the `lineage-mismatch`
  mode with its failed attempt and retry, and the `contract-invalid` mode with
  nothing uploaded (FR-016, SC-001).
