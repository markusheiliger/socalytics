# Research: Recording Lineage and Upload

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-07

This document resolves every open technical question of the plan. Each entry
records the decision, why it was chosen, and the alternatives that were
evaluated. Shared persistence, identity, and API conventions (unit of work,
version triggers, migrations, `ITeamScopeResolver`, `IAuditTrail`, problem
details, `Idempotency-Key`) are adopted from the sibling foundation plans and
are not re-decided here.

## R1. .NET S3 client

- **Decision**: Use `AWSSDK.S3` 4.0.103.4 (with its transitive `AWSSDK.Core`)
  in the Infrastructure layer only, configured with `ServiceURL` set to the
  stamp's S3 endpoint, `ForcePathStyle = true`, a fixed signing region
  (`us-east-1` unless configured), the request protocol derived from the
  endpoint scheme, and the flexible-checksum defaults switched to
  `RequestChecksumCalculation = WHEN_REQUIRED` and
  `ResponseChecksumValidation = WHEN_REQUIRED`. Only three operations are used:
  presigning a single-object `PutObject`, `HeadObject` with
  `ChecksumMode = ENABLED`, and (fallback evidence) `GetObjectAttributes` with
  the `Checksum` and `ObjectSize` attributes.
- **Rationale**: The AWS SDK is the reference implementation of SigV4
  presigning and of the additional-checksum headers (`x-amz-checksum-sha256`)
  that FR-011 relies on; it works against any S3-protocol endpoint with
  path-style addressing, so the platform stays implementation-neutral as the
  constitution (Principle III) and
  [terminology-and-principles.md](../../docs/architecture/terminology-and-principles.md#3-s3-as-universal-data-plane)
  require. Turning the v4 flexible-checksum defaults down to `WHEN_REQUIRED`
  avoids SDK-added CRC trailers that third-party S3 servers handle
  inconsistently; the platform requests SHA-256 explicitly where it needs it.
  Keeping the SDK out of Application and Domain (enforced by an architecture
  test) means the storage product and client remain replaceable.
- **Alternatives considered**:
  - *Minio .NET SDK*: S3-compatible, but its upstream server is now archived
    (see R2), its presign API does not bind additional-checksum headers as
    directly, and adopting it would tie the client to a product name.
  - *Hand-written SigV4 over `HttpClient`*: no dependency, but re-implements
    security-critical signing and checksum handling with no benefit.
  - *Azure Blob SDK*: not the S3 protocol; contradicts the data-plane principle.

## R2. Local S3-compatible implementation (Aspire and Testcontainers)

- **Decision**: RustFS, pinned image `rustfs/rustfs:1.0.1`, run as a plain
  Aspire container resource (`AddContainer`) in the AppHost and as a generic
  Testcontainers container in `SocAlytics.Platform.Integration.Tests`. The
  container is configured through `RUSTFS_ADDRESS=0.0.0.0:9000`,
  `RUSTFS_VOLUMES`, `RUSTFS_ACCESS_KEY`, and `RUSTFS_SECRET_KEY`, with the
  console disabled; readiness is `GET /health` on port 9000.
- **Rationale**: RustFS is the implementation the architecture names first
  ([overview.md](../../docs/architecture/overview.md),
  [tenancy-and-technology.md](../../docs/architecture/tenancy-and-technology.md)),
  is Apache-2.0 licensed, reached general availability (1.0.0 on 2026-09-16,
  1.0.1 on 2026-10-03), and fixed the SHA-256 additional-checksum gap
  (rustfs/rustfs#4341, closed 2026-07-07): a mismatched body is rejected with
  `BadDigest` and `HeadObject` with checksum mode returns the stored
  `ChecksumSHA256`. That behavior is exactly the integrity evidence FR-011
  needs. The integration suite proves it on every run, so a regression in the
  pinned image fails tests instead of silently weakening verification.
- **Alternatives considered**:
  - *MinIO community edition*: functionally suitable, but the community
    project entered maintenance mode in December 2025, the repository was
    archived in February 2026, and official `minio/minio` images are no longer
    published; pinning an unmaintained image is a supply-chain risk.
  - *`CommunityToolkit.Aspire.Hosting.RustFS` 13.5.0*: convenient, but it
    requires `Aspire.Hosting` 13.5.0 while the AppHost SDK is 13.4.6; a plain
    container resource needs no extra package and no SDK upgrade.
  - *Ceph RGW or LocalStack*: heavier containers with no benefit for a single
    bucket and three operations.
  - *A `Testcontainers.Minio` module*: exists (4.15.0) but targets MinIO; no
    RustFS module exists, and the generic `ContainerBuilder` from
    `Testcontainers` 4.15.0 needs only image, port, environment, and a health
    wait.

## R3. Upload grant shape (FR-008, FR-009, FR-010)

- **Decision**: A grant is a SigV4 presigned `PUT` URL for exactly one object
  key chosen by the platform, with `x-amz-checksum-sha256` (the base64 form of
  the declared SHA-256) and `Content-Type` included as signed headers. The API
  returns `{ method: "PUT", url, requiredHeaders, expiresAt }` with
  `Cache-Control: no-store`. The lifetime comes from required configuration
  `Recordings:Upload:GrantLifetime` (no default; tests use explicit short
  values). A grant is never persisted, logged, audited, or stored in a retry
  outcome; audit records only that a grant was issued and its expiry.
- **Rationale**: A presigned single-object `PUT` cannot list, read, delete, or
  write another key, and carries no authority for other Matches or Teams. Signing
  the checksum header binds the grant to the declared content: storage rejects
  any body whose SHA-256 differs, and any later re-use of a still-valid grant
  (including after completion) can only write byte-identical content, so an
  accepted object's content cannot be changed through its grant. Re-upload to
  the same key while the session is pending is still possible, as the spec's
  "wrong file uploaded" edge case requires.
- **Alternatives considered**:
  - *Presigned POST with a policy*: supports `content-length-range`, but
    additional-checksum enforcement through POST policies is less uniformly
    supported by S3 implementations, and POST form uploads are a different
    client contract.
  - *Short-lived STS credentials scoped to a prefix*: broader authority than
    one key and not available on every S3 implementation.
  - *Unsigned checksum (client may send any checksum)*: the stored object
    could then differ from the declaration until completion; binding is
    stronger at no cost.
- **Known limit**: a presigned `PUT` cannot bound the body size; size is
  verified at completion (R4), and oversize objects never become lineage.
  Storage abuse up to grant expiry is bounded only by the grant lifetime and is
  recorded under open risks (THR-003 remains Open / Blocking).

## R4. Integrity verification on completion (FR-011, FR-014)

- **Decision**: Completion calls `HeadObject` with `ChecksumMode = ENABLED` on
  the session's key and accepts the object only when: the object exists; its
  `ContentLength` equals the declared size; `ChecksumSHA256` is present,
  decodes to 32 bytes, and its lowercase hex equals the declared
  `sha-256:<hex>`; and the checksum type, when reported, is `FULL_OBJECT` (a
  composite multipart checksum is rejected). If `HeadObject` returns no SHA-256
  checksum, the adapter tries `GetObjectAttributes` once; if neither yields a
  full-object SHA-256, completion fails closed with
  `integrity-evidence-unavailable`. The storage `ETag` and, when the bucket
  reports one, the version identifier are recorded as non-authoritative
  evidence on the recording version. Storage I/O happens outside the database
  transaction; the transaction then re-checks the session state.
- **Rationale**: Storage-computed checksums verify the stored bytes without the
  control plane reading media (FR-010, SC-001). The S3 `ETag` is not a content
  digest (it is MD5 or multipart-derived and implementation-specific), so it
  cannot satisfy FR-011. Failing closed when evidence is missing satisfies the
  spec's "storage cannot attest integrity" edge case.
- **Alternatives considered**:
  - *Trust the client's declaration*: forbidden by FR-011.
  - *Stream the object through the API to hash it*: violates FR-010 and the
    control-plane principle.
  - *Compare `ETag` with an MD5 declaration*: not a SHA-256 digest, not
    reliable across implementations or multipart uploads.

## R5. Status codes for verification and dependency failures

- **Decision**: Missing object, size mismatch, digest mismatch, and missing
  integrity evidence are `409 Conflict` with distinct problem codes
  (`upload-object-missing`, `upload-object-mismatch`,
  `integrity-evidence-unavailable`); the session stays `pending`. Storage
  unreachable, timeouts, or 5xx from storage are `503` with code
  `object-storage-unavailable` and change no state.
- **Rationale**: The shared status mapping reserves 409 for state-rule
  conflicts and 503 for unavailable dependencies; a verification failure is a
  conflict between the stored object state and the session declaration and is
  retryable after correction (FR-026), whereas an outage is a dependency
  failure.
- **Alternatives considered**: `422 Unprocessable Content` (not in the shared
  mapping); `400` (the request itself is valid).

## R6. Timeline mapping representation and digest

- **Decision**: A timeline mapping is an ordered list of 1 to 64 spans. Each
  span maps the media interval `[mediaStartSeconds, mediaEndSeconds)` to match
  time starting at `matchStartSeconds`, at a 1:1 rate. Values are nonnegative
  numeric seconds with at most millisecond precision. Spans must have positive
  length, be strictly increasing and non-overlapping in media time, and be
  strictly increasing and non-overlapping in match time. Internally values are
  integer milliseconds. The mapping digest is
  `sha-256:<hex>` over the canonical JSON
  `{"spans":[{"matchStartMilliseconds":…,"mediaEndMilliseconds":…,"mediaStartMilliseconds":…}],"version":1}`
  (UTF-8, lexicographically ordered properties, no insignificant whitespace,
  integers only, spans in media order).
- **Rationale**: Spans express a recording that starts mid-match, recordings
  that cover both halves with the break cut out, and multi-camera offsets,
  without frame-rate or codec knowledge the control plane cannot obtain without
  reading media. Integer milliseconds make the canonical form free of
  floating-point ambiguity, so the digest is deterministic and stable. The
  digest covers content only, so it is the "timeline-mapping digest" of the
  materialized-segment identity in
  [match-data-pipeline.md](../../docs/architecture/match-data-pipeline.md#segment-contract),
  and equal content on the same recording version deduplicates (FR-016).
- **Alternatives considered**:
  - *Single offset*: cannot express cut breaks or multiple periods.
  - *Arbitrary piecewise-linear anchors with rates*: variable rates are not
    justified by any requirement and complicate segment coverage.
  - *Frame-based mapping*: requires frame rate, which only media inspection can
    provide.
  - *RFC 8785 library for canonicalization*: the mapping contains only
    integers, so a small deterministic writer suffices without a dependency.

## R7. Retry keys and replay (FR-025, FR-026)

- **Decision**: The `Idempotency-Key` header (1–255 visible ASCII characters)
  is required on start upload, complete upload, revise timeline mapping, and
  finalize. One append-only table stores
  `(operation, match_id, idempotency_key)` uniquely, together with the request
  digest (`sha-256` over the canonical JSON of operation, route targets, and
  normalized body), the success status, and the identities of the resources
  produced. The row is inserted in the same unit of work as the change. On a
  repeat, the handler re-authorizes first, then compares digests: equal →
  replay by re-reading the referenced immutable resources; different → `409`
  `idempotency-key-reused`. A concurrent duplicate blocks on the unique index
  and, after the winner commits, receives a unique violation; the handler rolls
  back and replays. Replay of start upload returns the original session with a
  freshly issued grant, because grants are never stored.
- **Rationale**: Storing identities rather than response bodies keeps secrets
  out of durable records (FR-029) and lets replays always reflect the same
  immutable data. The unique index makes duplicates safe under concurrency
  (SC-006) without application locks. Failed requests never insert the row
  (FR-026), because the row commits only with the change.
- **Alternatives considered**:
  - *Store the full response*: would persist upload grants.
  - *Advisory locks per key*: extra mechanism; the unique index already
    serializes.
  - *A shared platform idempotency table*: none is defined by the persistence
    foundation; a Recordings-owned table matches
    [match-data-pipeline.md](../../docs/architecture/match-data-pipeline.md#planned-recording-lineage-foundation)
    ("scoped idempotency outcomes"). A later shared mechanism can absorb it.

## R8. Recordings-finalized event record (FR-022, FR-023, FR-024)

- **Decision**: Finalization inserts one row into the Recordings-owned,
  append-only table `recording_finalized_events` in the same transaction as the
  set version, its memberships, the retry outcome, and the audit event. The row
  holds the event id, event type `matches.recordings-finalized`, contract
  version `1.0.0`, recording-set-version id (unique), Match, Team, occurrence
  time, and the canonical payload (JSON Schema
  [recordings-finalized.schema.json](contracts/schemas/recordings/recordings-finalized/v1/recordings-finalized.schema.json),
  published as `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`;
  see R15). No publication state lives on this row, and this feature makes no
  outbox call. Durable Analysis introduces the outbox and NATS publisher, adds
  the `IOutbox` call to this feature's finalize handler, and backfills outbox
  entries for records finalized before it merged; it records publication state
  in its own structure keyed by `event_id`.
- **Rationale**: The unique constraint on the recording-set version enforces
  exactly one record (FR-024) at the database level. Keeping the row immutable
  satisfies "its event record remains unchanged" (User Story 3) and lets the
  immutability trigger cover it. Separating publication state avoids an
  `UPDATE` on an evidence record and keeps transport out of this feature's
  scope.
- **Alternatives considered**:
  - *A generic `outbox` table with `published_at`*: the publisher, retry,
    and cleanup semantics belong to Durable Analysis; defining them here would
    be speculative and would make the evidence row mutable.
  - *Publishing to NATS now*: out of scope per the spec's assumptions.

## R9. Immutability enforcement (FR-015, FR-021)

- **Decision**: Recording versions, timeline mappings, recording-set versions,
  set memberships, finalized-event records, and retry outcomes have no
  `version` column. A shared trigger function
  `socalytics.reject_immutable_change()` is attached `BEFORE UPDATE OR DELETE`
  to each of these tables and raises an error, and the migration revokes
  `UPDATE` and `DELETE` on them from `socalytics_app`. No Application command
  or endpoint offers modification or deletion.
- **Rationale**: Defence in depth: the API has no modifying operation, the
  runtime role lacks the privilege, and a trigger blocks accidental changes even
  by the migration role. The persistence structural test (version triggers
  only on versioned tables) is unaffected.
- **Alternatives considered**: application-only enforcement (insufficient
  evidence for User Story 3 scenario 4); row-level security (unnecessary
  complexity).

## R10. Upload session concurrency

- **Decision**: `recording_upload_sessions` is the only mutable aggregate root
  of this feature. It carries `version bigint not null default 1` with the
  shared `socalytics.advance_version()` trigger, its representation returns a
  strong ETag, and it has no edit operation. Completion is a lifecycle
  transition guarded by `WHERE id = @Id AND state = 'pending'`; zero rows means
  the session completed concurrently and the handler resolves the outcome
  through the retry record (replay or `409 upload-session-completed`). Grant
  issuance writes nothing to the session.
- **Rationale**: The architecture requires a `version` on every mutable
  aggregate root, and the persistence structural test expects the trigger.
  Lifecycle transitions use state guards, not `If-Match`, per
  [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md#representation-conventions).
- **Alternatives considered**: no `version` column (violates the architecture
  rule for mutable roots); requiring `If-Match` on completion (contradicts the
  lifecycle-transition rule).

## R11. Authorization, archived Seasons, and audit

- **Decision**: Every handler first resolves the Match through
  `ITeamScopeResolver` (Club and Identity) to its Team and Season state,
  inside the command's unit of work. Recording management
  (`recordings:manage`) requires an active member who is a Club Admin or holds
  Coach on that Team; lineage reads (`recordings:read`) also allow Viewer.
  Missing Match → 404; unresolvable scope, membership, or role → 403; archived
  Season → `409 season-archived` for mutations only. Every listed action and
  every denial is recorded through `IAuditTrail` in the same unit of work as
  the change; a denial's audit event is committed in its own unit of work
  because the request changes nothing else.
- **Rationale**: Implements FR-001 to FR-006 and FR-030 with the shared
  capabilities instead of a parallel mechanism; the recording permissions are
  the FR-003 extension of team roles.
- **Alternatives considered**: reading Club tables directly from Recordings
  SQL (couples areas and bypasses the resolver); `404` for cross-team Matches
  (the spec requires forbidden).

## R12. Object key layout and storage configuration

- **Decision**: Keys are
  `{prefix}matches/{matchId}/upload-sessions/{uploadSessionId}` in one
  configured bucket per stamp, where `prefix` comes from
  `Recordings:Upload:KeyPrefix` (default `recordings/`). Keys are not exposed
  in the API. Configuration sections: `ObjectStorage` (`ServiceUrl`,
  `Region`, `AccessKey`, `SecretKey`, `Bucket`, `ForcePathStyle`) and
  `Recordings:Upload` (`GrantLifetime`, `MaxObjectSizeBytes` ≤ 5 GiB,
  `AllowedContentTypes`, `KeyPrefix`). All values except `KeyPrefix` and
  `ForcePathStyle` are required and validated at startup; locally the AppHost
  supplies development values and generated secret parameters. The bucket is
  created locally by the AppHost after the container is healthy and in tests by
  the fixture; the API never creates buckets.
- **Rationale**: One key per session gives the "single platform-chosen
  location" of FR-008 and keeps re-uploads to the same key. The API's storage
  credential needs only `s3:PutObject` on the prefix (the authority a presigned
  URL inherits, narrowed to one key), `s3:GetObject` and
  `s3:GetObjectAttributes` on the prefix (required by `HeadObject` and the
  fallback attributes call), and `s3:ListBucket` conditioned to the prefix so
  that a missing object is reported as `404` rather than an ambiguous `403`.
  Bucket administration stays outside the runtime. The 5 GiB cap is the S3
  single-`PUT` limit; multipart is out of scope.
- **Storage unavailability (edge case "Object storage unavailable")**:
  presigning is a local computation and would succeed while storage is down,
  so start upload and grant issuance first perform a reachability probe — a
  `HeadObject` on the session's key, where `200` or `404` both prove
  reachability — before any state change. Network failures, timeouts, and
  storage `5xx` map to `503 object-storage-unavailable`.
- **Alternatives considered**: content-addressed keys (would collapse distinct
  uploads and leak digests into layout); API-side bucket creation (needs
  administrative storage authority at runtime).

## R13. Observability and secret hygiene (FR-029, SC-008)

- **Decision**: No request or response body logging on recording endpoints;
  grants are returned with `Cache-Control: no-store`; AWS SDK logging stays
  disabled; recording endpoints cap request bodies at 1 MiB. An integration
  test captures logs and scans the database (sessions, versions, mappings,
  sets, retry outcomes, events, audit) for `X-Amz-Signature`,
  `X-Amz-Credential`, and the storage secret key.
- **Rationale**: Presigned URLs embed a signature and credential scope; they
  must not reach durable or telemetry sinks. The body cap is executable
  evidence that the API cannot receive media (FR-010, SC-001).
- **Alternatives considered**: redaction middleware (unnecessary when the URL
  is never logged in the first place).

## R14. Package versions and environment

- **Decision**: Add `AWSSDK.S3` 4.0.103.4 (Infrastructure and AppHost),
  `Testcontainers` 4.15.0 (Integration.Tests), and `JsonSchema.Net` 8.0.5
  (Contracts.Tests) to `src/platform/Directory.Packages.props`. No other
  dependency is added.
- **Rationale**: `AWSSDK.S3` and `Testcontainers` versions were verified
  through the configured package feed on 2026-10-07; `Testcontainers` matches
  the `Testcontainers.PostgreSql` 4.15.0 version the persistence plan adopts.
  `JsonSchema.Net` 8.0.5 is the coordinator-selected validator (R15).
  `environment-setup` provides the .NET SDK from `global.json`, and Docker is
  present on the runner for Testcontainers. Today `environment-verify` covers
  `src/platform/` and Markdown but would report changes under the new
  repository-root `contracts/` folder as uncovered, so this feature depends on
  the environment feature
  `specs/20261007-115855-environment-verification-coverage`, which runs the
  platform checks (including the contract tests) for `contracts/` changes and
  must be merged first.
- **Alternatives considered**: none needed.

## R15. Repository-root contracts, `$id` convention, and validator

- **Decision**: This feature creates the repository-root `contracts/` folder
  with its first artifact,
  `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`,
  and the index `contracts/README.md` listing, per artifact, its path, owner
  (Recordings / control plane), exact version, example location, and the
  validation command. Schemas follow
  `contracts/<area>/<name>/v<major>/<name>.schema.json` with
  `$id` = `https://socalytics.invalid/contracts/<area>/<name>/v<major>/<name>.schema.json`
  and the exact semantic version in `x-socalytics-version`; the event payload
  repeats it in `contractVersion`. Shared definitions are kept local to the
  schema until Durable Analysis adds `contracts/common/v1`. The contract
  command is `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`:
  a new xUnit v3 + Shouldly test project in the platform solution that uses
  `JsonSchema.Net` 8.0.5 to (1) discover every `*.schema.json` under
  `contracts/` (linked into the test output by a csproj glob), (2) validate it
  against the 2020-12 meta-schema, (3) check that its `$id` matches its path
  under the convention and that `x-socalytics-version` is a semantic version
  whose major equals the path's `v<major>`, (4) validate every entry of its
  `examples` against it with format assertion enabled, and (5) check that
  every schema is listed in `contracts/README.md`. The OpenAPI fragment stays
  in this feature folder as plan evidence; the generated `/openapi/v1.json`
  remains the published REST description.
- **Rationale**: [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md)
  places canonical contracts in a repository-root `contracts/` folder with an
  index and one deterministic contract command, uses a stable major-version
  `$id`, and requires offline reference resolution; the reserved `.invalid`
  top-level domain (RFC 2606) guarantees an `$id` is never dereferenced.
  `JsonSchema.Net` 8.0.5 is the last MIT-licensed line, supports draft
  2020-12 including the meta-schema and format assertion, and runs inside the
  existing .NET test tooling, so no new runtime is needed. Hosting the tests
  in the platform solution makes the existing `dotnet test` command and the
  environment-verify platform checks run them.
- **Alternatives considered**:
  - *`urn:` `$id` values*: valid but do not mirror the folder layout, so
    path/`$id` agreement cannot be checked mechanically.
  - *Contracts inside the feature folder only*: not canonical and not
    discoverable by later features.
  - *Node or Python validators (ajv, jsonschema)*: would add a runtime to the
    verification path before any product code needs it.
  - *Newer `JsonSchema.Net` releases*: outside the MIT line selected by the
    coordinator.
