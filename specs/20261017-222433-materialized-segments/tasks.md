---

description: "Task list for Materialized Video Segments and Analyst Input Staging"
---

# Tasks: Materialized Video Segments and Analyst Input Staging

**Input**: Design documents from `specs/20261017-222433-materialized-segments/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required. FR-028 to FR-030 and SC-001 to SC-007 require automated evidence; test work is part of each task.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files never
  overlap.
- **[Story]**: Which user story this task belongs to (US1 to US4)
- Paths are repository-relative; the migration number `NNNN` is the next free number on an up-to-date default branch
  at implementation time.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The machine-readable contracts every later task validates against (FR-022, FR-023).

### Contract schemas

- [x] T001 [P] Create `contracts/segments/segment-manifest/v1/segment-manifest.schema.json` 1.0.0 with
  `releases/1.0.0.schema.json`, `examples/valid/` (`segment-manifest.json`, `clipped-segment-manifest.json`),
  `examples/invalid/` (`missing-media-digest.json`, `plain-digest-form.json`, `zero-segment-number.json`,
  `unknown-property.json`, `audio-media-type.json`), and `examples/invalid/expectations.json`, exactly as specified in
  `specs/20261017-222433-materialized-segments/contracts/segment-contracts.md` (`mediaType` const `video/mp4`, policy
  `version` const `1`, `segmentNumber` minimum 1, `timelineSpans` minItems 1, shared definitions from
  `contracts/common/v1/common.schema.json`)
- [x] T002 [P] Add `analysis/analyst-job` 1.2.0 to `contracts/analysis/analyst-job/v1/analyst-job.schema.json` on top
  of 1.1.0: optional `segmentMedia` (`videoPolicy` enum `required`/`optional`, `manifestChecksum` sha-256 digest), the
  `allOf` rule that forbids it for `executionScope = match`, `releases/1.2.0.schema.json`,
  `examples/valid/segment-job-with-media.json`, `examples/invalid/segment-media-on-match-job.json` and
  `segment-media-forbidden-policy.json` with `expectations.json` entries; earlier releases stay unchanged. In the same
  task, keep the SDK suite green (the SDK embeds a copy of the analysis contracts, and its tests validate every
  example): refresh the copy with `src/analysts/sdk/scripts/sync_contracts.py`, add `1.2.0` to the supported
  `analyst-job` versions in `src/analysts/sdk/src/socalytics_analyst/schemas.py`, add the optional `segmentMedia`
  field (`videoPolicy`, `manifestChecksum`) to `LogicalJob` in `src/analysts/sdk/src/socalytics_analyst/models/job.py`
  so the model matches the schema, and extend the SDK job tests for it; run `uv run --no-sync pytest` and
  `uv run --no-sync python scripts/sync_contracts.py --check` in `src/analysts/sdk` (the verification runs the SDK
  tests, which failed twice when only the contract changed)

### Segment media response schema

- [x] T003 Create `contracts/analysis/segment-media/v1/segment-media.schema.json` 1.0.0 with
  `releases/1.0.0.schema.json`, `examples/valid/segment-media.json`, `examples/invalid/` (`missing-grants.json`,
  `write-grant-name.json`, `single-grant.json`), and `expectations.json`, referencing the segment manifest schema, as
  specified in `specs/20261017-222433-materialized-segments/contracts/segment-contracts.md` (exactly two grants named
  `segment-manifest.json` and `segment.mp4`)

### Contract registration

- [x] T004 Register the three contract changes: index rows in `contracts/README.md` (segment-manifest owner "Segment
  Service (control-plane deployment)", segment-media owner "Scheduler and Job Registry; Analyst Manager as consumer",
  analyst-job version `1.2.0` with its released copies), schema ids in
  `src/platform/SocAlytics.Platform.Application/Abstractions/ContractSchemaIds.cs`, catalog entries in
  `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/ContractCatalog.cs`, and
  `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/SegmentContractsTests.cs` that validates every example and
  checks the rules the schema cannot express (`endMs > startMs`, encoded interval covers the requested interval,
  nominal window equals `[(n-1)·D, n·D)`, spans ordered, non-overlapping, and inside the nominal window); extend
  `AnalysisExampleTests.cs` for analyst-job 1.2.0; run `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain rules, persistence, storage, options, and test support that every story uses.

### Segment domain, schema, storage, and options

- [x] T005 [P] Implement the `Segments` domain in `src/platform/SocAlytics.Platform.Domain/Segments/`
  (`MaterializedSegmentIdentity` with all four fields required and `segmentNumber > 0`; `SegmentWindowCalculator` for
  the nominal window `[(n-1)·D, n·D)`, the intersection with mapped spans, the requested source interval as the
  smallest contiguous media interval covering every non-empty intersection, the intersected `timelineSpans`, and
  rejection on an empty intersection; `MaterializedSegmentState` `absent | materializing | available | failed`;
  `SegmentFailureReasons` with exactly the sanitized reasons of `data-model.md`), consistent with
  `src/platform/SocAlytics.Platform.Infrastructure/Analysis/LineageAnalysisSegmentSource.cs` numbering, with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/Domain/SegmentWindowCalculatorTests.cs` (boundary
  ownership at exactly `n·D`, clipping, multi-span coverage, empty intersection, nonpositive values) (FR-002, FR-007)
- [x] T006 [P] Add `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_segments_materialized_segments.sql`
  creating `socalytics.materialized_segments` exactly as in `data-model.md` (unique identity over
  `recording_version_id, timeline_mapping_digest, segmentation_policy_digest, segment_number`; check constraints:
  `segment_number > 0`, `duration_seconds > 0`, `state IN ('absent','materializing','available','failed')`,
  association columns all null unless `state = 'available'` and all non-null when it is, `candidate_id`,
  `lease_owner`, `lease_expires_at` non-null exactly when `state = 'materializing'`), `version` through
  `socalytics.attach_version_trigger`, indexes for claiming (`state`, `demanded`, `next_attempt_at`,
  `lease_expires_at`) and recording-version lookup, the nullable column `segment_rejection_reason` (text) on
  `socalytics.logical_jobs`, and grants for `socalytics_app` as in existing migrations
- [x] T007 [P] Add a streaming put with a full-object SHA-256 checksum and content length to
  `src/platform/SocAlytics.Platform.Application/Abstractions/ObjectStorage/IObjectStorage.cs` and implement it in
  `src/platform/SocAlytics.Platform.Infrastructure/ObjectStorage/S3ObjectStorage.cs` (the store must reject bytes that
  do not match), updating every test double that implements `IObjectStorage` under `src/platform/Tests/`, with an
  integration test against the RustFS test container in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/` or the existing object-storage test file
  (FR-004)
- [x] T008 [P] Add `src/platform/SocAlytics.Platform.Application/Segments/SegmentOptions.cs` binding `Segments:Materialization`
  (`PollInterval`, `LeaseDuration`, `MaxConcurrentPerInstance`, `MaxAttempts`, `RetryDelay`, `ExecutionTimeout`,
  `MaxSegmentBytes`, `WorkingDirectory`) and `Segments:Toolchain` (`FfmpegPath`, `FfprobePath`,
  `ExpectedVersionPrefix`) with validation (positive durations and counts, non-empty paths) and unit tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentOptionsTests.cs` (research R12)

### Segment store and synthetic test media

- [x] T009 [P] Define `ISegmentStore` in `src/platform/SocAlytics.Platform.Application/Segments/ISegmentStore.cs` and implement
  `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Segments/SegmentStore.cs` with Dapper: demand upsert
  (`INSERT … ON CONFLICT DO NOTHING`, re-demand of `failed` resets `attempts_used`), claim with
  `FOR UPDATE SKIP LOCKED` of demanded `absent` rows due for an attempt and of `materializing` rows with expired
  leases, publication as a compare-and-set on `candidate_id` and state, transient-failure and failure transitions,
  lookups by identity and by recording versions, waiting-job queries joined with `logical_jobs`, and purge locking;
  tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentStoreTests.cs` (state constraints,
  concurrent claims, stale candidate refusal, version trigger) (FR-005, FR-006)
- [x] T010 [P] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Support/SyntheticMedia.cs` that creates a
  10-second, 64×64, keyframe-every-second MPEG-4 MP4 with `testsrc2` through the configured `ffmpeg` in a test-owned
  directory and uploads and finalizes it through the existing recording upload handlers with a two-span or single-span
  timeline mapping; it fails visibly with a named message when the toolchain is missing, like Docker-dependent tests
  (research R11)

### Segment composition

- [x] T011 Register the Segments options (bound and validated at start), `ISegmentStore`, and the time and storage
  dependencies in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` and
  `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`; add development
  values to `src/platform/SocAlytics.Platform.Api/appsettings.Development.json` (`MaxConcurrentPerInstance` 2,
  `MaxAttempts` 3, `ExpectedVersionPrefix` matching the environment's pinned 8.1 build) and any toolchain path
  environment the AppHost passes in `src/platform/SocAlytics.Platform.AppHost/`; extend the options validation tests in
  `src/platform/Tests/SocAlytics.Platform.Host.Tests/` (research R12)

**Checkpoint**: Contracts, domain rules, persistence, storage, options, and test media exist.

---

## Phase 3: User Story 1 - A segment job that needs video gets verified segment media (Priority: P1) MVP

**Goal**: Materialize requested segments once, concurrency-safely, with valid manifests, and make video jobs ready only
when their segment is available.

**Independent Test**: Finalize the synthetic recording with a 5-second test policy and a `required` video node; both
segments become `available` once, manifests validate, and the jobs become ready only afterwards with `segmentMedia`.

### Extraction, manifest, and node snapshot

- [x] T012 [P] [US1] Implement `ISegmentExtractor` (`src/platform/SocAlytics.Platform.Application/Segments/ISegmentExtractor.cs`)
  with `src/platform/SocAlytics.Platform.Infrastructure/Segments/FfmpegSegmentExtractor.cs` and
  `src/platform/SocAlytics.Platform.Infrastructure/Segments/ToolchainProbe.cs` per research R3: input seeking on a
  presigned source read URL that is never logged, `-copyts`, `-map 0:v:0`, stream copy, no metadata, MP4 with
  `+faststart` and bit-exact flags into a private working directory, ffprobe of the first and last video packet
  timestamps, a keyframe check of the first packet, `segment-coverage-incomplete` when the encoded interval does not
  cover the requested interval, the execution timeout, `MaxSegmentBytes`, working-directory cleanup, and
  `toolchain-unavailable` when the executables are missing or their version does not start with `ExpectedVersionPrefix`,
  registering both in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`;
  tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/FfmpegSegmentExtractorTests.cs` with
  `SyntheticMedia` (covered interval, keyframe start, only one video stream, missing toolchain) (FR-003)
- [x] T013 [P] [US1] Implement `src/platform/SocAlytics.Platform.Application/Segments/SegmentManifestBuilder.cs` that builds
  the segment manifest 1.0.0 (identity, policy version and duration, nominal window, requested source interval,
  encoded interval, timeline spans, media reference, `video/mp4`, media digest and size, producer toolchain, UTC
  creation time) and its canonical bytes and SHA-256 checksum, registered in
  `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`, with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentManifestBuilderTests.cs` validating the
  output against the schema (FR-004, SC-001)
- [x] T014 [P] [US1] Add the run-pinned profile's `videoPolicy` to the node snapshot in
  `src/platform/SocAlytics.Platform.Application/Analysis/WorkflowSnapshotMapper.cs` (read from the resolved Analyst
  profile content, carrying it through `src/platform/SocAlytics.Platform.Application/Registry/ResolvedWorkflowDefinition.cs`
  and the resolvers in `src/platform/SocAlytics.Platform.Infrastructure/Registry/` when they do not expose it yet), with
  tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/WorkflowSnapshotVideoPolicyTests.cs` (FR-012)

### Materialization

- [x] T015 [US1] Implement `src/platform/SocAlytics.Platform.Application/Segments/RequestSegmentMaterialization.cs`:
  validate the identity, the policy version (`segmentation-policy-unknown` unless `1`) and the digest binding
  (`segmentation-policy-conflict`), recording-set membership and match and team scope through the Recordings lineage
  queries (`segment-scope-mismatch`), and the coverage intersection (`segment-coverage-empty`); rejections create no
  `available` row, change no existing row, and create no lineage input; accepted requests upsert demand; tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentRequestTests.cs` (FR-002, FR-007, FR-010)
- [x] T016 [US1] Implement `src/platform/SocAlytics.Platform.Application/Segments/MaterializeSegment.cs`: claim, extract,
  write media and manifest under `segments/{recordingVersionId}/{mappingDigestHex}/{policyDigestHex}/{segmentNumber}/{candidateId}/`,
  confirm the store's attested full-object SHA-256 and size equal the manifest, publish by compare-and-set in a unit of
  work, delete losing or expired candidates' objects, and treat a published association as a cache hit only after its
  attested checksum matches; tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentMaterializationTests.cs` covering manifest
  contents and digest (SC-001), 8 concurrent requests with one association and no leftover candidate objects (SC-002),
  a recording-set-only revision reusing the same association, and coexisting policy digests (FR-004 to FR-006, FR-010)
- [x] T017 [US1] Add `src/platform/SocAlytics.Platform.Infrastructure/Workers/SegmentMaterializationWorker.cs` on the
  existing `WorkerLoop` pattern with `MaxConcurrentPerInstance` parallel claims and a toolchain probe at start, and
  register it where the existing analysis workers are registered; tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentWorkerTests.cs` for processing demanded
  rows and reclaiming an expired `materializing` lease after a simulated restart without publishing the old candidate
  (FR-001, FR-006)
- [ ] T018 [US1] Gate readiness on segment media: request demand at run creation in
  `src/platform/SocAlytics.Platform.Application/Analysis/Commands/HandleRecordingsFinalizedNotification.cs` for
  segment-scoped jobs whose node `videoPolicy` is `required` or `optional` (never `forbidden`), recording a rejected
  request's reason in `logical_jobs.segment_rejection_reason` through
  `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/LogicalJobStore.cs`; in
  `src/platform/SocAlytics.Platform.Application/Analysis/RunReadinessCoordinator.cs` keep such jobs waiting while their
  segment is `absent` or `materializing`, pin `segmentMedia` (`manifestChecksum`, `manifestReference`, `mediaDigest`,
  `mediaSizeBytes`) into the job's input snapshot when `available`, emit analyst-job `1.2.0` with
  `segmentMedia { videoPolicy, manifestChecksum }`, reuse the pin for retries, and re-apply readiness for every run
  waiting on an identity when it is published (from `MaterializeSegment.cs`); tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/SegmentReadinessTests.cs` (ready only after
  `available`, payload valid against analyst-job 1.2.0, `forbidden` nodes create no demand and no `segmentMedia`
  (SC-003), result manifest contract unchanged) (FR-012, FR-015)

**Checkpoint**: US1 is independently testable: segments materialize once and gate readiness.

---

## Phase 4: User Story 2 - The Analyst Manager stages segment media for the attempt (Priority: P1)

**Goal**: Attempt-fenced grants, verified staging at `/socalytics/media/`, SDK access, and end-to-end evidence.

**Independent Test**: Run the end-to-end flow with the simulated runtime and the Docker test Analyst in `media` mode;
results are accepted, media is verified, and nothing remains after the attempt.

### Media read, Manager client, and SDK media model

- [ ] T019 [P] [US2] Implement `src/platform/SocAlytics.Platform.Application/Analysis/Results/GetSegmentMedia.cs` and map
  `GET /api/v1/analysis/attempts/{attemptId}/segment-media` (`getAnalysisSegmentMedia`) in
  `src/platform/SocAlytics.Platform.Api/Analysis/AnalystManagerEndpoints.cs` per
  `specs/20261017-222433-materialized-segments/contracts/segment-media-read.md` (ownership, fencing token, active and
  current attempt with an unexpired lease, `segment-media-not-applicable`, attested checksum and size check, two
  read-only grants expiring at the earlier of `Analysis:Results:GrantLifetime` and the lease expiry, audit events for
  foreign and out-of-scope requests, response valid against `analysis/segment-media` 1.0.0); refresh
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`;
  tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/SegmentMediaReadTests.cs`
  (stale, fenced, completed, foreign, forbidden-policy, and not-current refusals; grant expiry; no source locations)
  (FR-016, FR-017)

- [ ] T020 [P] [US2] Add `getAnalysisSegmentMedia` to the Manager platform client in
  `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs` and
  `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformWorkContracts.cs` (response validated
  against `analysis/segment-media` 1.0.0, refusals mapped like the upstream read) and parse `segmentMedia` in
  `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AnalystJobMessage.cs`, with tests in
  `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Platform/SegmentMediaClientTests.cs` (FR-016, FR-023)
- [ ] T021 [P] [US2] Extend the SDK in `src/analysts/sdk/`: copy the segment manifest schema into
  `src/analysts/sdk/src/socalytics_analyst/contracts/` (byte-identical, test-verified; analyst-job 1.2.0 with its
  `segmentMedia` model field already arrived with T002) by extending `src/analysts/sdk/scripts/sync_contracts.py`
  (and its SDK test) to include `segments/segment-manifest/v1` so the copy stays byte-identical and
  `uv run --no-sync python scripts/sync_contracts.py --check` passes, add
  `src/analysts/sdk/src/socalytics_analyst/models/segment.py` (`SegmentManifest`), add
  `src/analysts/sdk/src/socalytics_analyst/media.py` (`SegmentMedia` with `manifest`, `path`, `to_match_ms`), set
  `AnalystContext.media` in `src/analysts/sdk/src/socalytics_analyst/context.py`, and raise the input errors
  `segment-media-missing`, `segment-media-unexpected`, `segment-manifest-mismatch`, `segment-media-digest-mismatch`,
  `segment-manifest-invalid`, and `unsupported-version` (exit 2) per
  `specs/20261017-222433-materialized-segments/contracts/container-interface.md`; tests in
  `src/analysts/sdk/tests/test_media.py` and the contract example tests; run `uv run --no-sync pytest` (FR-021)

### Manager staging and mount

- [ ] T022 [US2] Implement `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/SegmentMediaStager.cs`: request
  the grants for the current attempt with lease renewal as in `UpstreamStager.cs`, download both objects to
  `{attemptDirectory}/media/` as `segment-manifest.json` and `segment.mp4`, verify the manifest's SHA-256 against
  `segmentMedia.manifestChecksum` and against the segment manifest schema, verify the media digest and size against
  the manifest, count the bytes against `Work:Staging:MaxBytesPerAttempt` together with upstream inputs, and delete the
  partial staging on any failure with the existing staging categories; tests in
  `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/SegmentMediaStagerTests.cs` (digest mismatch,
  checksum mismatch, limit exceeded, refused read, storage unavailable) (FR-018, SC-004)
- [ ] T023 [US2] Wire media staging into `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRunner.cs`
  before launch, mount `{attemptDirectory}/media/` read-only at `/socalytics/media/` only when present in
  `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Docker/DockerLaunchSpec.cs` and
  `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/AttemptExecution.cs`, and make the simulated Analyst in
  `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/SimulatedResultWriter.cs` fail when a job with
  `segmentMedia` has no verified media; extend `DockerLaunchSpecTests.cs`, `SimulatedRuntimeTests.cs`,
  `AttemptDirectoryTests.cs`, and `AttemptCleanupTests.cs` under
  `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/` (mount present only with media, other mounts and
  no-network rules unchanged, media deleted when the attempt ends and at Manager start) (FR-019, FR-020)
- [ ] T024 [US2] Add end-to-end evidence: a `media` mode in
  `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/TestAnalyst/analyst.py` that reads
  `/socalytics/media/segment-manifest.json` and `segment.mp4`, verifies the digest, and writes a result inside the
  nominal window; a manifest variant with `videoAccess` `required` and image support in
  `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs`; and
  `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/SegmentMediaEndToEndTests.cs` that finalizes the synthetic
  recording with a 5-second test policy and gets accepted results for both segments on the Docker runtime and the
  simulated runtime (FR-029, SC-006)

**Checkpoint**: US1 and US2 together deliver video to Analysts end to end.

---

## Phase 5: User Story 3 - Failures stay explicit and affect only the jobs that need the segment (Priority: P2)

**Goal**: Bounded retries without visible candidates, explicit `failed` states, contained job failures, and digest
refusal.

**Independent Test**: Inject a transient storage failure, a persistent extraction failure, and altered stored bytes;
observe recovery, contained failure, and refusal.

### Materialization failures

- [ ] T025 [US3] Implement retries and failure in
  `src/platform/SocAlytics.Platform.Application/Segments/MaterializeSegment.cs`: transient `source-unavailable`,
  `storage-unavailable`, and `extraction-failed` return the row to demanded `absent` with `next_attempt_at` after
  `RetryDelay` until `MaxAttempts`, then `failed` with `retry-budget-exhausted`; non-retryable reasons end `failed`
  immediately; candidates are never visible between attempts; a new demand for a `failed` identity starts a new cycle;
  tests with an injected extractor and storage in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentFailureTests.cs` (FR-008)
- [ ] T026 [US3] Propagate segment failure in `src/platform/SocAlytics.Platform.Application/Analysis/RunReadinessCoordinator.cs`:
  when a segment ends `failed`, or a job carries a `segment_rejection_reason`, fail waiting `required` jobs with reason
  `segment-unavailable` (blocking follows the
  normal required, optional, conditional, and partial-coverage rules) and make waiting `optional` jobs ready without
  `segmentMedia`, re-applied for every affected run in the failing unit of work; tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/SegmentFailureReadinessTests.cs` showing other
  segments' jobs reach their normal outcome (FR-013, FR-014, SC-005)
- [ ] T027 [US3] Refuse invalid bytes: when an `available` association's attested checksum or size differs on a cache
  hit in `src/platform/SocAlytics.Platform.Application/Segments/MaterializeSegment.cs` or on a media read in
  `src/platform/SocAlytics.Platform.Application/Analysis/Results/GetSegmentMedia.cs`, refuse it
  (`segment-media-invalid`), delete its objects, and set the row `failed` so it is never handed to a new job; tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentDigestRefusalTests.cs` (FR-009)

**Checkpoint**: Failures are explicit and contained.

---

## Phase 6: User Story 4 - A Club Admin purges cached segment media (Priority: P3)

**Goal**: List and purge a match's materialized segments without changing logical identity.

**Independent Test**: List, purge, check objects and states, and rematerialize through a new run; non-admins are
refused and in-use segments are kept.

### List and purge

- [ ] T028 [US4] Implement `src/platform/SocAlytics.Platform.Application/Segments/ListMaterializedSegments.cs`,
  `src/platform/SocAlytics.Platform.Application/Segments/PurgeMaterializedSegments.cs`, and
  `src/platform/SocAlytics.Platform.Application/Segments/SegmentAuditEvents.cs` per
  `specs/20261017-222433-materialized-segments/contracts/member-operations.md`: Club Admin authorization through
  `IAccessAuthorizer.AuthorizeClubAsync` for the match's club, listing of rows whose recording version belongs to the
  match's finalized recording sets with `inUse`, purge that skips rows that are `materializing` or pinned or awaited by
  `Waiting`, `Ready`, or
  `Claimed` jobs of non-terminal runs, deletes objects, returns rows to `absent` without demand, is idempotent, and
  writes `segments.purged`; tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentPurgeTests.cs` (in-use kept, idempotent
  repeat, rematerialization of a purged segment for a later run with the same validation (SC-007)) and
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/SegmentAccessTests.cs` (Coach, Viewer, and other
  club's Club Admin refused without disclosure) (FR-011, FR-024 to FR-027)
- [ ] T029 [US4] Map `listMaterializedSegments` and `purgeMaterializedSegments` with `MapMemberApi()` and
  `ProblemResults` in `src/platform/SocAlytics.Platform.Api/Segments/MaterializedSegmentEndpoints.cs`, call the new
  mapping method from `src/platform/SocAlytics.Platform.Api/Program.cs`, refresh
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`,
  and add HTTP-level tests (anti-forgery on purge, `404`/`403` refusals, response shapes without object keys) in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Segments/MaterializedSegmentEndpointTests.cs` (FR-024 to
  FR-027)

**Checkpoint**: All user stories are independently functional.

---

## Phase 7: Polish & Cross-Cutting Concerns

### Documentation

- [ ] T030 [P] Make `docs/architecture/` describe the merged state: re-read the narratives and every status statement
  listed in the plan's Architecture Updates (On-Demand Video Segmentation and Segment Contract, Implementation
  Validation, Readiness And Dependencies, Runtime Preflight And Status and Analyst Manager Responsibilities,
  Capability-Scoped Result Access, Contracts and Compatibility Scope And Dependencies, Data Organization, section 4 of
  Terminology and Principles, POL-004) and correct every statement this feature makes false, including planned,
  future-tense, or "decided, not yet implemented" wording anywhere in `docs/architecture/`, treating the plan's
  rewrites as examples rather than the whole job; keep storage cost, policy migration at scale, POL-004, and production
  toolchain and storage values explicitly unresolved; run `node .github/scripts/check-markdown.mjs` (FR-030)
- [ ] T031 [P] Update `README.md` (toolchain prerequisite for the API host and toolchain-dependent tests), `AGENTS.md`
  (Current State: Segment Service, media mount, segment options, member and Manager operations, the first Manager
  version that stages media and the minimum Manager version to set), `src/platform/README.md`,
  `src/analysts/README.md`, `src/analysts/sdk/README.md` (media access with an example), and the Manager documentation
  under `src/analysts/manager/`; run `node .github/scripts/check-markdown.mjs` (FR-030)

### Validation

- [ ] T032 Run the quickstart validation: `dotnet restore`, `dotnet build --no-restore`, and
  `dotnet test --no-build` for `src/platform/SocAlytics.Platform.slnx` and
  `src/analysts/manager/SocAlytics.Analysts.Manager.slnx`, `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`,
  `uv run --no-sync pytest` in `src/analysts/sdk`, and `npm ci`, `npm run build`, and `npm test` in `src/clients/web` (the
  OpenAPI snapshot changed); fix failures in the files of the tasks above

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: T001 and T002 in parallel, then T003 (references the schema of T001), then T004.
- **Foundational (Phase 2)**: T005–T008 in parallel after Phase 1; then T009 (needs T005, T006) and T010 (needs the
  existing upload handlers) in parallel; then T011 (needs T008, T009).
- **US1 (Phase 3)**: T012–T014 in parallel (T012 uses T010); then T015, T016 (needs T012, T013, T015), T017 (needs T016), T018 (needs
  T014, T016).
- **US2 (Phase 4)**: T019 (needs T018), T020, and T021 in parallel; T022 (needs T020), T023 (needs T022), T024 (needs
  T019, T021, T023).
- **US3 (Phase 5)**: T025 (needs T016), T026 (needs T018, T025), T027 (needs T019, T025).
- **US4 (Phase 6)**: T028 (needs T009, T016), T029 (needs T028; after T019 because both refresh `openapi/v1.json`).
- **Polish (Phase 7)**: T030 and T031 in parallel after all stories; T032 last.

### User Story Dependencies

- US1 is the MVP and depends only on the foundation.
- US2 depends on US1 (it stages the pinned association).
- US3 extends US1 and US2 behavior (retries, failure propagation, digest refusal).
- US4 depends on the foundation and US1 (rows to list, rematerialization).

## Parallel Example

```text
Phase 1:  T001 segment-manifest schema | T002 analyst-job 1.2.0
Phase 2:  T005 domain | T006 migration | T007 object-storage put | T008 options
Phase 2:  T009 segment store | T010 synthetic test media
Phase 3:  T012 extractor | T013 manifest builder | T014 node snapshot videoPolicy
Phase 4:  T019 media read operation | T020 Manager client | T021 SDK media model
Phase 7:  T030 architecture docs | T031 READMEs and AGENTS.md
```

## Implementation Strategy

1. **MVP**: Phases 1–3 (US1): segments materialize once and gate readiness; validate with the US1 independent test.
2. **Delivery**: Phase 4 (US2) brings media to Analysts; validate end to end.
3. **Hardening**: Phase 5 (US3) failure containment; Phase 6 (US4) purge.
4. **Finish**: Phase 7 documentation and full validation.
