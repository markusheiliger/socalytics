# Research: Materialized Video Segments and Analyst Input Staging

All Technical Context unknowns are resolved below. Each decision lists its rationale and the alternatives considered.

## R1 - Host of the Segment Service

**Decision**: The Segment Service is a `Segments` functional area inside the existing layers (Domain value objects and
rules, Application handlers and ports, Infrastructure persistence, toolchain runner, and object-storage adapter) and
runs as a hosted background worker in the API host, like the existing analysis workers under
`SocAlytics.Platform.Infrastructure/Workers/`. No new project, deployment unit, or HTTP operation handles media bytes.

**Rationale**: [Platform Implementation Profile](../../../docs/architecture/platform-implementation.md#data-organization)
already decides that segment materialization runs on demand within the control-plane deployment until measured
scaling justifies extraction, and the platform is one layered monolith whose functional areas are folders. The worker
reads source media from and writes segment media to object storage directly; no API operation reads or relays the
bytes, which keeps constitution Principle III.

**Alternatives considered**: A separate Segment Service project or container (rejected: an extraction the
architecture defers until measured evidence); running extraction in the Analyst Manager (rejected: the architecture
forbids it and Managers have no source-recording access); materializing inside the request path of run creation
(rejected: long media work inside a database transaction and request).

## R2 - Durable materialization state and concurrency

**Decision**: One versioned table row per materialized identity (unique over the four identity columns) holds the
state `absent | materializing | available | failed`, the bound policy version and duration, the current association
(media and manifest object keys, media digest and size, manifest checksum), the in-flight candidate identity, a lease
owner and expiry, attempts used, the sanitized failure reason, and timestamps. Requests upsert the row with
`INSERT … ON CONFLICT DO NOTHING`; a worker claims work with `SELECT … FOR UPDATE SKIP LOCKED` on rows that are
`absent` with demand, `materializing` with an expired lease, or due for retry, sets `materializing`, a new candidate
identity, and a lease, and commits before any media work. Publication is a compare-and-set on the row's candidate
identity and state inside a unit of work: only the candidate that still owns the lease publishes; a losing or expired
candidate deletes its own objects. The row's `version` trigger (`socalytics.attach_version_trigger`) gives optimistic
concurrency for purge.

**Rationale**: Satisfies "at most one current association, every successful requester receives it" across workers and
platform instances using the existing PostgreSQL unit-of-work and lease patterns (fenced attempts, outbox leases), and
survives restarts: an expired `materializing` lease is reclaimed, and its candidate is cleaned up by key.

**Alternatives considered**: Object-storage conditional writes as the only coordination (rejected: S3-compatible
stores differ in conditional-write support, and readiness needs queryable state); NATS work queue for materialization
(rejected: NATS is not a state store in this architecture; database polling with a short interval suffices in
development); advisory locks without a state row (rejected: no durable state for readiness and purge).

## R3 - Extraction with the pinned toolchain

**Decision**: Segmentation policy version 1 uses stream copy, no transcoding: `ffmpeg` reads the source recording
through a short-lived presigned GET URL issued by the platform's own object-storage adapter, seeks with input seeking
to the requested source interval, keeps source timestamps (`-copyts`), maps only the first video stream
(`-map 0:v:0`), drops metadata and other streams, writes an MP4 (`video/mp4`) with `+faststart` and bit-exact flags to
a private working directory, and stops at the first packet at or after the requested end. `ffprobe` then reads the
written file's first and last video packet timestamps; the encoded boundaries are those timestamps in source media
milliseconds. Materialization fails non-retryably (`segment-coverage-incomplete`) when the encoded interval does not
cover the requested interval, or the first video packet is not a keyframe. Executable paths and an expected version
prefix are configured (`Segments:Toolchain`); the worker checks `ffmpeg -version` and `ffprobe -version` once per
start and fails materializations with `toolchain-unavailable` (non-retryable) when they are missing or differ.

**Rationale**: Stream copy starting at the preceding keyframe yields independently decodable media that covers the
requested interval with keyframe context, needs no encoder (the environment's LGPL build has no H.264 encoder) and no
GPU, and is fast. Keeping source timestamps makes the encoded boundaries directly comparable with the requested source
interval. Reading through a presigned URL uses HTTP range requests instead of downloading whole recordings; the URL
never leaves the platform process and is not logged. The environment feature pins the toolchain in verification; the
expected version prefix keeps a differently versioned local install from silently producing evidence.

**Alternatives considered**: Re-encoding to a fixed profile (rejected: needs an encoder the LGPL build lacks, costs
CPU, and is beyond policy v1); output-side seeking (rejected: decodes the whole prefix); HLS or fragmented output
(rejected: more objects per segment without benefit for one Analyst read); downloading the full source first
(rejected: unbounded local storage per recording); keeping audio (rejected by clarification).

**Open item recorded for implementation**: storage endpoints that require TLS need the build's HTTPS protocol support;
the development store (RustFS in the AppHost and tests) uses HTTP. Production endpoints remain unresolved.

## R4 - Requested interval, coverage, and manifest time mapping

**Decision**: For segment `n` and duration `D`, the nominal window is `[(n-1)·D, n·D)` in match milliseconds. The mapped
spans of the identity's timeline mapping are intersected with the window; the requested source interval is the smallest
contiguous media interval `[min media start, max media end)` covering all non-empty intersections, translated through
each span's 1:1 rate. An empty intersection rejects the request (`segment-coverage-empty`). The manifest lists each
intersected span as `{ mediaStartMs, mediaEndMs, matchStartMs }` so Analysts convert media time to match time and
know which media time is unmapped.

**Rationale**: Implements the [Segment Contract](../../../docs/architecture/match-data-pipeline.md#segment-contract)
exactly, keeps one media artifact per identity, and gives Analysts the information they need to emit match-time facts
inside the nominal window. It agrees with the existing `LineageAnalysisSegmentSource` numbering.

**Alternatives considered**: One artifact per mapped span (rejected: multiplies objects and complicates identity);
omitting spans from the manifest (rejected: Analysts could not convert media time without the mapping).

## R5 - Segmentation policy binding

**Decision**: The Segment Service implements media profile version 1 for segmentation policy version 1 with any
positive integer duration. Each materialized row binds its policy digest to the policy version and duration of the
first validated request; a later request with the same digest but a different version or duration is rejected
(`segmentation-policy-conflict`), and an unsupported version is rejected (`segmentation-policy-unknown`). Run snapshots
remain the source of the policy; the manifest records version, duration, and digest.

**Rationale**: The Registry accepts the policy digest from workflow authors and existing Development and Test seeds use
durations other than 5 minutes; binding the digest on first use detects inconsistent definitions without inventing a
policy catalog. Production keeps the architecture's 5-minute policy v1 through its workflow definitions.

**Alternatives considered**: A Registry-owned policy catalog (rejected: out of scope); accepting any digest without
binding (rejected: two durations under one digest would alias identities).

## R6 - Readiness integration

**Decision**: Run creation (`HandleRecordingsFinalizedNotification`) records, for every segment-scoped job whose node
snapshot's video policy is `required` or `optional`, a materialization demand for its identity in the same unit of
work (row upsert). A rejected request changes no row; the job records the rejection reason, and readiness treats it
like a `failed` segment for that job only. The node snapshot gains the
pinned profile's `videoPolicy`. `RunReadinessCoordinator` treats such a job as not ready while its row is `absent` or
`materializing`; when the row is `available` it pins `{ manifestChecksum, mediaDigest, mediaSizeBytes,
manifestReference }` into the job's input snapshot and emits analyst-job 1.2.0 with `segmentMedia`; when `failed`, a
`required` job transitions to `Failed` with reason `segment-unavailable` and normal blocking follows, while an
`optional` job becomes ready without `segmentMedia`. Publication or failure of a row re-applies readiness for every run
with a waiting job on that identity in the same unit of work; the existing reconciliation worker re-applies it after
restarts. Retries of an attempt reuse the pinned association.

**Rationale**: Matches step 1 of [Readiness And Dependencies](../../../docs/architecture/job-processing.md#readiness-and-dependencies)
and the clarifications, reuses the input snapshot as lineage, and keeps NATS out of state decisions.

**Alternatives considered**: Materializing only when a job's other dependencies are satisfied (rejected: delays the
first segment jobs needlessly; segment jobs normally have no upstream); a separate job state `AwaitingMedia`
(rejected: `Waiting` already means not ready, and the run status remains readable).

## R7 - Manager-mediated media read and grants

**Decision**: New Manager operation `GET /api/v1/analysis/attempts/{attemptId}/segment-media?fencingToken=…`
(operation `getAnalysisSegmentMedia`) with the existing DPoP Manager authentication and the same attempt, lease,
fencing, ownership, and audit checks as `listAnalysisUpstreamResults`. It returns the pinned association, the segment
manifest document, and two presigned GET grants (manifest and media) that expire at the earlier of the configured
grant lifetime and the attempt's lease expiry. Before issuing grants it confirms the media object's attested full-object
SHA-256 and size equal the pinned values; otherwise it refuses with `segment-media-invalid`. A job without pinned media
returns `segment-media-not-applicable`. Response schema: new `contracts/analysis/segment-media/v1/` 1.0.0.

**Rationale**: Identical security posture to upstream reads; per-object, short-lived, read-only grants; video bytes
never pass through the API.

**Alternatives considered**: Grants embedded in the job message (rejected: messages are durable and replayable; grants
must be attempt-fenced); presigned access from the container (rejected: Analysts have no network).

## R8 - Container interface: media mount

**Decision**: The Manager stages segment media into `{attemptDirectory}/media/` and mounts it read-only at
`/socalytics/media/` with exactly two files: `segment-manifest.json` (byte-identical to the stored manifest) and
`segment.mp4`. The directory is absent for jobs without `segmentMedia`. Staging verifies the manifest's SHA-256 against
the job's `segmentMedia.manifestChecksum`, validates it against the segment manifest schema, verifies the media's
digest and size against the manifest, and counts media bytes against `Work:Staging:MaxBytesPerAttempt` together with
upstream inputs. Failures map to the existing staging categories (`staging-digest-mismatch`, `staging-limit-exceeded`,
`staging-disk-full`, `staging-storage-unavailable`, `staging-read-refused`). Cleanup is the existing attempt-directory
deletion. The simulated runtime exposes the same directory.

**Rationale**: A separate mount cannot collide with capability identifiers under `/socalytics/inputs/`; reusing staging
categories and bounds keeps the Manager's failure reporting unchanged.

**Alternatives considered**: `/socalytics/inputs/media/` (rejected: collides with a capability named `media`); a
separate staging bound for media (rejected: one per-attempt bound is simpler and already configurable).

## R9 - Contracts and versions

**Decision**: New `contracts/segments/segment-manifest/v1/segment-manifest.schema.json` 1.0.0 (owner: Segment Service;
consumers: Analyst Manager, Analyst SDK) and `contracts/analysis/segment-media/v1/segment-media.schema.json` 1.0.0
(owner: Scheduler and Job Registry; consumer: Analyst Manager); additive `analysis/analyst-job` 1.2.0 on top of the
Python Analyst SDK feature's 1.1.0 with optional `segmentMedia`, allowed only for segment-scoped jobs. Each has a
released copy, valid and invalid examples with expectations, and an index row. The result manifest is unchanged.

**Rationale**: [Contracts and Compatibility](../../../docs/architecture/contracts-and-compatibility.md) assigns
logical-segment manifests to the Segment Service and requires additive minor versions within a major version.

**Alternatives considered**: Placing the manifest under `contracts/recordings/` (rejected: the Segment Service, not the
Recordings area, owns it).

## R10 - Purge and listing

**Decision**: Member operations under `MapMemberApi()`: `GET /api/v1/matches/{matchId}/materialized-segments`
(`listMaterializedSegments`) and `POST /api/v1/matches/{matchId}/materialized-segments/purge`
(`purgeMaterializedSegments`), authorized with `IAccessAuthorizer.AuthorizeClubAsync` for the Club Admin permission
of the match's club (resolved through the Club application handlers); other members get `404` for matches they cannot
see and `403` otherwise, following existing member endpoints. The listing covers rows whose recording version belongs
to one of the match's finalized recording sets. Purge locks the rows, skips rows that are `materializing` or pinned or awaited by a
`Waiting`, `Ready`, or
`Claimed` job of a non-terminal run, deletes the objects of `available` and `failed` rows, sets them to `absent` without
demand, writes one security audit event, and returns purged and in-use counts; repeating it is idempotent. No `If-Match`
is required because purge never changes logical identity and is safe to repeat.

**Rationale**: Matches the clarified per-match scope and the architecture's manual purge, keeps in-flight lineage
intact, and reuses the member API conventions.

**Alternatives considered**: Purging in-use rows and rematerializing for pinned jobs (rejected: stream copy is
deterministic in practice but not guaranteed, so a pinned checksum could stop matching); a Web UI (out of scope).

## R11 - Tests and synthetic media

**Decision**: Tests generate a short synthetic recording at test time with the pinned `ffmpeg` (`testsrc2`, native
MPEG-4, 64×64, keyframe every second, about 10 seconds) and upload it through the existing recording upload flow;
segment tests use a 5-second test policy. Integration tests (PostgreSQL and RustFS containers, Docker required) cover
the Segment Service, readiness, the media read, purge, and authorization; toolchain-dependent tests fail visibly when
the toolchain is missing, as Docker-dependent tests do. Manager tests use a fake platform client and fixture files;
Docker end-to-end tests add a `media` mode to the test Analyst image with a `required` video policy. SDK tests use
fixture mounts.

**Rationale**: No binary fixtures in the repository, identical tooling in verification and local runs, and evidence
for every requirement.

**Alternatives considered**: A committed binary fixture (rejected: opaque and unnecessary); mocking the toolchain in
integration tests (kept only for failure injection in unit tests).

## R12 - Configuration

**Decision**: New options section `Segments` with `Materialization` (`PollInterval`, `LeaseDuration`,
`MaxConcurrentPerInstance`, `MaxAttempts`, `RetryDelay`, `ExecutionTimeout`, `MaxSegmentBytes`, `WorkingDirectory`)
and `Toolchain` (`FfmpegPath`, `FfprobePath`, `ExpectedVersionPrefix`), validated at start. Development values are set
in `appsettings.Development.json` and by the AppHost; production values remain unresolved. Grant lifetime reuses the
existing Analysis result grant lifetime option.

**Rationale**: Follows existing options patterns; makes retry budget and limits explicit runtime policy, as the
architecture requires.

**Alternatives considered**: Hard-coded values (rejected: the architecture makes retry budgets runtime policy).
