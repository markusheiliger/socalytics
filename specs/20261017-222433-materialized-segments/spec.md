# Feature Specification: Materialized Video Segments and Analyst Input Staging

**Feature Branch**: `20261017-222433-materialized-segments`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Materialized Video Segments and Analyst Input Staging: the architecture defines a data-plane Segment Service that materializes independently decodable media for a logical segment only when it is requested, keyed by the immutable materialized identity `(recording-version-id, timeline-mapping-digest, segmentation-policy-digest, segment-number)`, with a segment manifest (full identity, nominal/requested/encoded boundaries, immutable artifact reference, media SHA-256), keyframe context that may extend the encoded interval without granting facts outside the nominal window, abstract states absent/materializing/available/failed, single current artifact-and-digest association under concurrency, rejection rules, retries that never expose partial candidates, coexistence of policy versions, and no automatic eviction during initial private development with an explicit manual purge (docs/architecture/match-data-pipeline.md#on-demand-video-segmentation and its subsections, #segment-and-match-analysis-scope, #implementation-validation; docs/architecture/terminology-and-principles.md segment terms; docs/architecture/job-processing.md#readiness-and-dependencies step 1). Today run creation computes logical segment identities (`LineageAnalysisSegmentSource`) but no media is materialized, and Analysts receive no video. This feature implements the Segment Service as part of the platform (the plan decides its host within `src/platform` and the existing layers; it never runs through an Analyst Manager and the API never relays media bytes), using the FFmpeg/ffprobe toolchain the environment spec provides, with object storage through the S3 protocol; adds the machine-readable segment manifest contract under `contracts/` (the architecture defers it to the service implementation); materializes the segments a run's segment-scoped jobs need on demand before or when the jobs become ready, idempotently and concurrency-safely, with digest validation and explicit failure that blocks only the affected jobs per the normal rules; lets the Manager holding a current attempt of a segment-scoped job whose capability declares a video policy that permits video obtain short-lived per-object read grants for its segment's media and manifest (the same Manager-mediated, attempt-fenced, lease-bounded pattern as upstream results), verify the digest, and stage them read-only into the attempt's input mount at a path the container interface defines (extending spec 8's interface and SDK input models), deleting them when the attempt ends; capabilities whose video policy forbids video never receive media; and adds a Club Admin manual purge operation for materialized segments that keeps logical identity so later requests rematerialize deterministically. The simulated Analyst and/or the Docker test Analyst consume staged media in tests; the end-to-end evidence uses a short synthetic recording. Out of scope: presigned source-video access for Analysts outside staging, member-facing media or clips (spec 10), automatic eviction and retention policy, transcoding profiles beyond what one segmentation policy v1 needs, GPU decoding, production storage and cost values, real capabilities. Depends on `specs/archive/20261017-163801-analyst-media-environment` (#143), `specs/20261017-220709-python-analyst-sdk` (batch sibling, container interface), Recording Lineage and Upload, Durable Analysis Workflow, Analysis Result Read for Jobs, Analyst Manager Work Execution, Docker Desktop Runtime Adapter for Analyst Execution, Analyst Registry (video policy) (merged; archived). Implements docs/architecture/match-data-pipeline.md#on-demand-video-segmentation (all subsections), docs/architecture/contracts-and-compatibility.md (logical-segment contract), and the media staging parts of docs/architecture/analyst-runtime-and-recovery.md."

## Clarifications

### Session 2026-10-17

- Q: How should a segment job whose run-pinned video policy is `optional` behave when its segment media is requested?
  → A: The platform requests its segment and the job waits until the segment is `available` or `failed`; when
  `available` the media is staged, and when `failed` the job becomes ready without media instead of failing.
- Q: Which scope should the Club Admin's list and purge of materialized segments have? → A: One match: all
  materialized segments of the recording versions in the match's finalized recording sets.
- Q: Which media streams should a materialized segment contain? → A: Only the source's primary video stream; audio,
  subtitle, and data streams and container metadata are excluded.
- Q: Should a result manifest record the segment media its result was derived from, so acceptance compares it? →
  A: No; the job's immutable input snapshot records the segment association, and the result manifest contract stays
  unchanged.
- Q: How should an Analyst Manager too old to stage segment media be kept from running jobs that carry segment media?
  → A: Through the stamp's minimum Manager version from
  [Analyst Manager Minimum Version](../20261017-213549-manager-minimum-version/spec.md): environments that use
  video-permitting profiles set it to at least the first Manager version that stages media; no new mechanism is added.

## User Scenarios & Testing *(mandatory)*

The users are Coaches and Club Admins, whose finalized match recordings must reach the segment-level Analysts that
need video; the operators and maintainers of the platform and the Analyst Manager, who need media to be produced
once, verified, and delivered without credentials reaching an Analyst; and Analyst authors, who need the staged
segment media and its time mapping in the container.

### User Story 1 - A segment job that needs video gets verified segment media (Priority: P1)

A Coach finalizes a match recording set. The platform creates the analysis run; for every segment-scoped job whose
run-pinned Analyst profile permits video, the platform materializes that job's logical segment from the source
recording into independently decodable media with a segment manifest, verifies its digest, and makes the job ready
only once its segment media is available. The same logical segment is materialized only once, however many jobs,
runs, or concurrent requests need it.

**Why this priority**: Without materialized media no video-consuming Analyst can run; every other story builds on the
available segment.

**Independent Test**: Upload and finalize a short synthetic recording whose mapping covers two segments of a short
test segmentation policy, with a workflow containing one segment-scoped capability whose profile requires video. Each
segment reaches `available` exactly once, its manifest validates against the segment manifest contract and states the
full identity, nominal window, requested interval, encoded boundaries covering the requested interval, artifact
reference, and a media digest equal to the stored bytes; the segment jobs become ready only after their segment is
available.

**Acceptance Scenarios**:

1. **Given** a finalized recording set and a run whose segment-scoped node's pinned profile requires video, **When**
   the run is created, **Then** the platform requests materialization of each distinct materialized identity of that
   node's jobs, and each such job stays waiting until its segment is `available` and its other readiness conditions
   hold.
2. **Given** a segment is materialized, **When** its manifest is published, **Then** it states the complete
   materialized identity, the nominal half-open match-time window, the requested source interval, the actual encoded
   media boundaries, the mapped time spans needed to convert media time to match time, an immutable artifact
   reference, and the `sha-256` media digest, and the encoded boundaries cover the complete requested interval.
3. **Given** two runs, two nodes, or concurrent requests that need the same materialized identity, **When**
   materialization completes, **Then** exactly one artifact-and-digest association is current and every requester
   receives that same association; no temporary, losing, partial, or failed candidate is ever visible.
4. **Given** a later recording-set version that contains the same recording version and timeline mapping, **When**
   its run is created, **Then** it reuses the already available materialized segment under a distinct analysis
   segment reference without materializing it again.
5. **Given** a run whose segment-scoped nodes all forbid video, **When** it is created, **Then** no segment is
   materialized and its jobs become ready exactly as today.

---

### User Story 2 - The Analyst Manager stages segment media for the attempt (Priority: P1)

The Manager claims an attempt of a segment job whose pinned profile permits video. Before launch it obtains, for its
current, unfenced attempt only, short-lived per-object read grants for the job's segment manifest and media, downloads
them, verifies the manifest against the job's recorded association and the media against the manifest's digest, and
stages both read-only at the container interface's media path. The Analyst reads the media and its manifest from that
path without any credential or network access; the staged media is deleted when the attempt ends.

**Why this priority**: Media that cannot reach the Analyst has no value; this is the Manager-mediated delivery the
architecture requires instead of credentials in the container.

**Independent Test**: Run the end-to-end flow with the simulated runtime and with the Docker test Analyst in a mode
that reads the staged media; the Analyst finds the manifest and the media at the defined path, the media's digest
equals the manifest's, the attempt's result is accepted, and the attempt directory holds no media after the attempt.
A Manager whose attempt is stale, fenced, or foreign, or whose job forbids video, obtains no grant.

**Acceptance Scenarios**:

1. **Given** the current, unfenced attempt of a video-permitting segment job, **When** the owning Manager requests
   its segment media, **Then** it receives the segment manifest and short-lived per-object read grants for the
   manifest and the media, which expire no later than the configured grant lifetime and carry no write access.
2. **Given** staged downloads, **When** the manifest's checksum differs from the association recorded for the job, the
   media digest or size differs from the manifest, or the bytes exceed the attempt's staging bound, **Then** the
   Manager deletes the partial staging, does not launch the container, and completes the attempt as failed with a
   sanitized staging category.
3. **Given** successful staging, **When** the container starts, **Then** the segment manifest and the media are
   available read-only at the media path the container interface defines, the existing job, attempt, input, result,
   and scratch mounts are unchanged, and no grant, credential, or platform address reaches the container.
4. **Given** a stale, fenced, completed, or foreign attempt, or a job whose pinned profile forbids video, **When** a
   Manager requests segment media, **Then** the request is refused without disclosing the segment, and the refusal of
   a foreign or out-of-scope request is recorded as a security audit event.
5. **Given** the attempt ends in any way, **When** the Manager cleans up, **Then** the staged media is deleted with the
   attempt directory, and at Manager start leftover attempt directories are deleted as today.

---

### User Story 3 - Failures stay explicit and affect only the jobs that need the segment (Priority: P2)

A source recording cannot be read, extraction fails, the toolchain is unavailable, or stored bytes do not match their
digest. The platform retries transient failures for the same materialized identity within a configured budget without
ever exposing a partial candidate, rejects invalid requests before publication, and, when the segment ends `failed`,
fails only the jobs that need that segment, so downstream work follows the normal required, optional, conditional,
and partial-coverage rules.

**Why this priority**: Media processing fails in practice; explicit, contained failure keeps runs explainable and
prevents fabricated or partial lineage.

**Independent Test**: Inject a transient storage failure that recovers within the budget, a persistent extraction
failure, and stored bytes altered after publication; the first ends `available` with no visible candidate in between,
the second ends `failed` and fails only that segment's video jobs with a sanitized reason, and the altered bytes are
refused as a cache hit and at staging.

**Acceptance Scenarios**:

1. **Given** a materialization request with a missing identity field, a nonpositive duration or segment number, an
   unknown policy or mapping, a recording-set, match, or team scope mismatch, or an empty coverage intersection,
   **When** it is evaluated, **Then** it is rejected before publication and creates no cache entry and no lineage
   input.
2. **Given** a transient extraction or storage failure, **When** the retry budget is not exhausted, **Then** the
   platform retries with the unchanged materialized identity, and no failed or partial candidate becomes visible
   between attempts.
3. **Given** the retry budget is exhausted or the failure is not retryable, **When** materialization ends, **Then** the
   segment is `failed` with a sanitized reason, no association is published, each waiting job whose policy requires
   the segment's media fails with a sanitized segment-unavailable reason, each waiting job whose policy is `optional`
   becomes ready without media, and jobs of other segments continue.
4. **Given** a published association whose stored bytes no longer match the manifest digest, **When** it is used as a
   cache hit or staged, **Then** it is refused, and the platform does not hand it to a new job.
5. **Given** a `failed` segment, **When** a later run needs the same materialized identity, **Then** the platform
   attempts materialization again rather than reusing the failure forever.

---

### User Story 4 - A Club Admin purges cached segment media (Priority: P3)

During initial private development segment media is kept until an explicit purge. A Club Admin lists the
materialized segments of one of the club's matches with their states and purges them; purged media and manifests are
deleted from object storage while their logical identity stays addressable, so a later need rematerializes the same
logical segment with the same validation.

**Why this priority**: Storage grows without automatic eviction; the manual purge is the only bounded cleanup the
architecture allows initially, and it also proves that purge never changes logical identity.

**Independent Test**: As a Club Admin, list a match's materialized segments, purge them, check that their objects are
gone and their states are `absent`, then create a new run that needs one of them and observe it rematerialized with a
valid manifest. A Coach or a Club Admin of another club is refused; a segment pinned by a waiting, ready, or claimed
job is reported as in use and kept.

**Acceptance Scenarios**:

1. **Given** a Club Admin of the club that owns a match, **When** they list the match's materialized segments,
   **Then** they see each materialized identity of the match's recording versions with its state and, when
   available, its media size, without object-storage locations.
2. **Given** a Club Admin purges a match's materialized segments, **When** the purge completes, **Then** every
   segment not in use is deleted from object storage, becomes `absent`, keeps its identity, and the purge is recorded
   as a security audit event with the actor, the match, and the counts of purged and in-use segments.
3. **Given** a segment whose association is pinned by a waiting, ready, or claimed job of a non-terminal run, **When**
   the purge runs, **Then** that segment is kept and reported as in use.
4. **Given** a purged segment, **When** a later job needs it, **Then** it is materialized again from the same identity
   and published only after the same manifest and digest validation.
5. **Given** a member without the Club Admin role for the match's club, **When** they list or purge, **Then** the
   request is refused without disclosing the match's segments.

### Edge Cases

- The nominal window of a segment only partly overlaps the mapped coverage: the requested interval is clipped to the
  covered part; numbering and nominal ownership never shift.
- The intersection of a nominal window with a multi-span mapping covers several mapped spans: the requested media
  interval is the smallest contiguous media interval covering them, and the manifest lists the mapped spans so the
  Analyst can tell mapped from unmapped media time.
- An event exactly at a segment boundary belongs only to the later segment; encoded keyframe context before or after
  the requested interval never extends the facts an Analyst may report.
- The source has its first keyframe after the requested start, or the extracted media does not cover the requested
  interval: materialization fails rather than publishing an incomplete segment.
- Two platform instances or workers pick the same request: one publishes; the other observes the published
  association and publishes nothing.
- The platform restarts while a segment is `materializing`: the work resumes or restarts after its lease expires; the
  temporary candidate is never published.
- A job's attempt is retried after its segment was purged while no job pinned it: cannot happen for non-terminal jobs
  (purge keeps in-use segments); a new run's job requests rematerialization.
- A run is cancelled or superseded while its segments materialize: materialization completes or fails as cache work;
  cancelled jobs are not made ready.
- The pinned profile's video policy is `forbidden`: no materialization request, no media in the job message, no grant,
  and no media mount.
- The pinned profile's video policy is `optional` and the segment fails: the job runs without media; its job message
  states no segment association and the media path is absent.
- The media exceeds the Manager's staging bound: the attempt fails with the staging-limit category before launch.
- A Manager older than the first version that stages media: it admits no new work when the stamp's minimum Manager
  version is set accordingly; without that minimum, such a Manager could run a media job without staged media, and the
  Analyst fails on the missing media rather than producing a result.
- The toolchain is missing or the wrong version on the platform host: materialization fails explicitly with a
  sanitized, non-retryable reason and the health of other work is unaffected.
- Several recording versions in one set: each recording version's segments are separate materialized identities and
  separate jobs, as today.

## Requirements *(mandatory)*

### Functional Requirements

#### Segment Service and materialization

- **FR-001**: The platform MUST provide the Segment Service as part of the platform control-plane deployment; it MUST
  NOT run through an Analyst Manager, and no API operation may read or relay media bytes to or from a client or a
  Manager.
- **FR-002**: The Segment Service MUST materialize a logical segment only when it is requested, keyed by the complete
  materialized identity `(recording-version-id, timeline-mapping-digest, segmentation-policy-digest, segment-number)`
  as defined by the [Segment Contract](../../docs/architecture/match-data-pipeline.md#segment-contract), and MUST
  derive the nominal window, requested source interval, and coverage clipping exactly as that contract defines.
- **FR-003**: Materialized media MUST be independently decodable, MUST cover the complete requested interval, MAY
  extend beyond it only for keyframe decoding context, and MUST be produced by the toolchain that the environment
  feature pins; segmentation policy v1 MUST NOT require transcoding beyond what producing that media needs. The media
  MUST contain only the source's primary video stream, without audio, subtitle, or data streams or source container
  metadata.
- **FR-004**: The Segment Service MUST write the media and its segment manifest as immutable objects through the
  object-storage protocol, compute the media digest from the stored bytes, and publish an association only after the
  stored media's digest and size equal the manifest's.
- **FR-005**: Materialization MUST expose exactly the states `absent`, `materializing`, `available`, and `failed` per
  materialized identity, and only an `available` association with a valid digest MUST be visible as a cache hit or
  lineage input.
- **FR-006**: Concurrent requests for one materialized identity, across requesters, workers, and platform instances,
  MUST result in at most one current artifact-and-digest association, and every successful requester MUST receive that
  association; temporary, partial, failed, and losing candidates MUST remain invisible and MUST be cleaned up.
- **FR-007**: A request MUST be rejected before publication when any identity field is missing, the duration or
  segment number is nonpositive, the policy or mapping is unknown, recording-set membership or match or team scope
  disagrees, or the coverage intersection is empty; a rejection MUST create no cache entry and no lineage input.
- **FR-008**: Transient extraction or storage failures MUST be retried with the unchanged identity within a configured
  retry budget; exhausted budgets and non-retryable failures MUST end in `failed` with a sanitized reason and no
  published association. A later request for a `failed` identity MUST attempt materialization again.
- **FR-009**: Bytes that fail digest validation MUST be refused as a cache hit and as lineage input.
- **FR-010**: Recording versions, mappings, and segmentation policies MUST coexist by identity; a changed identity
  field MUST create a new materialized identity without invalidating an existing one, and a recording-set-only
  revision MUST reuse available media under a distinct analysis segment reference.
- **FR-011**: The platform MUST NOT evict materialized segments automatically; only the manual purge removes them.

#### Analysis readiness

- **FR-012**: When an analysis run is created, the platform MUST request materialization of every distinct
  materialized identity needed by a segment-scoped job whose run-pinned Analyst profile's video policy permits video
  (`required` or `optional`), and MUST NOT request it for jobs whose policy forbids video.
- **FR-013**: A segment-scoped job whose pinned video policy is `required` MUST become ready only when its segment is
  `available` and its other readiness conditions hold; when its segment ends `failed` or its materialization request
  was rejected, the job MUST fail with a
  sanitized segment-unavailable reason, and dependent work MUST follow the normal dependency and coverage rules.
- **FR-014**: A segment-scoped job whose pinned video policy is `optional` MUST wait until its segment is `available`
  or `failed` and its other readiness conditions hold; it MUST become ready with segment media when the segment is
  `available` and without segment media when the segment is `failed` or its materialization request was rejected.
- **FR-015**: When a job becomes ready with segment media, its immutable input snapshot and its ready-job message
  MUST record the segment association it uses (at least the segment manifest's checksum), so the Manager can verify
  the staged manifest and the run keeps auditable lineage; jobs without media MUST carry no such statement. The result
  manifest contract and its acceptance checks MUST stay unchanged; the input snapshot is the media lineage record.

#### Manager-mediated media read and staging

- **FR-016**: The platform MUST provide a Manager-only operation, authenticated like the existing Manager operations,
  that returns, for the current, unfenced, lease-valid attempt of the calling Manager, the job's recorded segment
  manifest and short-lived per-object read grants for the manifest and the media; grants MUST expire within the
  configured grant lifetime and no later than the attempt's lease expiry, MUST name only those objects, and MUST grant
  no write access.
- **FR-017**: The operation MUST refuse attempts that are stale, fenced, completed, foreign, or not current, jobs
  without recorded segment media, and associations whose stored bytes fail validation, without disclosing the segment;
  foreign and out-of-scope requests MUST be recorded as security audit events.
- **FR-018**: Before launching an attempt of a job with recorded segment media, the Manager MUST obtain the grants,
  download the manifest and the media, verify the manifest's checksum against the job's recorded association and the
  media's digest and size against the manifest, count the bytes against the attempt's configured staging bound, and
  stage both read-only at the media path of the container interface; any failure MUST delete the partial staging,
  prevent the launch, and fail the attempt with a sanitized staging category.
- **FR-019**: The container interface MUST define the media path and its layout for both the simulated and the Docker
  runtime; the media path MUST be absent for jobs without segment media, and every other mount and the no-network,
  no-environment-variable, no-credential rules MUST stay unchanged.
- **FR-020**: Staged media MUST be deleted when the attempt ends, by the same cleanup as staged upstream results.
- **FR-021**: The Analyst SDK MUST expose the staged segment manifest as a typed model and the media as a verified
  read-only file, verify the media digest against the manifest, report no media for jobs without it, and fail input
  loading with a named error when the staged media is inconsistent.

#### Segment manifest contract

- **FR-022**: A versioned, machine-readable segment manifest contract MUST be added under `contracts/` that encodes
  the Segment Contract's identities without redefining them, with released copies, valid and invalid examples, and an
  index row; the platform, the Manager, and the SDK MUST validate manifests against it.
- **FR-023**: Changes to the job message and any new Manager-facing response MUST be additive contract versions under
  the repository's contract rules; earlier versions MUST stay valid.

#### Purge and visibility

- **FR-024**: A Club Admin of the club that owns a match MUST be able to list the match's materialized segments (every
  materialized identity of the recording versions in the match's finalized recording sets that was ever requested)
  with their identity, state, and media size, without object-storage locations.
- **FR-025**: A Club Admin MUST be able to purge a match's materialized segments; the purge MUST delete the media and
  manifest objects of every segment not pinned by a waiting, ready, or claimed job of a non-terminal run, return them to
  `absent` with their identity kept, report purged and in-use counts, be idempotent, and be recorded as a security
  audit event.
- **FR-026**: After a purge, a later need for the same identity MUST rematerialize it with the same manifest and digest
  validation.
- **FR-027**: List and purge MUST be refused for members without the Club Admin role of the owning club, without
  disclosing the match's segments.

#### Evidence and documentation

- **FR-028**: Automated tests MUST cover boundary derivation and clipping, manifest contents and contract validity,
  digest validation, rejection rules, concurrent requests for one identity, retries without visible candidates,
  failure propagation to only the affected jobs, readiness gating, the media read's fencing and refusals, Manager
  staging and cleanup on both runtimes, SDK media loading, purge and rematerialization, and authorization.
- **FR-029**: End-to-end evidence MUST upload and finalize a short synthetic recording, materialize its segments, and
  run a video-consuming test Analyst that reads the staged media and whose result is accepted.
- **FR-030**: The documented commands, local setup (including the toolchain the platform host needs), and the
  architecture narratives this feature affects MUST describe the implemented state.

### Key Entities

- **Materialized Segment**: One materialized identity with its state (`absent`, `materializing`, `available`,
  `failed`), its current association when available, its sanitized failure reason when failed, and its retry count.
- **Segment Association**: The single current, immutable pair of media artifact and segment manifest for one
  materialized identity, with the media digest and size and the manifest checksum.
- **Segment Manifest**: The published document stating the full identity, nominal window, requested source interval,
  encoded boundaries, mapped time spans, artifact reference, and media digest.
- **Segment Media Read**: The Manager's attempt-scoped request returning the manifest and short-lived per-object read
  grants.
- **Staged Segment Media**: The manifest and media in the attempt's read-only media path.
- **Segment Purge**: A Club Admin's audited removal of a match's unused materialized segments.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated tests, 100% of published segment manifests validate against the segment manifest contract,
  their encoded boundaries cover the requested interval, and their media digest equals the stored bytes.
- **SC-002**: With at least 8 concurrent requests for the same materialized identity, exactly 1 association becomes
  current and 100% of successful requesters receive it; 0 temporary or losing candidates remain visible afterwards.
- **SC-003**: 0 jobs whose pinned policy forbids video receive media, a grant, or a materialization request.
- **SC-004**: 100% of staged media in the tests is verified against the job's recorded association and the manifest
  digest before launch, and 0 bytes of staged media remain after an attempt ends.
- **SC-005**: A materialization failure fails only the jobs that need the failed segment; in the failure tests 100% of
  other segments' jobs reach their normal outcome.
- **SC-006**: The end-to-end flow from finalizing a short synthetic recording to an accepted result of a
  video-consuming test Analyst completes in the automated test suite on the verification environment.
- **SC-007**: After a purge, 100% of purged segments needed again are rematerialized and validated, and 0 in-use
  segments are deleted.

## Assumptions

### Dependencies

- [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md) (twin #143)
  provides the pinned FFmpeg and ffprobe build in verification; it MUST merge before this feature.
- [Python Analyst SDK](../20261017-220709-python-analyst-sdk/spec.md) (batch sibling) completes the container
  interface (verbatim job file, analyst-job 1.1.0 with the segment window, attempt context) and creates the SDK this
  feature extends; it MUST merge before this feature.
- [Analyst Manager Minimum Version](../20261017-213549-manager-minimum-version/spec.md) (batch sibling) provides the
  stamp's minimum Manager version that keeps Managers unable to stage segment media from admitting work; it MUST merge
  before this feature, and this feature's Manager documentation names the first Manager version that stages media.
- [Recording Lineage and Upload](../archive/20261005-130702-recording-lineage-upload/spec.md),
  [Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md),
  [Analysis Result Read for Jobs](../archive/20261013-134112-analysis-result-read/spec.md),
  [Analyst Manager Work Execution](../archive/20261012-141233-manager-work-execution/spec.md),
  [Docker Desktop Runtime Adapter for Analyst Execution](../archive/20261015-092159-docker-runtime-adapter/spec.md),
  and the Analyst Registry (profile video policy) are implemented and provide recording versions and mappings, object
  storage, logical segment identities at run creation, the Manager-mediated staging pattern, and the container
  runtimes.

### Architecture Alignment

- Implements [On-Demand Video Segmentation](../../docs/architecture/match-data-pipeline.md#on-demand-video-segmentation)
  with its [Segment Contract](../../docs/architecture/match-data-pipeline.md#segment-contract),
  [Segment And Match Analysis Scope](../../docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope),
  and [Implementation Validation](../../docs/architecture/match-data-pipeline.md#implementation-validation); step 1 of
  [Readiness And Dependencies](../../docs/architecture/job-processing.md#readiness-and-dependencies); the
  logical-segment contract row of [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md);
  and the media staging parts of [Analyst Runtime and Recovery](../../docs/architecture/analyst-runtime-and-recovery.md).
- The run-pinned Analyst profile's video policy (`required`, `optional`, `forbidden`) is the capability's video
  permission; it is already validated against the capability declaration's video access.

### Scope Boundaries

- Presigned source-video or segment access for Analysts outside Manager staging, member-facing media and evidence
  clips (Evidence Clip Access, the next spec of this batch), automatic eviction and retention policy, additional
  transcoding profiles, GPU decoding, production storage, cost, and toolchain values for deployment, a container image
  for the platform, and real capabilities are out of scope.
- The prototype measurements the architecture asks for (storage cost, policy-version migration at scale) are recorded
  as development evidence only; they do not approve production values.
- A Web UI for listing and purging segments is out of scope; the operations are member API operations.
