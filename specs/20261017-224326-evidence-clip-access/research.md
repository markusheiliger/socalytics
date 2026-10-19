# Research: Evidence Clip Access

All Technical Context unknowns are resolved below. Each decision lists its rationale and the alternatives considered.

## R1 - Operation shape

**Decision**: One member operation `POST /api/v1/analysis/runs/{runId}/evidence-media` (operation
`resolveAnalysisEvidenceMedia`, tag `Analysis evidence`) mapped through `MapMemberApi()` beside the run and fact reads.
The body names exactly one of `fact` (`acceptedResultId`, `ordinal`, optional `contextBeforeMs` and `contextAfterMs`)
or `range` (`fromMs`, `toMs`), plus optional `retryFailed` (default `false`). The response is `200` with the resolution
and `Cache-Control: no-store`; it carries no `ETag`. No `If-Match` is required because the operation changes no
versioned member resource.

**Rationale**: Resolution may request on-demand materialization, so it is not a safe method; `POST` with the existing
session anti-forgery filter (`SessionAntiforgeryFilter`) fits the member API conventions. The run is the scope that
carries team authorization (`analysis-run` team scope), the recording-set version, and the pinned segmentation policy,
so the route sits under the run like the fact reads of
[Accepted Fact Indexing](../archive/20261017-175648-fact-indexing-queries/contracts/member-fact-api.md). A future MCP
`get_video_clip()` tool can call the same Application handler.

**Alternatives considered**: `GET` with query parameters (rejected: it has side effects and responses must never be
cached or replayed by intermediaries); a match-level route taking a run identifier (rejected: duplicates the run's
scope check and invites mixing runs); separate operations for facts and ranges (rejected: identical resolution and
response).

## R2 - Coverage planning

**Decision**: A pure Domain planner (`EvidenceCoveragePlanner`) receives the requested half-open match-time range, the
run's recording-set members in membership order (recording version identity, timeline-mapping digest, and mapped spans),
and the pinned segmentation policy duration. It walks the members in order and assigns each still-unassigned match-time
sub-window to the first member whose mapped spans cover it; what no member covers becomes `not-covered`. Each assigned
sub-window is split at nominal segment boundaries `[(n-1)·D, n·D)` and at mapped-span boundaries, so every resulting
part belongs to exactly one materialized identity `(recording-version-id, timeline-mapping-digest,
segmentation-policy-digest, n)` and one mapped span. Parts are ordered by match time, are contiguous, and never
overlap. A fact range is first extended by the requested context (clipped at zero) and then shortened from its start to
`MaxDuration` when longer; an explicit range longer than `MaxDuration` is rejected.

**Rationale**: Implements the clarified "first recording in the set's order" rule and the
[Segment Contract](../../docs/architecture/match-data-pipeline.md#segment-contract) windows exactly; splitting at span
boundaries makes each part's media offsets a single linear translation. The segment window arithmetic reuses the
Domain calculator that [Materialized Video Segments](../20261017-222433-materialized-segments/plan.md) adds
(`SegmentWindowCalculator`), so evidence and analysis agree on numbering.

**Alternatives considered**: Returning every covering recording (rejected by clarification: more UI scope, no player
for several angles); one part per segment regardless of spans (rejected: a cut inside a segment would need two offset
mappings in one part).

## R3 - Run lineage inputs

**Decision**: The handler loads the run (team, match, recording-set version, workflow snapshot) through the existing
run queries, the set's ordered members with their exact timeline mappings through `IRecordingLineageQueries` (the typed
Recordings lineage query), and the segmentation policy (version, duration, digest) from the run's pinned workflow
snapshot. Nothing is read from other runs or set versions.

**Rationale**: These are immutable, already pinned at run creation, and are the same inputs that created the run's
segment jobs; the result cannot drift when newer sets or workflows exist.

**Alternatives considered**: Deriving segments from the run's logical jobs (rejected: match-scope-only workflows have
no segment jobs, and ranges may cross parts the jobs do not cover).

## R4 - Segment states and on-demand requests

**Decision**: For every part the handler reads the materialized-segment record of its identity through the Segment
Service's store (`ISegmentStore`) and maps it:

| Record | Action | Part state |
| --- | --- | --- |
| none, or `absent` never purged | request materialization (`RequestSegmentMaterialization`) | `materializing` |
| `absent` with a purge time | request materialization | `rematerializing` |
| `materializing` | none | `materializing`, or `rematerializing` when it was purged before |
| `failed` | none, or with `retryFailed` a new request | `failed` with the sanitized reason, or `materializing` |
| `available` | integrity check (R5) | `available`, or `failed` (`segment-media-invalid`) |
| request rejected before publication | none | `failed` with the rejection reason |

Requests use the run's recording-set version for scope validation, as run creation does, and run in the resolution's
unit of work. Each preparing part carries `retryAfterSeconds` from `Analysis:Evidence:RetryAfter`.

**Rationale**: Reuses the Segment Service's idempotent, concurrency-safe demand without a second materialization
path; the purge time distinguishes rematerialization, which the materialized-segment record of spec 9 already keeps.
Failed segments are not re-requested by default, so a permanently failing segment is not retried on every view
(clarification), while the player's "try again" can still ask for it.

**Alternatives considered**: Materializing synchronously inside the request (rejected: long media work in an HTTP
request; the architecture keeps extraction in the Segment Service worker); pinning evidence segments against purge
(rejected: the purge's in-use rule belongs to spec 9 and covers jobs only; a purged segment simply rematerializes).

## R5 - Integrity check and media offsets

**Decision**: For an `available` record the handler confirms, before issuing a link, that the media object's attested
full-object SHA-256 and size (`IObjectStorage.GetObjectChecksumAsync`) equal the recorded association, reads the
segment manifest object (at most 64 KiB, `ReadObjectAsync`), checks that its SHA-256 equals the recorded manifest
checksum, and validates it against `contracts/segments/segment-manifest/v1/`. A mismatch hands the identity to the
Segment Service's invalid-media handling (state `failed`, reason `segment-media-invalid`, objects deleted) and reports
the part as `failed`. Storage that cannot be reached fails the whole resolution with `503`
`evidence-storage-unavailable`; no link is issued without the check. Offsets come from the manifest: for match time
`t` inside a timeline span `{mediaStartMs, mediaEndMs, matchStartMs}`, the media time is
`mediaStartMs + (t − matchStartMs)`, and the offset is that media time minus `encodedInterval.startMs`. A part's
`startOffsetMs` and `endOffsetMs` translate its half-open window; the playback timeline of the media starts at the
encoded interval's first packet.

**Rationale**: Only metadata and the small manifest document are read; video bytes never pass through the platform
(constitution Principle III). The checksum check matches spec 9's Manager media read and the architecture rule that
bytes failing digest validation are never handed out. The manifest is the only record of the encoded interval and the
spans, so reading it avoids duplicating them in the materialized-segment record that spec 9 owns.

**Alternatives considered**: Issuing links without checking (rejected: the spec requires the digest check); adding
encoded-interval columns to spec 9's table (rejected: duplicates the manifest and changes another feature's schema);
reading the media to compute offsets (rejected: control plane never touches video).

**Open item recorded for implementation**: MP4 files written with source timestamps can expose a non-zero presentation
start. The Web UI player therefore seeks relative to the media's first seekable time (R10); the platform browser test
verifies that playback starts at the expected frame within one frame.

## R6 - Read links

**Decision**: Each available part's link is `IObjectStorage.PresignGetObject(mediaObjectKey, now + GrantLifetime)`
(S3 presigned `GET`, read-only, one object, range requests allowed). Two parts of the same segment share one link per
response. The link exists only in the response DTO: it is not persisted, the API has no request/response body logging,
exceptions and problem details never include it, and the audit event omits it. Integration tests capture all log
output of the host and assert that no link, signature parameter, or object key appears.

**Rationale**: Same short-lived, per-object grant type the platform already issues for uploads and Manager reads; the
media object key stays inside the signed URL, which reveals nothing beyond that object.

**Alternatives considered**: A redirecting API endpoint (rejected: the API would sit on every media request and still
hand out the URL); CDN-signed URLs (out of scope).

## R7 - Audit

**Decision**: A resolution that issues at least one link records one security audit event
`analysis.evidence-media.granted` through `IAuditTrail.RecordAsync` in the resolution's unit of work, with the actor,
club, team, match, run, the fact reference (`acceptedResultId`, `ordinal`) or range, the requested window, the media
digests of the granted parts, and the latest link expiry. Refusals use the existing authorization handling and are not
audited separately, consistent with the run and fact reads.

**Rationale**: Implements artifact-authorization evidence for `DAT-004` access per
[Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events) without durable presigned URLs.

**Alternatives considered**: One event per part (rejected: noisier with no added authority information); auditing
every resolution including those without links (rejected: no artifact authorization happened).

## R8 - Rate limiting

**Decision**: A rate-limiter policy `analysis-evidence-media` (`EvidenceRateLimiting`, an `IRateLimiterPolicy<string>`
like `EmailRateLimiting`) partitions a fixed one-minute window by the member account
(`SessionClaimTypes.AccountId`) with `Analysis:Evidence:ResolutionsPerMinute` permits and no queue; rejection returns
`429` problem `evidence-rate-limited` with `Retry-After` and `Cache-Control: no-store`. The endpoint requires the policy,
so a rejected request reaches neither authorization nor the Segment Service.

**Rationale**: Reuses the established per-policy rejection pattern without changing the Registry's global `OnRejected`.

**Alternatives considered**: Limiting by address (rejected: members behind one club network would share a budget);
limiting inside the handler (rejected: duplicate infrastructure).

## R9 - Configuration

**Decision**: New options section `Analysis:Evidence`, validated at start: `GrantLifetime` (development `00:05:00`,
allowed `00:00:30`–`01:00:00`), `RetryAfter` (development `00:00:05`, `00:00:01`–`00:05:00`), `ResolutionsPerMinute`
(development `60`, `1`–`10000`), `MaxDuration` (development `00:10:00`, `00:00:01`–`01:00:00`), and `MaxContext`
(development `00:00:10`, `00:00:00`–`00:01:00`). The AppHost sets the development values; every test settings
dictionary that configures the API host sets them too. Production values remain unresolved.

**Rationale**: Follows the existing required-options pattern (`Analysis:Results`, `Analysis:Reads`) and keeps every
lifetime and limit an explicit runtime value.

**Alternatives considered**: Code defaults (rejected: would read as adopted production values).

## R10 - Web UI player

**Decision**: A "Play evidence" button on each fact row of the review's fact browser opens `EvidencePlayerDialog`.
`useEvidencePlayback` is a reducer-based state machine (`resolving`, `playing`, `preparing`, `partUnavailable`,
`shortened`, `renewing`, `notFound`, `rateLimited`, `offline`, `error`) over the new adapter
`analysisEvidenceAdapter.resolve` (generated Kiota client). The fact resolution asks for 5 seconds of context on each
side. Playback uses two `<video crossorigin="anonymous" preload="auto">` elements that alternate, so the next piece is
loaded while the current one plays; each piece seeks to its media's first seekable time plus `startOffsetMs` and stops
at `endOffsetMs` (frame callback with a `timeupdate` fallback) before switching. Preparing parts are resolved again
after `retryAfterSeconds`, at most 20 times automatically, then "Try again" resolves with `retryFailed` for failed parts
and without it otherwise. A piece whose link expires within 30 seconds before it is loaded, or a media error, triggers
one re-resolution and resumption at the current match-time position. The player marks whether the position lies
inside the fact's window. Resolutions and links stay in component memory only.

**Rationale**: Native media elements keep playback, keyboard controls, and accessibility; alternating elements avoid a
visible stop between pieces; the explicit state machine is unit-testable without a browser.

**Alternatives considered**: Media Source Extensions concatenation (rejected: needs byte access and codec handling the
platform does not promise); one element with source swapping (rejected: visible stall at every boundary).

## R11 - Content Security Policy and object-store CORS

**Decision**: The preview and development policies in `src/clients/web/vite.config.ts` change `media-src blob:` to
`media-src blob: <object-storage-origin>` when `SOCALYTICS_OBJECT_STORAGE_ORIGIN` is set (else `blob:` only), and the
header test's expected policy is updated. Because the video elements use CORS mode, the store must answer media
`GET`/`HEAD` and range requests with CORS headers for the Web UI origin only: the AppHost's existing RustFS
`RUSTFS_CORS_ALLOWED_ORIGINS` (the Web UI origin, and the e2e origins) already restricts origins; the fake object
store of the simulated tests gains `GET`/`HEAD` with `Range` support for seeded objects and answers CORS only for its
configured origin. The service worker's existing rule of leaving cross-origin requests unhandled is covered by a unit
test for object-store media URLs.

**Rationale**: Media from the store's origin only, as the spec requires, with the same origin variable the upload
already uses; CORS mode makes another origin's playback fail at the store.

**Alternatives considered**: No-CORS media requests (rejected: then the store's allow-list would not constrain
playback); a separate media origin variable (rejected: one development store).

## R12 - Playable synthetic media for browser tests

**Decision**: Playwright's Chromium decodes neither H.264 nor MPEG-4 Part 2, so browser tests use a short synthetic VP9
recording in MP4 (`testsrc2`, 64×64, 10 seconds, keyframe every second, `libvpx-vp9`, `+faststart`) generated at test
time with the pinned FFmpeg of the
[Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md), whose LGPL
build includes the BSD-licensed libvpx. The simulated tests seed it into the fake object store; the platform test
uploads it through the existing upload flow, and the Segment Service's stream copy keeps VP9. Platform integration
tests reuse spec 9's synthetic recording helper, since they do not decode in a browser.

**Rationale**: No binary fixtures and no new tool; the same toolchain the platform host needs for materialization.

**Alternatives considered**: A committed binary fixture (rejected as in spec 9); browser-recorded WebM through
`MediaRecorder` (rejected: such files lack seek cues, so offsets cannot be tested); the Chrome channel with proprietary
codecs (rejected: a different browser than the pinned Playwright Chromium).

**Open item recorded for implementation**: confirm that `ffmpeg -encoders` of the pinned build lists `libvpx-vp9`;
if it does not, the evidence browser tests cannot decode media and the environment must change through an environment
spec before this feature merges.

## R13 - Tests

**Decision**: Domain tests for the planner (inside one segment, across a boundary, exactly at boundaries, span gaps,
several recordings, context clipping, shortening); Integration tests (PostgreSQL, RustFS, toolchain) for resolution
states, on-demand and post-purge requests, `retryFailed`, digest and manifest mismatches, offsets against the manifest
and the probed media within one frame, link expiry against the store, authorization on every request including role
revocation, audit content, log capture without links, rate limiting, and options validation; Host tests for the
OpenAPI snapshot; an end-to-end test that resolves a fact of the development workflow over a materialized synthetic
recording; Vitest for the adapter, the playback state machine, and the service-worker rule; Playwright simulated tests
for every player state, piece sequencing, expiry renewal, security policy, CORS, keyboard, axe, offline, and absence of
stored data; and one Playwright platform test that plays a fact's evidence.

**Rationale**: Covers FR-024 at the cheapest layer for each behavior while keeping one real playback.

**Alternatives considered**: Browser-only coverage of offsets (rejected: slow and imprecise).
