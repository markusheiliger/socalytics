# Research: Recording Lineage and Upload

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-07 (revised after the storage and platform spikes)

This document resolves every open technical question of the plan. Each entry
records the decision, why it was chosen, and the alternatives that were
evaluated. Shared persistence, identity, and API conventions are adopted from
the sibling foundation plans under their canonical names (`IUnitOfWork` /
`IUnitOfWorkScope`, `socalytics.attach_version_trigger(regclass)`,
`IRequestContext`, `IAuditTrail`, `IAccessAuthorizer`, `ITeamScopeResolver`,
`OperationResult<T>`, problem types `urn:socalytics:problem:<code>`) and are
not re-decided here.

Evidence sources: the storage spike (RustFS 1.0.1 and `AWSSDK.S3` 4.0.103.4,
presigned multipart upload with composite SHA-256) and the platform spike
(Aspire 13.4.6 container and parameter patterns), both run on 2026-10-07 and
summarized in the coordinator's risk brief.

## R1. .NET S3 client

- **Decision**: Use `AWSSDK.S3` 4.0.103.4 in the Infrastructure layer only,
  configured with `ServiceURL` set to the stamp's S3 endpoint,
  `ForcePathStyle = true`, `AuthenticationRegion` (default `us-east-1`), the
  request protocol derived from the endpoint scheme, and
  `RequestChecksumCalculation = WHEN_REQUIRED`,
  `ResponseChecksumValidation = WHEN_REQUIRED`. Operations used:
  `InitiateMultipartUpload`, presigned `UploadPart` (`GetPreSignedURL`),
  `ListParts`, `CompleteMultipartUpload`, `HeadObject` with
  `ChecksumMode = ENABLED`, `AbortMultipartUpload`, `DeleteObject`, and, in
  development only, `PutBucket`.
- **Rationale**: The AWS SDK is the reference implementation of SigV4
  presigning and of the additional-checksum headers the design relies on; the
  spike proved every listed call against RustFS 1.0.1, including
  `ChecksumType = COMPOSITE` on initiation (exposed by 4.0.x). It works with any
  S3-protocol endpoint through path-style addressing, so the platform stays
  implementation-neutral (constitution Principle III,
  [terminology-and-principles.md](../../docs/architecture/terminology-and-principles.md#3-s3-as-universal-data-plane)).
  Keeping the SDK out of Application and Domain (architecture test) keeps the
  store and client replaceable.
- **Alternatives considered**: the Minio .NET SDK (product-named, upstream
  server archived); hand-written SigV4 over `HttpClient` (re-implements
  security-critical signing); Azure Blob SDK (not the S3 protocol).

## R2. Local S3-compatible implementation (Aspire and Testcontainers)

- **Decision**: RustFS, pinned image `rustfs/rustfs:1.0.1`. The AppHost adds it
  with stable Aspire 13.4.6 APIs only:
  `builder.AddContainer("rustfs", "rustfs/rustfs", "1.0.1")` with
  `.WithHttpEndpoint(targetPort: 9000, name: "s3")`,
  `.WithEnvironment("RUSTFS_ACCESS_KEY", …)`,
  `.WithEnvironment("RUSTFS_SECRET_KEY", …)`, and
  `.WithHttpHealthCheck("/health", endpointName: "s3")`. Credentials are
  generated persisted secret parameters
  (`AddParameter(name, new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true)`),
  which persist only because the AppHost has a `<UserSecretsId>` (added by the
  persistence foundation; this feature verifies it). The API receives the
  endpoint through a `ReferenceExpression`
  (`ObjectStorage__ServiceUrl` = the `s3` endpoint URL) and `WaitFor(rustfs)`.
  Integration tests run the same image with the generic Testcontainers
  `ContainerBuilder`.
- **Rationale**: RustFS is the implementation the architecture names first, is
  Apache-2.0 licensed and generally available, and passed the full storage
  spike. The only stable `CommunityToolkit.Aspire.Hosting.RustFS` release
  (13.5.0) requires Aspire 13.5, and the 13.4-compatible releases are betas, so
  a plain container keeps the AppHost on stable 13.4.6 packages. MinIO images
  are no longer pullable (`minio/minio` repository removed; `quay.io` 401).
- **Alternatives considered**: the RustFS community package (beta or Aspire
  13.5 upgrade); MinIO (unavailable); Ceph RGW or LocalStack (heavier, no
  benefit).

## R3. Every upload is multipart (FR-007, FR-033; SC-010)

- **Decision**: Every upload session is one S3 multipart upload created at
  start with `ChecksumAlgorithm = SHA256` and `ChecksumType = COMPOSITE`. The
  client declares `totalSizeBytes`, a fixed `partSizeBytes`, and the ordered
  `sha-256:<hex>` digest of every part. The platform validates:
  - `totalSizeBytes ≥ 1` and ≤ `Recordings:Upload:MaxObjectSizeBytes`
    (≤ 5 TiB, the S3 object limit);
  - `partCount = ceil(totalSizeBytes / partSizeBytes)` equals the number of
    declared digests, `1 ≤ partCount ≤ Recordings:Upload:MaxPartCount`
    (≤ 10 000);
  - `partSizeBytes ≤ Recordings:Upload:MaxPartSizeBytes` (≤ 5 GiB), and when
    `partCount > 1`, `partSizeBytes ≥ Recordings:Upload:MinPartSizeBytes`
    (≥ 5 242 880, the storage minimum that S3 enforces only at completion);
  - every non-final part is exactly `partSizeBytes`; the final part is
    `totalSizeBytes − (partCount − 1) · partSizeBytes` (1…`partSizeBytes`);
  - every digest is a well-formed 32-byte SHA-256.

  An invalid declaration is rejected with `400 upload-declaration-invalid`
  before any storage call or session row.
- **Rationale**: A single `PUT` is capped at 5 GiB, while full-match
  recordings can exceed it (SC-010). Making every upload multipart (even a
  single part) gives one code path, parallel part uploads, per-part retry, and
  per-part integrity binding. Rejecting empty recordings is a platform policy
  (storage would accept a zero-byte part). Validating the 5 MiB rule at
  declaration avoids late `EntityTooSmall` failures at completion.
- **Alternatives considered**: single `PUT` below 5 GiB plus multipart above
  (two paths, two digest forms); presigned POST (no per-part checksums);
  server-side resumable protocols such as tus (not S3).

## R4. Canonical composite content digest (FR-033)

- **Decision**: The recording's content digest is
  `sha-256-parts:<partSizeBytes>:<partCount>:<hex>`, where
  `hex = lowercase hex(SHA-256(d₁ ‖ d₂ ‖ … ‖ dₙ))` and `dᵢ` is the raw 32-byte
  SHA-256 of part `i`. `totalSizeBytes` is stored and exposed as a separate
  field. The S3 form is `base64(bytes(hex)) + "-" + partCount`. On read the
  platform splits the storage value at the last `-`, requires the suffix to
  equal `partCount`, compares the decoded bytes, and requires
  `ChecksumType == COMPOSITE`. A `sha-256-parts` digest is never compared with
  a plain `sha-256` file digest, because the same bytes with a different part
  size (and even a single part, `SHA-256(SHA-256(file))`) produce a different
  value. Timeline-mapping and request digests keep the plain `sha-256:<hex>`
  form.
- **Rationale**: The value is computable from the declaration alone, is exactly
  what S3 reports after completion (spike Q3: expected and reported composite
  identical), and carries the part size and count that its meaning depends on.
- **Alternatives considered**: plain `sha-256` of the whole file (storage
  cannot report it for multipart objects without the platform reading media);
  embedding the total size in the digest string (size is metadata, not content
  identity; kept separate as the spike recommends); the S3 base64 form
  (implementation-shaped and lacks the part size).

## R5. Per-part upload grants (FR-008, FR-009, FR-010; SC-004, SC-010)

- **Decision**: A grant is a SigV4 presigned `UploadPart` URL for one part
  number of the session's multipart upload, signing `x-amz-checksum-sha256`
  (base64 of the declared part digest) and `Content-Length` (the part's exact
  size); `partNumber` and `uploadId` are signed through the query string. The
  client sends `PUT <url>` with exactly those two headers and no other
  `x-amz-*` header; `x-amz-sdk-checksum-algorithm` is omitted. Grants expire at
  `min(now + Recordings:Upload:GrantLifetime, session.expiresAt)`. Start upload
  returns grants for parts `1…min(partCount, MaxGrantsPerRequest)`; the grants
  operation returns grants for a requested list of part numbers (1–1 000
  unique, within `1…partCount`, at most `MaxGrantsPerRequest`). Grants are
  never stored, logged, audited, or kept in retry outcomes; audit records only
  the number of parts granted and the expiry.
- **Rationale**: The spike confirmed storage enforcement: wrong bytes →
  `400 BadDigest`; wrong or missing checksum header, wrong length, chunked
  transfer, or a tampered part number → `403`; an oversize body is refused on
  headers within milliseconds; nothing is persisted after failed attempts; a
  retry of a part through the same URL is idempotent. Signing `Content-Length`
  caps stored data at the declared part sizes (SC-010 "0 bytes beyond the
  declared part sizes"). A presigned part URL cannot list, read, delete,
  complete, or write another part or key.
- **Alternatives considered**: unsigned `Content-Length` (storage reads and
  discards oversize bodies; weaker bound); one grant for the whole object (not
  possible with multipart); returning all grants at start for up to 10 000
  parts (multi-megabyte responses).
- **Note**: browser clients need bucket CORS allowing `PUT` and the request
  header `x-amz-checksum-sha256`; deferred to the production profile and the
  client feature.

## R6. Completion recipe (FR-011, FR-012, FR-014)

- **Decision**: Completion runs these steps; storage calls run outside any
  database transaction:
  1. Authorize, check archive state, validate the timeline mapping and retry
     key, and require the session to be `pending` and unexpired.
  2. `ListParts` (paged) for the session's `uploadId`. Require exactly part
     numbers `1…partCount`, each with its declared size; take the ETags from
     this response. Missing parts → `409 upload-parts-incomplete`; a size
     difference → `409 upload-part-mismatch`.
  3. `CompleteMultipartUpload` with platform-built `PartETags` (part number,
     ETag, declared per-part `ChecksumSHA256`), the expected full composite in
     `ChecksumSHA256`, and `MpuObjectSize = totalSizeBytes`. Storage-side
     `InvalidPart`, `BadDigest`, or `EntityTooSmall` → `409 upload-part-mismatch`
     (session stays pending; the multipart upload stays usable for re-uploading
     parts); network failures and other `5xx` → `503 object-storage-unavailable`.
  4. `HeadObject` with `ChecksumMode = ENABLED`. Require
     `ChecksumSHA256 == base64(composite) + "-" + partCount`,
     `ChecksumType == COMPOSITE`, and `ContentLength == totalSizeBytes`. A
     missing checksum → `409 integrity-evidence-unavailable`; any difference →
     `409 upload-object-mismatch`.
  5. One unit of work: transition the session
     (`WHERE id = @Id AND state = 'pending' AND expires_at > now()`), insert the
     recording version and first timeline mapping, the retry outcome, and the
     audit event.

  **Recovery**: if a retry finds `NoSuchUpload` at step 2 (a previous attempt
  completed the multipart upload but did not commit), the handler skips to
  step 4 and proceeds when the assembled object matches. If step 5's guard
  fails because the session expired, the handler returns
  `409 upload-session-expired` and the expiry worker deletes the assembled
  object (R7). A post-assembly mismatch (step 4) cannot be repaired inside the
  session because its parts are consumed: the session stays pending until
  expiry, and the caller starts a new upload.
- **Rationale**: This is the spike's confirmed recipe. The platform builds the
  part list itself, so a client cannot complete with a subset (RustFS accepts a
  2-of-3 completion without the composite assertion; the `HeadObject`
  comparison would catch it, but it never gets that far). RustFS deviations
  are absorbed by design: `MpuObjectSize` is ignored (size verified from
  `HeadObject`); a wrong composite yields `500` instead of `400` (the platform
  never sends a wrong composite, and post-verification is authoritative);
  `ListParts` returns no per-part checksums and `GetObjectAttributes` reports
  wrong per-part checksums (neither is used); `HeadObject` with `PartNumber` is
  ignored (not used).
- **Alternatives considered**: trusting the client's declaration (forbidden by
  FR-011); client-supplied ETags (unnecessary; `ListParts` provides them);
  `GetObjectAttributes` as evidence (unreliable per-part checksums on RustFS);
  a stamp-internal verification worker reading bytes (not needed; the spike
  shows storage enforces and reports everything required).

## R7. Session expiry (FR-034; SC-010)

- **Decision**: Every session gets `expires_at = created_at +
  Recordings:Upload:SessionLifetime` (required configuration, no production
  default). Grants and completion refuse an expired session
  (`409 upload-session-expired`) using database time, independent of the
  worker. A hosted `UploadSessionExpiryWorker` (Infrastructure, registered by
  `AddInfrastructure()`) runs every `Recordings:Upload:ExpirySweepInterval`
  and calls the Application command `ExpireUploadSessionsHandler`, which:
  1. transitions due sessions in one unit of work per session
     (`state = 'pending' AND expires_at <= now()` → `expired`, `expired_at`),
     recording a system audit event `recording.upload.expire`;
  2. then releases storage for expired sessions whose `storage_released_at` is
     null: `AbortMultipartUpload` (`NoSuchUpload` is success) and
     `DeleteObject` on the session key (`NotFound` is success, removing an
     object assembled by a completion that lost the race), then sets
     `storage_released_at`.

  Every step is idempotent and state-guarded, so several API instances may run
  the worker concurrently without coordination.
- **Rationale**: Marking `expired` before touching storage makes expiry and
  completion mutually exclusive: completion commits only while
  `expires_at > now()`, and the worker only expires sessions with
  `expires_at <= now()`, so an accepted recording's object is never deleted.
  Storage held by an abandoned session is bounded by the session lifetime plus
  one sweep interval and by `partCount × partSize` (signed `Content-Length`).
- **Alternatives considered**: an `AbortIncompleteMultipartUpload` lifecycle
  rule only (not verified on RustFS, and it cannot mark sessions expired);
  aborting before marking expired (races with completion); a separate worker
  process (the API host already runs hosted services; no extraction evidence).
- **Residual**: a crash between `InitiateMultipartUpload` and the start
  transaction leaves an unreferenced multipart upload; the handler aborts it
  best-effort on failure. A storage lifecycle rule for incomplete uploads is a
  production-profile item.

## R8. Status codes for verification and dependency failures

- **Decision**: Declaration errors → `400 upload-declaration-invalid`; invalid
  part-number requests → `400 part-numbers-invalid`; missing parts →
  `409 upload-parts-incomplete`; part size or checksum mismatch →
  `409 upload-part-mismatch`; assembled object mismatch →
  `409 upload-object-mismatch`; missing integrity evidence →
  `409 integrity-evidence-unavailable`; expired session →
  `409 upload-session-expired`; completed session →
  `409 upload-session-completed`; storage unreachable, timeouts, or unexpected
  `5xx` → `503 object-storage-unavailable` with no state change.
- **Rationale**: The shared mapping reserves 409 for state conflicts and 503
  for unavailable dependencies; verification failures are conflicts between the
  stored state and the declaration and are retryable after correction
  (FR-026).
- **Alternatives considered**: `422` (not in the shared mapping).

## R9. Timeline mapping representation and digest

- **Decision**: A timeline mapping is an ordered list of 1 to 64 spans. Each
  span maps the media interval `[mediaStartSeconds, mediaEndSeconds)` to match
  time starting at `matchStartSeconds`, at a 1:1 rate. Values are nonnegative
  numeric seconds with at most millisecond precision. Spans have positive
  length and are strictly increasing and non-overlapping in media time and in
  match time. Internally values are integer milliseconds. The mapping digest is
  `sha-256:<hex>` over the canonical JSON
  `{"spans":[{"matchStartMilliseconds":…,"mediaEndMilliseconds":…,"mediaStartMilliseconds":…}],"version":1}`
  (UTF-8, lexicographically ordered properties, no insignificant whitespace,
  integers only, spans in media order).
- **Rationale**: Spans express a recording that starts mid-match, both halves
  with the break cut out, and multi-camera offsets without media inspection.
  Integer milliseconds make the canonical form deterministic. The digest covers
  content only, so it is the timeline-mapping digest of the materialized-segment
  identity in
  [match-data-pipeline.md](../../docs/architecture/match-data-pipeline.md#segment-contract),
  and identical content on one recording version deduplicates (FR-016).
- **Alternatives considered**: a single offset (cannot express cut breaks);
  variable-rate anchors (unjustified); frame-based mapping (needs media
  inspection); an RFC 8785 library (integers only, a small writer suffices).

## R10. Retry keys and replay (FR-025, FR-026)

- **Decision**: The `Idempotency-Key` header (1–255 visible ASCII characters)
  is required on start upload, complete upload, revise timeline mapping, and
  finalize. The append-only table `recording_retry_outcomes` stores
  `(operation, match_id, idempotency_key)` uniquely with the request digest
  (`sha-256` over canonical JSON of operation, route targets, and normalized
  body, including every part digest), the success status, and the identities
  produced. The row commits with the change. On a repeat the handler
  re-authorizes first, then compares digests: equal → replay by re-reading the
  referenced resources; different → `409 idempotency-key-reused`. A concurrent
  duplicate hits the unique key after the winner commits; the loser rolls back,
  aborts any multipart upload it created, and replays. A start-upload replay
  returns the original session in its current state: with freshly issued
  grants for the first parts while it is pending and unexpired, and without
  grants once it is completed or expired.
- **Rationale**: Identities instead of response bodies keep grants out of
  durable records (FR-029); the unique key makes duplicates safe (SC-006);
  failed requests never record an outcome (FR-026).
- **Alternatives considered**: storing responses (would persist grants);
  advisory locks (the unique key already serializes); a shared idempotency
  table (none exists; the architecture names Recordings-owned "scoped
  idempotency outcomes").

## R11. Recordings-finalized event record (FR-022, FR-023, FR-024)

- **Decision**: Finalization inserts one row into the append-only table
  `recording_finalized_events` in the same unit of work as the set version, its
  memberships, the retry outcome, and the audit event. The row holds the event
  id, event type `matches.recordings-finalized`, contract version `1.0.0`, the
  recording-set-version id (unique), Match, Team, occurrence time, and the
  payload valid against
  [recordings-finalized.schema.json](contracts/schemas/recordings/recordings-finalized/v1/recordings-finalized.schema.json)
  (published as
  `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`;
  member content digests use the `sha-256-parts` form). The row has no
  publication state and this feature makes no outbox call: Durable Analysis
  introduces `IOutbox` and `outbox_messages`, adds the `IOutbox` call to this
  feature's finalize handler, backfills outbox entries for records finalized
  before it merged, and tracks publication separately keyed by `event_id`.
- **Rationale**: The unique constraint enforces exactly one record (FR-024);
  an immutable row satisfies User Story 3 and the immutability trigger;
  transport stays out of scope.
- **Alternatives considered**: a generic outbox table with `published_at`
  (publisher semantics belong to Durable Analysis; would make evidence mutable);
  publishing now (out of scope).

## R12. Immutability enforcement (FR-015, FR-021)

- **Decision**: Recording versions, timeline mappings, recording-set versions,
  memberships, finalized-event records, and retry outcomes have no `version`
  column. The shared trigger function `socalytics.reject_immutable_change()`
  (created by this feature's migration) is attached `BEFORE UPDATE OR DELETE`
  and raises; the migration revokes `UPDATE, DELETE` on these tables from
  `socalytics_app`. The tables are classified as immutable in
  `Structure/PersistedTableClassifications.cs` so the persistence structural
  test checks them.
- **Rationale**: Defence in depth: no modifying API, no runtime privilege, and
  a trigger that blocks accidental changes even by the migration role.
- **Alternatives considered**: application-only enforcement; row-level
  security.

## R13. Upload session concurrency and lifecycle

- **Decision**: `recording_upload_sessions` is the only mutable aggregate root.
  It carries `version bigint not null default 1` with
  `socalytics.attach_version_trigger('socalytics.recording_upload_sessions')`,
  returns a strong ETag, and has no edit operation. Lifecycle transitions are
  state-guarded: `pending → completed` (completion), `pending → expired`
  (worker), and the storage-release stamp on expired sessions. Grant issuance
  writes nothing to the session.
- **Rationale**: The architecture requires `version` on every mutable root;
  transitions use state guards rather than `If-Match`
  ([contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md#representation-conventions)).
- **Alternatives considered**: no `version` (violates the rule); `If-Match` on
  transitions (contradicts the lifecycle rule).

## R14. Authorization, archived Seasons, and audit

- **Decision**: Every handler resolves the route Match with
  `IAccessAuthorizer.AuthorizeTeamResourceAsync(TeamOwnedResource(match, matchId), permission)`
  using the canonical `TeamPermission.Write` (Club Admin or Coach of the Team)
  for start, grants, completion, mapping revision, finalization, and upload
  session reads, and `TeamPermission.Read` (adds Viewer) for lineage reads.
  This is the FR-003 extension: `Write` on a Team conveys recording-management
  authority for that Team only. The authorizer resolves the Team through
  `ITeamScopeResolver` (the `match` scope source registered by Club and
  Identity), records denials through its built-in
  `IAuditTrail.RecordIndependentAsync`, and maps a missing Match to not found.
  The returned `TeamScope.SeasonState` rejects mutations for archived Seasons
  (`409 season-archived`) and is re-checked inside the unit of work. Success
  audit events use `IAuditTrail.RecordAsync` in the same unit of work and
  record the resource, the Match's `team_id`, and `details.matchId` (the audit
  table has no Match column); the
  expiry worker records system events with the correlation from
  `IRequestContext`. Handlers return `OperationResult<T>` with
  `OperationFailure` mapped to problem details by the API.
- **Rationale**: Reuses the canonical authorization, scope, audit, and result
  types instead of a parallel mechanism.
- **Alternatives considered**: reading Club tables directly (bypasses the
  resolver); new team permissions (Club and Identity's `Write` already matches
  the required role set).

## R15. Object key layout, configuration, credentials, and bucket

- **Decision**: Keys are
  `{prefix}matches/{matchId}/upload-sessions/{uploadSessionId}` in one bucket
  per stamp (`prefix` from `Recordings:Upload:KeyPrefix`, default
  `recordings/`); keys and multipart upload ids are never exposed by the API.
  Configuration:
  - `ObjectStorage`: `ServiceUrl`, `Region`, `AccessKey`, `SecretKey`,
    `Bucket`, `ForcePathStyle` (default `true`), `EnsureBucketOnStartup`
    (default `false`).
  - `Recordings:Upload`: `SessionLifetime`, `GrantLifetime`,
    `ExpirySweepInterval`, `MaxObjectSizeBytes`, `MinPartSizeBytes`,
    `MaxPartSizeBytes`, `MaxPartCount`, `MaxGrantsPerRequest`,
    `AllowedContentTypes`, `KeyPrefix`.
  - `Recordings:Sets`: `MaxMembers` (default 100, the spec's default
    maximum; validated to `1 … 1000`, the structural ceiling shared by the
    database check, the OpenAPI request, and the event schema).

  All values without a stated default are required and validated at startup
  against the storage limits; the AppHost and tests supply explicit development
  values. The bucket is created by the API's development-only startup step when
  `EnsureBucketOnStartup = true` (set only by the AppHost) and by the
  integration-test fixture; production provisioning is deferred. The runtime
  credential needs `s3:PutObject` (initiate, presigned parts, complete),
  `s3:ListMultipartUploadParts`, `s3:AbortMultipartUpload`, `s3:GetObject`
  (`HeadObject`), and `s3:DeleteObject` on the prefix, plus `s3:ListBucket`
  conditioned to the prefix so missing objects report `404`;
  `s3:CreateBucket` only in development.
- **Rationale**: One key per session gives the single platform-chosen location
  of FR-008. The platform spike recommends bucket creation by the API or test
  setup, which keeps the AppHost free of the S3 SDK.
- **Alternatives considered**: content-addressed keys (would collapse distinct
  uploads); AppHost-side bucket creation (adds the SDK to the AppHost).

## R16. Store conformance probe and large-upload evidence

- **Decision**: `ObjectStoreConformanceTests` in Integration.Tests runs a
  reduced version of the storage spike against the configured store (the
  RustFS Testcontainer by default, or an external store named by
  `SOCALYTICS_CONFORMANCE_S3_*` environment variables): composite initiation;
  signed part checksum and length enforcement (BadDigest, length, part-number
  tamper, missing header); `ListParts` ETags and sizes; completion with the
  composite; `HeadObject` composite, type, and length; abort. A store must pass
  it before a deployment profile adopts it. `LargeRecordingUploadTests`
  uploads a recording slightly above 5 GiB (64 MiB parts, generated
  deterministically and streamed, never held in memory) through the API and
  RustFS to evidence SC-010. Because it needs about 12 GB of free Docker disk
  and several minutes, it runs only when the environment variable
  `SOCALYTICS_RUN_LARGE_UPLOAD_TEST` is `true` and otherwise reports as
  skipped with that reason, so the per-task full suite stays within the
  runner's disk and time budget. This feature owns the evidence: the task
  that adds the test and the final validation task run it explicitly with the
  variable set.
- **Rationale**: AWS documents composite SHA-256 for multipart uploads but not
  presigned `UploadPart` with a signed checksum explicitly; the probe turns the
  store contract into executable evidence for every store and catches
  regressions in the pinned image.
- **Alternatives considered**: a startup probe in the API (writes test objects
  in production storage at every start); no probe (silent incompatibility).

## R17. Observability and secret hygiene (FR-029, SC-008)

- **Decision**: No request or response body logging on recording endpoints;
  grant responses carry `Cache-Control: no-store`; AWS SDK logging stays off;
  recording endpoints cap request bodies at 1 MiB (a 10 000-part declaration is
  about 0.75 MiB) and accept only JSON. An integration test captures logs and
  scans the database for `X-Amz-Signature`, `X-Amz-Credential`, and the storage
  secret key.
- **Rationale**: Presigned URLs embed a signature and credential scope; the
  body cap is executable evidence that the API cannot receive media (FR-010,
  SC-001).
- **Alternatives considered**: redaction middleware (unnecessary when URLs are
  never logged).

## R18. Package versions and environment

- **Decision**: Add `AWSSDK.S3` 4.0.103.4 (Infrastructure), `Testcontainers`
  4.15.0 (Integration.Tests), and `JsonSchema.Net` 8.0.5 (Contracts.Tests) to
  `src/platform/Directory.Packages.props`, each only if no entry for that
  package exists yet (duplicates fail the build); the AppHost needs no new package.
  Directly referenced `Microsoft.Extensions.*` packages follow the central
  10.0.12 floor set by the persistence foundation.
- **Rationale**: Versions verified through the configured package feed;
  `JsonSchema.Net` 8.0.5 is the last MIT release (9.x is under a different
  license). `environment-setup` provides the .NET SDK and Docker is on the
  runner. `environment-verify` would report changes under the new
  repository-root `contracts/` folder as uncovered, so this feature depends on
  `specs/20261007-115855-environment-verification-coverage`, which must be
  merged first.
- **Alternatives considered**: none needed.

## R19. Repository-root contracts, `$id` convention, and validator

- **Decision**: This feature creates the repository-root `contracts/` folder
  with its first artifact,
  `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`,
  and the index `contracts/README.md` listing, per artifact, its path, owner
  (Recordings / control plane), exact version, example location, and validation
  command. Schemas follow `contracts/<area>/<name>/v<major>/<name>.schema.json`,
  or, for the shared definitions named in
  [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md),
  `contracts/common/v<major>/common.schema.json` (area `common`, no name
  segment), with `$id` = `https://socalytics.invalid/` + the repository path
  (for example
  `https://socalytics.invalid/contracts/<area>/<name>/v<major>/<name>.schema.json`)
  and the exact version in `x-socalytics-version`; the event payload repeats it
  in `contractVersion`. Shared definitions stay local until Durable Analysis
  adds its shared schemas. This feature owns the bootstrap: it creates the
  folder, the index, and the test project if they are missing and otherwise
  extends them, never overwriting existing files or index rows; Analyst
  Manager Registration and Durable Analysis only add schemas, index rows, and
  fixture tests. The contract command is
  `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`, a new
  xUnit v3 + Shouldly project using `JsonSchema.Net` 8.0.5 (central package
  version added only if absent) with three shared pieces:
  - `ContractCatalog` (the only discovery code; namespace
    `SocAlytics.Platform.Contracts.Tests`) enumerates every
    `contracts/**/*.schema.json` linked into the test output by a csproj glob,
    excluding `**/releases/**` (immutable released copies that reuse their
    `$id`), and exposes `Schemas`, `Releases` (the released copies under
    `**/releases/**`, each linked to its current schema, so later features
    never enumerate files themselves), `IndexRows` (the rows of the
    `Artifact | Owner | Version | Example | Validation command` table in
    `contracts/README.md`, keyed by the first link of the Artifact cell), and
    `LoadRegistry()`, which returns one fresh offline `SchemaRegistry` (never
    the global one, no fetching) containing every catalog schema keyed by its
    `$id`.
  - `ContractIndexTests` checks that every schema has exactly one index row
    and every row names a catalog schema, that each `$id` equals
    `https://socalytics.invalid/contracts/` plus the path below `contracts/`,
    and that each path follows `contracts/<area>/<name>/v<major>/<name>.schema.json`
    or the shared-definitions form `contracts/common/v<major>/common.schema.json`
    (built in from the start so Durable Analysis need not extend the test).
  - `SchemaMetaValidationTests` validates each schema against the 2020-12
    meta-schema, checks `$schema` and that `x-socalytics-version` is a semantic
    version whose major equals the path's `v<major>`, resolves every `$ref`
    offline through `LoadRegistry()` (relative references resolved against
    the schema's `$id`; the target must be a catalog schema and its JSON
    pointer must resolve), so cross-file references such as Durable
    Analysis's shared definitions are supported, and validates every
    `examples` entry with format assertion enabled through the registry.

  Feature-specific fixture tests (for example the recordings-finalized
  negative cases) live beside them and use `ContractCatalog`. The OpenAPI
  fragment stays in this
  feature folder; the generated `/openapi/v1.json` is the published REST
  description.
- **Rationale**: [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md)
  places canonical contracts in a repository-root folder with an index and one
  deterministic command, uses major-version `$id` values, and requires offline
  resolution; the reserved `.invalid` domain (RFC 2606) is never dereferenced.
  Hosting the tests in the platform solution makes the existing `dotnet test`
  and environment-verify platform checks run them.
- **Alternatives considered**: `urn:` identifiers (path agreement not
  checkable); contracts only inside feature folders (not canonical); Node or
  Python validators (new runtime); newer `JsonSchema.Net` (non-MIT license).
