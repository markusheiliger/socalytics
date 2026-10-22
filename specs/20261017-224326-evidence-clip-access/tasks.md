---

description: "Task list for Evidence Clip Access"
---

# Tasks: Evidence Clip Access

**Input**: Design documents from `specs/20261017-224326-evidence-clip-access/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required. FR-024, FR-025, and SC-001 to SC-006 require automated evidence; test work is part of each task.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files never
  overlap.
- **[Story]**: Which user story this task belongs to (US1 to US4)
- Paths are repository-relative. Materialized Video Segments, Accepted Fact Indexing, and Web UI Connected Analysis
  Review are merged before this feature; their files (for example `ISegmentStore`, `RequestSegmentMaterialization`,
  `IFactQueries`, `FactBrowser.tsx`, `models/analysis.ts`) exist.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The validated `Analysis:Evidence` options every later task reads (FR-016).

### Evidence options

- [x] T001 Create `src/platform/SocAlytics.Platform.Application/Analysis/Evidence/EvidenceOptions.cs` for section
  `Analysis:Evidence` with `GrantLifetime` (`00:00:30`..`01:00:00`), `RetryAfter` (`00:00:01`..`00:05:00`),
  `ResolutionsPerMinute` (`1`..`10000`), `MaxDuration` (`00:00:01`..`01:00:00`), and `MaxContext`
  (`00:00:00`..`00:01:00`), all required and validated at start; bind it in
  `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidenceOptionsValidationTests.cs` for
  missing and out-of-range values; because the options are required and validated at start, the same task also sets
  the values `GrantLifetime` `00:05:00`, `RetryAfter` `00:00:05`, `ResolutionsPerMinute` `60`, `MaxDuration`
  `00:10:00`, and `MaxContext` `00:00:10` as `Analysis:Evidence:*` in every test settings dictionary that configures
  the API host, so the Architecture and Host tests that verify this task still start:
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/Support/TestIdentityAccessSettings.cs`,
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/RecordingOptionsValidationTests.cs`,
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/AnalysisReadOptionsTests.cs`,
  `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/EndpointSecurityTests.cs`, and
  `src/platform/Tests/SocAlytics.Platform.Host.Tests/MinimumWebVersionTests.cs` (and any other host settings found by
  searching for `Analysis:Reads:GrantLifetime`); run the Host and Architecture tests (FR-016; research R9)
- [x] T002 Set the same development values as `Analysis__Evidence__*` in
  `src/platform/SocAlytics.Platform.AppHost/Program.cs` and as `Analysis:Evidence:*` in any API host settings
  dictionary or launch configuration T001 did not cover (searching for `Analysis:Reads:GrantLifetime`), and confirm
  that the Host and Architecture tests pass (FR-016; research R9)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Coverage planning, fact lookup, offsets, and the Web UI media origin and test media that every story
uses.

### Planner, fact lookup, and offsets

- [x] T003 [P] Create the pure Domain planner
  `src/platform/SocAlytics.Platform.Domain/Analysis/Evidence/EvidenceCoveragePlanner.cs` and
  `EvidencePartState.cs` (`available`, `materializing`, `rematerializing`, `failed`, `not-covered`): from a half-open
  requested range, the run's recording-set members in membership order (recording version id, timeline-mapping
  digest, mapped spans), and the policy duration `D`, assign each sub-window to the first member whose spans cover it,
  split at nominal boundaries `[(n-1)·D, n·D)` and span boundaries, and return ordered, contiguous, non-overlapping
  parts with identity and span, or `not-covered`; include the fact-window extension by context clipped at zero and the
  shortening to `MaxDuration` with `shortened = true`, reusing the Segments Domain window calculator; add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidenceCoveragePlannerTests.cs`
  (inside one segment, across a boundary, start and end exactly at boundaries, span gaps, several recordings with
  overlapping and disjoint coverage, context at zero, shortening) (FR-001–FR-005; research R2)
- [x] T004 [P] Add `GetRunFactAsync(runId, acceptedResultId, ordinal)` returning the fact's `startMs` and `endMs` or
  null to `src/platform/SocAlytics.Platform.Application/Analysis/Facts/IFactQueries.cs` and
  `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/FactQueries.cs` (reading
  `analysis_indexed_facts` filtered by `run_id`); add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/RunFactLookupTests.cs` (found, other
  run, unknown ordinal) (FR-001, FR-013)
- [x] T005 [P] Create `src/platform/SocAlytics.Platform.Application/Analysis/Evidence/SegmentManifestOffsets.cs`:
  validate a manifest document against `contracts/segments/segment-manifest/v1/` and translate a part's half-open
  match-time window to `startOffsetMs` and `endOffsetMs` with
  `offset(t) = span.mediaStartMs + (t − span.matchStartMs) − encodedInterval.startMs`, refusing windows outside one
  span; add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/SegmentManifestOffsetsTests.cs`
  with the manifest examples of `contracts/segments/segment-manifest/v1/examples/` (FR-006; research R5)

### Web UI media origin and test media

- [ ] T006 [P] Change `media-src` to `blob: <object-storage-origin>` when `SOCALYTICS_OBJECT_STORAGE_ORIGIN` is set
  (else `blob:`) in both policies of `src/clients/web/vite.config.ts`, and update the expected preview policy in
  `src/clients/web/tests/e2e/simulated/headers.spec.ts` (FR-022; contracts/media-origin-and-storage.md)
- [x] T007 [P] Extend `src/clients/web/tests/e2e/support/fakeObjectStorage.mjs` and `fakeObjectStorage.d.mts` with
  object seeding (key, bytes, content type), `GET` and `HEAD` with `Range` (`206`, `Content-Range`,
  `Accept-Ranges: bytes`), `403` after the query's expiry, and CORS headers only for the configured origin, keeping
  `PUT` unchanged; extend `src/clients/web/tests/unit/support/fakeObjectStorage.test.ts` (FR-022;
  contracts/media-origin-and-storage.md)
- [x] T008 [P] Create `src/clients/web/tests/e2e/support/syntheticVideo.ts` that generates, once per test run, a
  10-second 64×64 `testsrc2` VP9 MP4 (`libvpx-vp9`, keyframe every second, `+faststart`) with the `ffmpeg` on `PATH`,
  failing visibly with a named message when `ffmpeg` or `libvpx-vp9` is missing (research R12)

---

## Phase 3: User Story 1 - Resolve a fact's evidence to short-lived media access (Priority: P1) MVP

**Goal**: A reader resolves a fact or range of a run to ordered parts with links, digests, and offsets; links are
short-lived, never stored or logged, audited, authorized per request, and rate-limited.

**Independent Test**: The spec's US1 independent test over a materialized synthetic recording.

### Resolution

- [ ] T009 [US1] Create `src/platform/SocAlytics.Platform.Application/Analysis/Evidence/ResolveEvidenceMedia.cs` and
  `EvidenceAuditEvents.cs`: validate the request (exactly one of `fact` and `range`; `fromMs ≥ 0`, `toMs > fromMs`,
  range at most `MaxDuration`; context `0..MaxContext`; `ordinal ≥ 0`), authorize with
  `IAccessAuthorizer.AuthorizeTeamResourceAsync(new("analysis-run", runId), TeamPermission.Read)` on every call
  (not found otherwise), resolve the fact with `GetRunFactAsync` (not found when missing), load the run's
  recording-set members through `IRecordingLineageQueries` and the pinned segmentation policy from the run's workflow
  snapshot, plan the parts (T003), and for `available` records confirm the attested media SHA-256 and size and the
  manifest checksum, compute offsets (T005), and issue `PresignGetObject(mediaObjectKey, now + GrantLifetime)` once per
  segment; report other records with their current state; record `analysis.evidence-media.granted` through
  `IAuditTrail.RecordAsync` with actor, club, team, match, run, fact reference or range, requested window,
  `shortened`, media digests, and latest expiry, never the link, only when a link is issued; register the handler in
  `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (FR-001–FR-008,
  FR-012–FR-014; research R3–R7)

### Endpoint and test support

- [ ] T010 [P] [US1] Map `POST /api/v1/analysis/runs/{runId}/evidence-media` (`resolveAnalysisEvidenceMedia`, tag
  `Analysis evidence`) with `MapMemberApi()` in the new
  `src/platform/SocAlytics.Platform.Api/Analysis/AnalysisEvidenceEndpoints.cs` (`Cache-Control: no-store`, no `ETag`,
  problems `400`, `401`, `404`, `429`, `503` as in contracts/evidence-media-api.md); add the rate-limiter policy
  `analysis-evidence-media` partitioned by `SessionClaimTypes.AccountId` with `ResolutionsPerMinute` permits per
  minute and its own rejection (`429`, problem `evidence-rate-limited`, `Retry-After`, `Cache-Control: no-store`) in the
  new `src/platform/SocAlytics.Platform.Api/Analysis/EvidenceRateLimiting.cs`; register both and require the policy in
  `src/platform/SocAlytics.Platform.Api/Program.cs`; refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json`
  with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter
  OpenApiSnapshotTests` and run the Host and Architecture tests (FR-015, FR-025; research R1, R8)
- [ ] T011 [P] [US1] Create the shared test support
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/Support/EvidenceScenario.cs`: a club,
  team, and match with members per role, a finalized set of the synthetic recording of Materialized Video Segments'
  `SyntheticMedia` (and optionally a second overlapping recording), a run with indexed facts inside one segment and
  across a boundary under a 5-second test policy, helpers that materialize segments through the Segment Service, and the
  existing `CapturingLoggerProvider` wired into the API host (FR-024)

### Resolution tests

- [ ] T012 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidenceResolutionTests.cs`:
  one piece inside a segment and two across a boundary in match-time order covering the range without gap or overlap;
  offsets checked with `ffprobe` packet timestamps of the stored media to within one frame; the link downloads bytes
  whose SHA-256 equals `mediaDigest` and stops working after its expiry; context extension, clipping at zero, and
  shortening; first-recording selection with two recordings; validation problems (SC-001; FR-001–FR-006)
- [ ] T013 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidenceAuthorizationTests.cs`:
  Club Admin, Coach, and Viewer allowed; members of other teams or clubs, missing runs, and facts of other runs
  receive `404`; a Coach whose role is revoked after a successful resolution receives `404` with no link; no session
  receives `401` (SC-003; FR-013)
- [ ] T014 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidenceAuditAndLoggingTests.cs`:
  one `analysis.evidence-media.granted` event per resolution with links and its fields; none without links; no link,
  `X-Amz-Signature`, or object key in audit details, captured logs, or stored rows (SC-002; FR-012, FR-014)
- [ ] T015 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidenceRateLimitTests.cs`
  with a low `ResolutionsPerMinute`: the request over the limit receives `429` `evidence-rate-limited` with
  `Retry-After`, issues no link, records no demand, and the limit is per member (FR-015)

---

## Phase 4: User Story 2 - Explicit states when evidence media is not ready (Priority: P1)

**Goal**: Absent, purged, failed, invalid, and uncovered segments are reported explicitly; absent and purged ones are
requested from the Segment Service.

**Independent Test**: The spec's US2 independent test.

- [ ] T016 [US2] Extend `src/platform/SocAlytics.Platform.Application/Analysis/Evidence/ResolveEvidenceMedia.cs` with
  the part state mapping of data-model.md: record demand through `RequestSegmentMaterialization` (with the run's
  recording-set version for scope validation) in the resolution's unit of work for missing or `absent` records
  (`materializing`, or `rematerializing` when the record has a purge time), report `materializing` records (or
  `rematerializing` after a purge) without a new request, report `failed` with its sanitized reason and request again
  only with `retryFailed`, map rejected requests to `failed` with the rejection reason, hand a failed integrity check
  to the Segment Service's invalid-media handling (`segment-media-invalid`), add `retryAfterSeconds` from `RetryAfter`
  to preparing parts, never request demand for `not-covered` parts, and fail the resolution with `503`
  `evidence-storage-unavailable` when storage cannot confirm a checksum or read a manifest (FR-005, FR-008–FR-011;
  research R4, R5)

### State tests and end to end

- [ ] T017 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Evidence/EvidencePartStateTests.cs`:
  never-materialized segment reported `materializing` then available with a link after the worker ran; purged segment
  reported `rematerializing`; `failed` without and with `retryFailed`; `not-covered` without demand; altered stored
  bytes refused, segment `failed`, objects deleted; mixed available and preparing parts; two members resolving the
  same absent segment concurrently end with one association; storage unavailable gives `503` without links (SC-004;
  FR-007–FR-011)
- [ ] T018 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/EvidenceMediaEndToEndTests.cs`: after
  the development workflow completes over an uploaded synthetic recording, a Coach resolves an indexed fact, waits
  for `materializing` parts to become available within a bounded time, and downloads media whose digest equals the
  returned `mediaDigest` (FR-024)

---

## Phase 5: User Story 3 - Play a fact's evidence in the analysis review (Priority: P2)

**Goal**: "Play evidence" plays the fact's evidence with context across segments, with explicit states and renewal.

**Independent Test**: The spec's US3 independent test in the simulated and platform browser projects.

### Adapter and timeline

- [ ] T019 [US3] Add the evidence resolution, part, and media-piece models to
  `src/clients/web/src/platform/models/analysis.ts` and the new adapter
  `src/clients/web/src/platform/adapters/analysisEvidenceAdapter.ts` (`resolve(runId, request)` over
  `resolveAnalysisEvidenceMedia` with outcomes resolution, `notFound`, `rateLimited` with delay,
  `storageUnavailable`, `offline`, `error`; unknown states kept raw); add
  `src/clients/web/tests/unit/platform/adapters/analysisEvidenceAdapter.test.ts` (FR-018; contracts/evidence-player.md)
- [ ] T020 [US3] Create `src/clients/web/src/features/analysis/evidenceTimeline.ts` (piece order, seek target from
  the first seekable time plus `startOffsetMs`, stop at `endOffsetMs`, match-time position, inside-fact test, expiry
  margin of 30 seconds) and `src/clients/web/tests/unit/features/analysis/evidenceTimeline.test.ts` (FR-018, FR-020)

### Player

- [ ] T021 [US3] Create `src/clients/web/src/features/analysis/useEvidencePlayback.ts` and
  `src/clients/web/tests/unit/features/analysis/useEvidencePlayback.test.ts`: the reducer-based state machine of
  data-model.md (`resolving`, `playing`, `preparing` with at most 20 automatic re-resolutions after
  `retryAfterSeconds`, `partUnavailable`, `shortened`, `renewing`, `notFound`, `rateLimited`, `offline`, `error`),
  fact requests with 5 seconds of context on each side, "Try again" with `retryFailed` only for failed parts, and one
  re-resolution with resumption at the current match time on expiry or media error; resolutions and links in memory
  only (FR-018–FR-020)
- [ ] T022 [US3] Create `src/clients/web/src/features/analysis/EvidencePlayerDialog.tsx` (dialog with title, focus
  trap, Escape, polite live region, two alternating `<video crossorigin="anonymous" preload="auto" playsinline>`
  elements, inside-fact marker, release of sources on close), add the "Play evidence" button with an accessible name
  including the fact's match-clock range to each fact row in `src/clients/web/src/features/analysis/FactBrowser.tsx`,
  add the `evidence.*` keys to `src/clients/web/src/i18n/en/analysis.json`, and add
  `src/clients/web/tests/unit/features/analysis/EvidencePlayerDialog.test.tsx` (FR-017–FR-021;
  contracts/evidence-player.md)

### Browser tests

- [ ] T023 [P] [US3] Add the `resolveAnalysisEvidenceMedia` route to `src/clients/web/tests/e2e/support/simulatedApi.ts`
  (scripted resolutions with links to objects seeded from `syntheticVideo.ts` into the fake store) and
  `src/clients/web/tests/e2e/simulated/evidence-playback.spec.ts`: fact inside one segment, across two segments
  without member action, preparing then available, failed and not-covered parts, shortened notice, expiry during
  playback with renewal and resumption, a link that stops working before its expiry (purged segment) leading to
  re-resolution and the `rematerializing` state, not found, rate limited, keyboard operation, and axe checks for every
  state
  (SC-005, SC-006; FR-017–FR-021)
- [ ] T024 [P] [US3] Add `src/clients/web/tests/e2e/platform/evidence-playback.spec.ts` and chain it after the analysis
  review project in `src/clients/web/playwright.config.ts`: upload and finalize a `syntheticVideo.ts` recording, open
  the completed run's review, choose "Play evidence" on a fact, and observe playback reaching the fact's window
  within one frame of the expected media time (SC-001, SC-005; FR-024)

---

## Phase 6: User Story 4 - Media playback stays confined to the object store and the session (Priority: P3)

**Goal**: Media loads only from the object-store origin for the Web UI origin, and nothing is cached or stored.

**Independent Test**: The spec's US4 independent test.

### Confinement checks

- [ ] T025 [P] [US4] Extend `src/clients/web/tests/unit/serviceWorker/shellWorker.test.ts`: a `GET` with `Range` to the
  object-store origin stays unhandled by the service worker (FR-023)
- [ ] T026 [P] [US4] Add `src/clients/web/tests/e2e/simulated/evidence-confinement.spec.ts`: a page of another origin
  receives no CORS access to seeded media while the Web UI origin plays it, no `securitypolicyviolation` occurs during
  playback, the offline browser shows the connection-required state on "Play evidence", and after a playback session
  service-worker caches, `localStorage`, `sessionStorage`, and IndexedDB contain no media, link, or resolution
  (SC-006; FR-022, FR-023)
- [ ] T027 [P] [US4] Extend `src/clients/web/tests/e2e/platform/evidence-playback.spec.ts`: a request for the played
  media with another `Origin` receives no `Access-Control-Allow-Origin` for it from the development object store
  (FR-022)

---

## Phase 7: Polish & Cross-Cutting Concerns

### Documentation

- [ ] T028 [P] Make `docs/architecture/` describe the merged state: re-read the narratives and every status statement
  listed in the plan's Architecture Updates (Capability-Scoped Result Access, the `get_video_clip()` sentence of the
  Analyst Capability Catalog, MCP Tools, Client Applications Web UI, the Segment Service paragraph of the Segment
  Contract, section 4 of Terminology and Principles, Audit Events, and the Web UI security baseline) and correct every
  statement this feature makes false, including planned, future-tense, or "decided, not yet implemented" wording
  anywhere in `docs/architecture/`, treating the plan's rewrites as examples rather than the whole job; keep the MCP
  tool, production link lifetimes, rate limits, CDN delivery, and the production storage origin explicitly unresolved;
  run `node .github/scripts/check-markdown.mjs` (FR-025)
- [ ] T029 [P] Update `AGENTS.md` (Current State: the evidence-media operation, Web UI evidence playback, the
  `Analysis:Evidence` options), `README.md` (the toolchain prerequisite for the Web UI evidence browser tests),
  `src/platform/README.md` (operation, options, rate limit, audit event), and `src/clients/README.md` (evidence
  playback, synthetic VP9 test media); run `node .github/scripts/check-markdown.mjs` (FR-025)

### Validation

- [ ] T030 Run the quickstart validation: `dotnet restore`, `dotnet build --no-restore`, and `dotnet test --no-build`
  for `src/platform/SocAlytics.Platform.slnx`, and `npm ci`, `npm run build`, `npm test`, and `npm run test:e2e` with
  the `simulated` and `platform` projects in `src/clients/web`; fix failures in the files of the tasks above

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: T001, then T002.
- **Foundational (Phase 2)**: T003–T005 in parallel after Phase 1; T006–T008 in parallel.
- **US1 (Phase 3)**: T009 (needs T003–T005); then T010 and T011 in parallel (both need T009); then T012–T015 in parallel.
- **US2 (Phase 4)**: T016 (needs T009), then T017 and T018 in parallel.
- **US3 (Phase 5)**: T019 and T020 in parallel (T019 needs the snapshot of T010); T021 (needs T019, T020), T022 (needs
  T021), T023 (needs T006–T008, T022), T024 (needs T016, T022).
- **US4 (Phase 6)**: T025–T027 in parallel after US3 (T027 extends the file of T024).
- **Polish (Phase 7)**: T028 and T029 in parallel after all stories; T030 last.

### User Story Dependencies

- US1 is the MVP and depends only on the foundation.
- US2 extends US1's handler with on-demand requests and explicit states.
- US3 depends on US1 (operation and snapshot) and, for the platform test, on US2 (on-demand materialization).
- US4 depends on US3's player and on the foundation's security policy.

## Parallel Example

```text
Phase 2:  T003 planner | T004 fact lookup | T005 manifest offsets
Phase 2:  T006 security policy | T007 fake object store | T008 synthetic video
Phase 3:  T010 endpoint and rate limit | T011 test support
Phase 3:  T012 resolution | T013 authorization | T014 audit and logs | T015 rate limit
Phase 4:  T017 part states | T018 end to end
Phase 5:  T023 simulated playback | T024 platform playback
Phase 6:  T025 service worker | T026 simulated confinement | T027 platform CORS
Phase 7:  T028 architecture docs | T029 READMEs and AGENTS.md
```

## Implementation Strategy

1. **MVP**: Phases 1–3 (US1): resolution with links, audit, authorization, and rate limit; validate with the US1
   independent test.
2. **Delivery**: Phase 4 (US2) explicit states and on-demand materialization; Phase 5 (US3) the player.
3. **Hardening**: Phase 6 (US4) confinement checks.
4. **Finish**: Phase 7 documentation and full validation.
