---

description: "Task list for Web UI Match Workflow and Recording Upload"
---

# Tasks: Web UI Match Workflow and Recording Upload

**Input**: Design documents from `specs/20261013-112844-web-ui-match-upload/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/routes-and-roles.md](contracts/routes-and-roles.md), [contracts/platform-adapter.md](contracts/platform-adapter.md), [contracts/upload-worker.md](contracts/upload-worker.md), [contracts/security-headers.md](contracts/security-headers.md), [contracts/development-storage.md](contracts/development-storage.md), [quickstart.md](quickstart.md)

**Tests**: Required. The spec requires automated coverage of every acceptance scenario (SC-002) and the constitution requires focused tests. Every task adds the tests for the code it creates in the same task, so `npm run build`, `npm test`, `npm run test:e2e`, and the platform test suites stay green after each task. Browser tests in the `simulated` project use the foundation's fixtures (axe WCAG 2.2 AA on every view, failure on any CSP violation, no secrets in browser storage), extended by T001 to assert that no grant URL or signature is stored.

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Web UI Foundation `specs/archive/20261012-150657-web-ui-foundation` (the `src/clients/web/` project, session and guards, shell, the platform adapters and `PlatformFailure`, `simulatedApi.ts` and fixtures, the AppHost Web UI resource and `e2e` mode, the CSP and SRI, the team views), the Web UI verification environment `specs/archive/20261012-145505-web-ui-environment`, and the recording, club, and analysis APIs from `specs/archive/20261005-130702-recording-lineage-upload`, `specs/archive/20261005-130701-club-identity-foundation`, and `specs/archive/20261005-130703-durable-analysis-workflow`. This feature changes no platform API and no migration. Every dependency added to `src/clients/web/package.json` is pinned to an exact version.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US5)
- Every task names its exact file paths

## Path Conventions

- Web UI project: `src/clients/web/` with `src/` (application), `src/uploads/` (upload engine and worker), `tests/unit/` (Vitest), `tests/e2e/simulated/`, `tests/e2e/platform/`, `tests/e2e/support/` (Playwright).
- Platform: `src/platform/SocAlytics.Platform.AppHost/Program.cs`, `src/platform/Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Test infrastructure for direct-to-storage uploads.

- [x] T001 Upload test infrastructure (research.md § R14; contracts/development-storage.md § Simulated tests; SC-003, SC-007): add `fake-indexeddb` (exact version, dev dependency) to `src/clients/web/package.json` and `package-lock.json`; add `src/clients/web/tests/e2e/support/fakeObjectStorage.mjs` (a Node HTTP server on a fixed local port that answers CORS preflights only for the preview origin, accepts `PUT` of parts, verifies `x-amz-checksum-sha256` (base64 SHA-256) against the body and refuses mismatches with `400 BadDigest`, records stored parts, and exposes a control endpoint for inspection and fault injection: fail part *n* once or always, delay responses, refuse with `403` as an expired grant); add `tests/e2e/support/testFiles.ts` (writes deterministic video files of a given size, for example 40 MiB = three 16 MiB parts and 1 GiB, into the test's temporary folder with a `video/mp4` name); in `src/clients/web/playwright.config.ts` start the fake store as a second `webServer` for the `simulated` project and pass its origin to the preview server as `SOCALYTICS_OBJECT_STORAGE_ORIGIN`; extend `tests/e2e/support/simulatedApi.ts` with fixtures and handlers for matches, upload sessions and grants (pointing to the fake store), completion, timeline mappings, recording-set versions, lineage, and analysis runs, plus a request log; extend `tests/e2e/support/fixtures.ts` so the after-test storage check also fails when IndexedDB holds a grant URL, `X-Amz-Signature`, or `x-amz-checksum-sha256` value. Add `tests/unit/support/fakeObjectStorage.test.ts` for the fake store's checksum verification and fault injection.

**Checkpoint**: The Web UI check passes with the extended test support.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Adapters, the pure upload core, the development storage access, the CSP additions, the upload worker, and the coordinator. No user story starts before this phase is complete.

### Adapters, upload core, storage access, and CSP

- [x] T002 [P] Platform adapters (contracts/platform-adapter.md; research.md § R8; FR-013, FR-025): add `src/clients/web/src/platform/adapters/matchAdapter.ts`, `recordingUploadAdapter.ts`, `recordingLineageAdapter.ts`, and `analysisRunAdapter.ts` with the operations and operation ids of the contract, mapping generated types to the Web UI's models in `src/clients/web/src/platform/models/recordings.ts` and `src/platform/models/matches.ts` (`homeAway` is "`home`, `away`, or `neutral`"); every idempotent operation takes the caller's `Idempotency-Key` and never generates one; extend `src/clients/web/src/platform/failure.ts` with every row of the contract's error-mapping table (`seasonArchived`, `tooLarge`, `unsupportedType`, `uploadExpired`, `uploadCompleted`, `uploadPartsRefused`, `uploadIntegrityFailed`, `storageUnavailable`, `programmingError`, span-level `validation`) and their translation keys in `src/clients/web/src/i18n/en/common.json`. Unit tests in `src/clients/web/tests/unit/platform/adapters/` with the fake request adapter for each operation and each new mapping row.
- [x] T003 [P] Upload core (research.md § R2, § R4, § R5; data-model.md § Upload job): add `src/clients/web/src/uploads/partPolicy.ts` (part size 16 MiB raised to the next whole MiB that keeps the part count at 10 000 or below; a file of up to 16 MiB is one part whose part size equals the file size), `src/uploads/digests.ts` (`sha-256:<64 lowercase hex>` part digests, base64 conversion, and the composite `sha-256-parts:<partSize>:<partCount>:<hex>` as SHA-256 over the concatenated raw part digests), `src/uploads/retryPolicy.ts` (retryable: network error, `408`, `429`, `5xx`; 5 attempts after 1, 2, 4, 8, 16 s with ±20 % jitter; `400 BadDigest` is non-retryable `fileChanged`; `403` from storage triggers one re-grant), `src/uploads/grantScheduler.ts` (at most 32 parts ahead; renew grants expiring within 60 s), and `src/uploads/uploadJob.ts` (the job states and reasons of data-model.md § States with allowed transitions). Unit tests in `src/clients/web/tests/unit/uploads/core/`, including the platform's composite vectors from `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/CompositeDigestTests.cs` and part-count boundaries.
- [x] T004 [P] Development storage access in the AppHost (contracts/development-storage.md; research.md § R11; FR-027): in `src/platform/SocAlytics.Platform.AppHost/Program.cs` set `RUSTFS_CORS_ALLOWED_ORIGINS` on the `rustfs` container to the Web UI endpoint's origin and `SOCALYTICS_OBJECT_STORAGE_ORIGIN` on the Web UI resource to the `rustfs` `s3` endpoint's origin, in the default and the `e2e` mode; extend `src/platform/Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs` to assert both settings in the AppHost model. Prove the allow-list early: add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/DevelopmentCorsTests.cs`, which starts its own `rustfs/rustfs:1.0.1` container (not the shared `RustFsContainerFixture`) with the variable set to a test origin and asserts that a preflight for `PUT` with request header `x-amz-checksum-sha256` on a presigned part URL succeeds for that origin and is refused for another origin. If the variable does not work for presigned `PUT`, implement the fallback of contracts/development-storage.md instead: a `DevelopmentCorsOrigins` option in `src/platform/SocAlytics.Platform.Infrastructure/ObjectStorage/ObjectStorageOptions.cs`, one S3 `PutBucketCors` rule applied by `src/platform/SocAlytics.Platform.Infrastructure/ObjectStorage/DevelopmentBucketInitializer.cs` when `EnsureBucketOnStartup` is `true`, the AppHost setting `ObjectStorage__DevelopmentCorsOrigins` instead of the variable, and the same test against the bucket rule.
- [ ] T005 [P] CSP additions and the worker policy (contracts/security-headers.md; research.md § R9, § R11, § R12; FR-026, SC-008): in `src/clients/web/vite.config.ts` build the preview and development policies with `media-src blob:`, `worker-src 'self'`, `connect-src 'self' <SOCALYTICS_OBJECT_STORAGE_ORIGIN>` (only `'self'` when the variable is absent), and `trusted-types default react-dom upload-worker`; add `src/clients/web/src/uploads/workerPolicy.ts` (Trusted Types policy `upload-worker` whose `createScriptURL` returns only the bundled worker URL and throws otherwise; the allowed URL is passed in, so the policy does not import the worker module). Unit tests in `src/clients/web/tests/unit/uploads/workerPolicy.test.ts`; update `src/clients/web/tests/e2e/simulated/headers.spec.ts` to assert the policy of the contract with the fake storage origin.

### Upload worker

- [ ] T006 Upload worker (contracts/upload-worker.md; research.md § R1–R6; data-model.md § Journal record; FR-006, FR-007, FR-008, FR-009, FR-010, FR-014): add `src/clients/web/src/uploads/worker/protocol.ts` (the message types of the contract), `src/uploads/createUploadWorker.ts` (creates the module worker from its bundled URL through the `upload-worker` policy of T005), `src/uploads/worker/uploadWorker.ts` (message dispatch), `src/uploads/worker/hashing.ts` (`prepare` and `verify`, one part at a time with `crypto.subtle`, progress, cancellation), `src/uploads/worker/transfer.ts` (`XMLHttpRequest` `PUT` of `Blob.slice(start, end, '')` with only the grant's `x-amz-checksum-sha256` header, four parts in parallel, byte progress, the retry policy, `needGrants`, pause, resume, abort, Web Locks `socalytics-upload-slot` and `socalytics-upload:<uploadSessionId>`), and `src/uploads/worker/journal.ts` (IndexedDB `socalytics-upload-journal`, store `uploads`, key `[memberId, uploadSessionId]`, index `[memberId, matchId]`; fields exactly as data-model.md § Journal record; "Never contains grant URLs, signatures, required headers, session secrets, or file contents"; `purgeMember` aborts the member's transfers and deletes all their records). The worker imports nothing from `src/platform/`. Unit tests in `src/clients/web/tests/unit/uploads/worker/` with `fake-indexeddb`, a fake `XMLHttpRequest`, and fake locks, including an import-boundary test.

### Coordinator and repository

- [ ] T007 Upload coordinator and repository (contracts/upload-worker.md; contracts/platform-adapter.md; research.md § R7, § R8, § R10; FR-005, FR-011, FR-013, FR-015, FR-016, FR-017): add `src/clients/web/src/uploads/uploadCoordinator.ts` (main-thread owner of upload jobs: refuses files whose `type` does not start with `video/`, including an empty type; writes the journal record with the start key before calling `start`; answers `needGrants` through `RecordingUploadAdapter.issueGrants`; completes with the completion key once every part is confirmed and a mapping exists; maps completion refusals as research.md § R7 describes; pauses on unreachable, storage-unavailable, and session-ended and resumes on recovery; one file transfers at a time while others wait in the queue) and `src/uploads/uploadRepository.ts` (the screens' read and command interface: jobs per member and match, add files, set mapping, retry, pause, resume, remove from queue, reselect file, abandon, acknowledge expiry, purge member) plus `src/uploads/UploadProvider.tsx` mounted in `src/clients/web/src/App.tsx`. Unit tests in `src/clients/web/tests/unit/uploads/coordinator/` with fake adapters and a fake worker for every state transition, refusal, and key reuse.

**Checkpoint**: Adapters, the upload engine, the development storage access, and the CSP additions are in place.

---

## Phase 3: User Story 1 - Manage a team's matches (Priority: P1) 🎯 MVP

**Goal**: Coaches and Club Admins list, create, and edit matches; Viewers read them.

**Independent Test**: As a Coach, create two matches, edit one, and see both in the team's list; as a Viewer, see them read-only; as a Coach of another team, reach none.

- [ ] T008 [US1] Match list, create, edit, and match view (contracts/routes-and-roles.md; data-model.md § Match; FR-001–FR-004; US1 scenarios 1–6): extend `src/clients/web/src/features/teams/TeamPage.tsx` with the team's match list (opponent, kickoff time, venue (home, away, or neutral), competition; newest kickoff first; paged) and "New match" for editors; add `src/clients/web/src/features/matches/MatchCreatePage.tsx`, `MatchEditPage.tsx` (opponent read-only; `If-Match` with the loaded version; conflict with reload), and `MatchPage.tsx` (details; editor actions hidden for readers and while the team's `seasonState` is `archived`; the platform's `season-archived` refusal shown); register the routes in `src/clients/web/src/router.tsx`; keys in `src/clients/web/src/i18n/en/matches.json`. Unit tests in `src/clients/web/tests/unit/features/matches/`; browser tests in `src/clients/web/tests/e2e/simulated/matches.spec.ts` for US1 scenarios 1–6 with axe checks and keyboard operation on every new view.
- [ ] T009 [US1] Platform end-to-end match flow (SC-002): add `src/clients/web/tests/e2e/platform/matches.spec.ts`: as the Club Admin, create a season, activate it, create a team, create a match, edit its competition, and see it in the team's list against the real platform.

**Checkpoint**: User Story 1 works against the simulated API and the real platform.

---

## Phase 4: User Story 2 - Upload a recording to a match (Priority: P2)

**Goal**: Add video files, prepare, transfer directly to object storage, set the timeline mapping, and complete.

**Independent Test**: Add two 40 MiB files to a match, watch the queue and progress, set mappings, complete both, and confirm that no recording bytes reached the API.

- [ ] T010 [US2] Duration and timeline mapping editor (research.md § R9; data-model.md § Timeline mapping draft; FR-018; clarification on the proposed span): add `src/clients/web/src/uploads/duration.ts` (detached `<video preload="metadata" muted>` with a `blob:` URL, never attached or played; no duration after 10 s or when not finite; revokes the URL) and `src/clients/web/src/features/uploads/TimelineMappingEditor.tsx` (default one span `0 → durationSeconds` starting at the match time the member enters; add, edit, and remove spans; `mm:ss` or `hh:mm:ss` input converted to seconds with millisecond precision; the media end entered by the member when no duration is known; platform validation shown next to the affected span); keys in `src/clients/web/src/i18n/en/uploads.json`. Unit tests in `src/clients/web/tests/unit/features/uploads/mapping/`.
- [ ] T011 [US2] Upload views in the match (FR-005–FR-011; US2 scenarios 1–8; clarifications on video-only files and the queue; edge case "Same file added twice"): add `src/clients/web/src/features/uploads/AddRecordingsPanel.tsx` (picker and drop zone with `accept="video/*"`, display name and description per file, refusal of non-video files, and the warning that the file seems already uploaded or uploading when its composite digest equals a recording version's `contentDigest` or the `contentDigest` of one of the member's pending jobs or journal records for the match), `src/clients/web/src/features/uploads/UploadList.tsx` (queue, preparation and transfer progress, paused and failed states with retry, remove from queue, mapping editor, completion), and the recordings section in `src/clients/web/src/features/matches/MatchPage.tsx`; extend keys in `src/clients/web/src/i18n/en/uploads.json`. Unit tests in `src/clients/web/tests/unit/features/uploads/views/`; browser tests in `src/clients/web/tests/e2e/simulated/uploads.spec.ts` for US2 scenarios 1–8 (with fault injection for retries, expired grants, and refused completion), the same-file edge case, and SC-003 (the simulated API's request log shows no non-JSON body to `/api/v1`).
- [ ] T012 [US2] Upload indicator, leave guard, and responsiveness (contracts/routes-and-roles.md § Shell additions; FR-012; US2 scenario 9; SC-006): add `src/clients/web/src/shell/UploadIndicator.tsx` and its panel to `src/clients/web/src/shell/AppLayout.tsx` (active and queued uploads with overall progress on every route), and the `beforeunload` confirmation while an upload is `transferring`, `starting`, or `completing` in `src/clients/web/src/uploads/useLeaveGuard.ts`; keys in `src/clients/web/src/i18n/en/shell.json`. Unit tests in `src/clients/web/tests/unit/shell/uploadIndicator.test.tsx`; browser tests in `src/clients/web/tests/e2e/simulated/upload-shell.spec.ts` for US2 scenario 9 and SC-006 (a 1 GiB file: no main-thread long task over 200 ms reported by a `PerformanceObserver` while preparing and transferring, and navigation between views works).
- [ ] T013 [US2] Platform end-to-end upload (SC-002, SC-003): add `src/clients/web/tests/e2e/platform/upload-flow.spec.ts`: on a new match, upload a 40 MiB file through the Web UI directly to RustFS, set the match start time, complete, and see the accepted recording version with its digest.

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - Finalize the match's recording set (Priority: P3)

**Goal**: Finalize ordered selections and see the analysis run states.

**Independent Test**: Finalize two accepted recordings in a chosen order and see the set version and its analysis run state.

- [ ] T014 [US3] Finalize view and analysis run states (data-model.md § Finalize selection, § Analysis run; research.md § R8, § R13; FR-020, FR-023; US3 scenarios 1–6; clarification on repeat finalization): add `src/clients/web/src/features/lineage/FinalizePage.tsx` (select and order accepted recording versions, one mapping per version with the newest as default; "Not empty; at most the platform's limit (100 by default, refusal authoritative)"; one idempotency key per open dialog reused on retry; a selection identical in order, versions, and mappings to the newest finalized set requires explicit confirmation) and `src/clients/web/src/features/lineage/AnalysisRunStates.tsx` (runs grouped by `recordingSetVersionId`; state, workflow, timestamps, failure reason; refetch every 10 s while any run is `Requested` or `Running` and the page is visible; no results), shown in `src/clients/web/src/features/matches/MatchPage.tsx`; register `/matches/:matchId/finalize` in `src/clients/web/src/router.tsx`; keys in `src/clients/web/src/i18n/en/lineage.json`. Unit tests in `src/clients/web/tests/unit/features/lineage/finalize/`; browser tests in `src/clients/web/tests/e2e/simulated/finalize.spec.ts` for US3 scenarios 1–6.
- [ ] T015 [US3] Platform end-to-end finalization (SC-002): extend `src/clients/web/tests/e2e/platform/upload-flow.spec.ts` to finalize the uploaded recording and see the new set version and the state of the analysis run the platform created for it. The AppHost creates the run from the development workflow `Analysis:DefaultWorkflow` in `src/platform/SocAlytics.Platform.Api/appsettings.Development.json` (or, once `specs/archive/20261012-152148-analyst-registry` is merged, from the workflow it resolves for the development stamp); assert that at least one run for the new set appears in any state, at least `Requested`, without depending on Analyst execution.

**Checkpoint**: User Stories 1–3 work independently.

---

## Phase 6: User Story 4 - Recover an interrupted upload (Priority: P4)

**Goal**: Resume after reload, session end, or network loss; abandon; expired uploads; sign-out purge.

**Independent Test**: Reload during a multi-part upload, reselect the file, and see only missing parts sent; reselect a different file and see the refusal; abandon another upload.

- [ ] T016 [US4] Recovery views and sign-out purge (FR-014–FR-017; US4 scenarios 1–7; clarification on sign-out; edge cases "Match opened in two tabs", "Different member in the same browser", "Membership or role revoked during an upload"): add `src/clients/web/src/features/uploads/PendingUploads.tsx` (this member's journal records for the match with file name, progress, and session expiry; reselect to resume with size check and digest verification; refusal on mismatch; abandon with the expiry explanation; expired uploads with "start again"; "running elsewhere" while another tab holds the upload's lock), shown in `src/clients/web/src/features/matches/MatchPage.tsx`; in `src/clients/web/src/features/account/` and `src/clients/web/src/session/SessionProvider.tsx` make explicit sign-out ask for confirmation while an upload is transferring and call the repository's `purgeMember` before ending the session, while a session ended by the platform keeps the records; keys in `src/clients/web/src/i18n/en/uploads.json`. Unit tests in `src/clients/web/tests/unit/features/uploads/recovery/`; browser tests in `src/clients/web/tests/e2e/simulated/recovery.spec.ts` for US4 scenarios 1–7 and the listed edge cases, including SC-004 (the fake store's log shows no already confirmed part sent again).
- [ ] T017 [US4] Platform end-to-end recovery (SC-004): add `src/clients/web/tests/e2e/platform/recovery.spec.ts`: start a 40 MiB upload, reload after the first part is stored, reselect the file, observe that the upload completes, and sign out and back in to see no pending upload.

**Checkpoint**: User Stories 1–4 work independently.

---

## Phase 7: User Story 5 - Review the lineage and correct evidence (Priority: P5)

**Goal**: Read-only lineage, recording version detail, and mapping revision.

**Independent Test**: Revise one mapping, finalize a new set with it, and see both mappings and both sets unchanged; as a Viewer, see the same without actions.

- [ ] T018 [US5] Lineage and mapping revision (data-model.md § Lineage; FR-019, FR-021, FR-022, FR-024; US5 scenarios 1–5): add `src/clients/web/src/features/lineage/LineageSection.tsx` (recording versions with display name, size, content digest, and upload time; mappings with spans, digest, and time; finalized set versions with ordered members, digests, and finalization time; no playback, preview, download, edit, reorder, or delete action) shown in `src/clients/web/src/features/matches/MatchPage.tsx`, and `src/clients/web/src/features/lineage/RecordingVersionPage.tsx` (mappings and "Revise mapping" for editors with `TimelineMappingEditor` and one idempotency key per open dialog); register `/matches/:matchId/recordings/:recordingVersionId` in `src/clients/web/src/router.tsx`; extend keys in `src/clients/web/src/i18n/en/lineage.json`. Unit tests in `src/clients/web/tests/unit/features/lineage/review/`; browser tests in `src/clients/web/tests/e2e/simulated/lineage.spec.ts` for US5 scenarios 1–5 as Coach and Viewer.

**Checkpoint**: All user stories work independently.

---

## Phase 8: Polish & Cross-Cutting Concerns

- [ ] T019 Documentation (plan.md § Architecture Updates; Constitution Principle I): apply every update listed in plan.md § Architecture Updates and correct any other statement in `docs/architecture/` that the merged feature makes false, including planned or future-tense wording. In particular, record in `docs/architecture/client-applications.md` § Web UI the upload worker and its resume journal (worker-owned IndexedDB, member-scoped, identifiers and progress only, never grants or file contents, purged on explicit sign-out, not the offline replica; uploads stay connected-only); in `docs/architecture/platform-implementation.md` § Object Storage the development CORS allow-list for the Web UI origin and the storage origin passed to the Web UI, with production CORS and origins still unresolved, and in § Planned Acceptance Evidence the match workflow and recording upload as implemented Web UI workflows; in the Web UI security baseline of `docs/architecture/security-and-data-governance.md` the CSP additions of contracts/security-headers.md. Update `src/clients/README.md` (match and upload views, the fake object store in the `simulated` project) and the "Current State" in `AGENTS.md`.
- [ ] T020 Run the validation in [quickstart.md](quickstart.md): platform build and Host tests, then in `src/clients/web/` `npm ci`, `npm run build`, `npm test`, and `npm run test:e2e` (both projects), and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Setup (Phase 1)**: T001 first; T006 needs `fake-indexeddb` and every browser test needs the fake store.
- **Foundational (Phase 2)**: after Setup; T002–T005 in parallel, then T006 (uses T003, T005), then T007 (uses T002, T006). Blocks all stories.
- **US1 (Phase 3)**: after Foundational. **US2 (Phase 4)**: after US1 (match view). **US3 (Phase 5)**: after US2 (accepted recordings). **US4 (Phase 6)**: after US2 (uploads). **US5 (Phase 7)**: after US3 (finalized sets and the mapping editor).
- **Polish (Phase 8)**: after all stories.
- Within each story, the platform end-to-end task comes last.

### Parallel opportunities

- Phase 2: T002 (`src/platform/adapters/`, `src/platform/models/`, `src/platform/failure.ts`, `src/i18n/en/common.json`, `tests/unit/platform/adapters/`), T003 (`src/uploads/` core files, `tests/unit/uploads/core/`), T004 (`src/platform/SocAlytics.Platform.AppHost/`, `src/platform/Tests/SocAlytics.Platform.Host.Tests/`, `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/DevelopmentCorsTests.cs`, and for the fallback `src/platform/SocAlytics.Platform.Infrastructure/ObjectStorage/`), and T005 (`vite.config.ts`, `src/uploads/workerPolicy.ts`, `tests/unit/uploads/workerPolicy.test.ts`, `tests/e2e/simulated/headers.spec.ts`) change disjoint files; the worker factory `src/uploads/createUploadWorker.ts` belongs to T006 because it references the worker module T006 creates.
- No other phase has tasks with disjoint footprints: the story tasks share `MatchPage.tsx`, `router.tsx`, and translation files, or depend on each other (T012's indicator and browser tests need the uploads that T011's views add).

## Implementation Strategy

1. Setup and Foundational, then User Story 1 as the MVP (matches in the Web UI).
2. Add User Story 2 (upload), then 3 (finalize), 4 (recovery), and 5 (lineage).
3. Documentation and full validation.
