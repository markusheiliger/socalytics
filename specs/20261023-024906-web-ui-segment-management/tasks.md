---

description: "Task list for Web UI Materialized Segment Management for Club Admins"
---

# Tasks: Web UI Materialized Segment Management for Club Admins

**Input**: Design documents from `specs/20261023-024906-web-ui-segment-management/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required. FR-019 and SC-001 to SC-007 require automated evidence; test work is part of each task.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files never
  overlap.
- **[Story]**: Which user story this task belongs to (US1 to US3)
- Paths are repository-relative; the Web UI lives in `src/clients/web`. No platform operation, contract, or OpenAPI
  snapshot changes (FR-018); never refresh the committed OpenAPI snapshot.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The view models and the text resources every later task uses.

### Models and texts

- [x] T001 [P] Create `src/clients/web/src/platform/models/segments.ts` with `MaterializedSegment`, `SegmentList`, and
  `PurgeOutcome` as specified in `specs/20261023-024906-web-ui-segment-management/data-model.md` (`state` is
  `'absent' | 'materializing' | 'available' | 'failed'` with unknown platform values kept as reported text;
  `mediaSizeBytes`, `failureReason`, and `availableAt` are `undefined` when not reported)
- [x] T002 [P] Create the `segments` translation namespace `src/clients/web/src/i18n/en/segments.json` (area title and
  caption, loading, empty ("segments are created on demand when an analysis needs them"), not-permitted, error with
  retry, offline, summary, column headers (the segment number column labelled "Segment number"), an area note that
  purged segments are rematerialized on demand, the four state names, in-use and not-in-use, "not reported", refresh, purge
  action, "nothing to purge" reason, confirmation title and body (match, unused segments deleted and become absent,
  segments in use kept and reported, purged segments rematerialized on demand), in-progress, outcome with purged and
  in-use counts, failure with "segments already purged by the call stay purged", retry) and register it in
  `src/clients/web/src/i18n/index.ts` (`ns` list and `resources`)

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The adapter, summary, and query key that all three stories build on.

### Adapter and derived values

- [x] T003 [P] Create `src/clients/web/src/platform/adapters/segmentAdapter.ts` (`list` over `listMaterializedSegments`
  and `purge` over `purgeMaterializedSegments` through the shared Kiota client and `toPlatformFailure`; nullable DTO
  fields become `undefined`; an unknown `state` is kept verbatim; no headers, no retry, no snapshot change) as specified
  in `specs/20261023-024906-web-ui-segment-management/contracts/segment-adapter.md`, with the unit test
  `src/clients/web/tests/unit/platform/adapters/segmentAdapter.test.ts` (mapping, nullable fields, unknown state,
  `401`, `403`, `404`, `503` refusals, single request per call)
- [x] T004 [P] Create `src/clients/web/src/features/segments/segmentSummary.ts` (`total`, per-state counts, `other`,
  `inUse`, and `purgeable` = segments with `inUse === false` and `state` in (`available`, `failed`), as in `data-model.md`) and
  `src/clients/web/src/features/segments/segmentKeys.ts` (query key `['matches','segments',matchId]`), with the unit
  test `src/clients/web/tests/unit/features/segments/segmentSummary.test.ts` (including cases for an unknown state and
  for `materializing`, neither of which is purgeable)

**Checkpoint**: Adapter, summary, and texts exist; stories can start.

## Phase 3: User Story 1 - See the materialized segments of a match (Priority: P1) 🎯 MVP

**Goal**: A Club Admin sees every materialized segment of a match, with a summary and an explicit refresh, on the match
page; no other member sees the area or causes a request.

**Independent Test**: A Club Admin opens a match with segments in several states and sees every row with state, size,
failure reason, availability time, in-use marker, and the summary counts; a Coach or a Club Admin of another club sees
neither the area nor causes any request.

### Segments area and scripted segment API

- [x] T005 [P] [US1] Create `src/clients/web/src/features/segments/SegmentsSection.tsx`: the list query (`retry: false`,
  `refetchOnWindowFocus: false`, no `refetchInterval`, `gcTime: 0`, enabled only for the Club Admin) with explicit
  loading, empty, and list states; a `role="status"` summary from `segmentSummary`; a semantic table with caption and
  column headers in platform order (shortened recording version with the full value and the digests available in a
  disclosure with copy, reusing `CopyableValue` from `src/clients/web/src/features/operations/CopyableValue.tsx`,
  segment number (column labelled "Segment number", never "Interval"), state as text, size only when reported, failure
  reason as text only when reported, availability time only when reported, in-use text, "not reported" for absent
  values; sizes are formatted with `Intl.NumberFormat` and timestamps with `Intl.DateTimeFormat` using the active i18n
  language, in a small formatter module in `src/clients/web/src/features/segments/`, not with `formatBytes` or
  `formatTimestamp`, which use fixed units and a hard-coded locale); a refresh action that keeps the previous list visible and never polls; offline text through
  `useConnectivity().isUnreachable` with refresh disabled; scrollable at narrow widths; memory-only data (FR-003 to
  FR-008, FR-015, FR-016, FR-017), with the unit test
  `src/clients/web/tests/unit/features/segments/SegmentsSection.test.tsx` (loading, empty, every state, absent values,
  summary, refresh keeps the previous list, offline, no storage writes, locale-specific size and time formatting, and
  that after sign-out or a `401` no segment data remains in the query cache)
- [x] T006 [P] [US1] Add the scripted segment API to `src/clients/web/tests/e2e/support/simulatedApi.ts` (list handler
  and fixtures with segments in every state, in use and not in use, and a purge handler with scripted outcomes and
  refusals, usable by all three stories; request counters so tests can assert that no request was sent)

### Match page mount and simulated evidence

- [x] T007 [US1] Mount `SegmentsSection` in `src/clients/web/src/features/matches/MatchPage.tsx` below the recordings
  and analysis run list and above the triage section, for a Club Admin of the owning club only (`isClubAdmin` from
  `src/clients/web/src/session/roles.ts`), with the unit test
  `src/clients/web/tests/unit/features/matches/MatchPageSegments.test.tsx` (Club Admin sees the area; Coach, Analyst,
  Viewer, and a Club Admin of another club do not and no segment request is sent)
- [x] T008 [US1] Create `src/clients/web/tests/e2e/simulated/segments.spec.ts` with the access, listing, summary, empty,
  refresh, and axe accessibility scenarios of User Story 1 (Club Admin sees the area; other roles see no area and the
  list request counter stays zero; text-only states; keyboard use)

**Checkpoint**: User Story 1 is complete and independently testable.

## Phase 4: User Story 2 - Purge unused segments after a confirmation (Priority: P2)

**Goal**: A Club Admin purges the unused segments of a match after a confirmation that states the outcome, and sees the
purged and in-use counts and the refreshed list.

**Independent Test**: Against unused and in-use segments the Club Admin starts the purge, cancels once (nothing sent),
confirms, reads the counts, sees the purged segments as absent and the in-use ones unchanged; repeating the purge
reports zero purged.

### Purge dialog and e2e configuration

- [x] T009 [P] [US2] Create `src/clients/web/src/features/segments/PurgeSegmentsDialog.tsx` following
  `src/clients/web/src/features/operations/ReconcileDialog.tsx`: confirmation stating the match (opponent name),
  that segments not in use are deleted and become absent, that segments in use are kept and reported, and that a purged
  segment is rematerialized on demand; Cancel sends nothing; one purge request per confirmation (a ref guards a second
  confirm); in-progress status with both actions disabled and dismissal blocked; the outcome with the returned `purged`
  and `inUse` counts (zero purged is a normal outcome); the failure state with a plain-language cause, the note that
  segments already purged by the call stay purged, and a retry action; `role="status"` and `role="alert"` announcements
  and focus returning to the purge button on close (FR-010 to FR-015); the outcome text repeats that purged segments
  are rematerialized on demand, with the unit test
  `src/clients/web/tests/unit/features/segments/PurgeSegmentsDialog.test.tsx` (cancel sends nothing, single request,
  outcome, zero purged, refusal and `503`, in-progress, focus)
- [x] T010 [P] [US2] Add to `src/platform/SocAlytics.Platform.AppHost/Program.cs`, in the existing e2e-mode block only,
  the environment value `Analysis__DefaultWorkflow__Nodes__0__VideoPolicy=optional` on the API so that finalizing an
  uploaded recording set requests segments (`research.md` R4); the value applies to the whole e2e AppHost, so the
  earlier chained platform projects must still pass with it (verified in T017); no other mode and no production code
  changes

### Purge action and simulated evidence

- [x] T011 [US2] Extend `src/clients/web/src/features/segments/SegmentsSection.tsx` with the purge action offered only
  while `purgeable > 0` (otherwise the "nothing to purge" reason), purge and refresh disabled while a purge is pending
  or the browser is offline, a list re-read after the purge ends, on success and on failure, and the area note that
  purged segments are rematerialized on demand; extend
  `src/clients/web/tests/unit/features/segments/SegmentsSection.test.tsx` for these cases
- [ ] T012 [US2] Extend `src/clients/web/tests/e2e/simulated/segments.spec.ts` with the purge scenarios (confirmation
  text, cancel sends nothing, confirm sends one request, counts and re-read list with purged segments absent and in-use
  segments unchanged, repeat reports zero purged, no purge when nothing is purgeable, in-flight controls disabled,
  refusal and `503` with retry, offline, navigating away from the match page during a pending purge (the result is
  discarded and the purge still completes), and a re-read list showing a purged segment as `materializing` and then
  `available`), including axe checks of the dialog and focus return

### Platform browser evidence

- [ ] T013 [US2] Create `src/clients/web/tests/e2e/platform/segments.spec.ts` (real `failed` segment from an uploaded
  file that is not decodable media with the platform-reported reason, real `available` segment from a tiny MP4 generated
  with the pinned `ffmpeg` lavfi source in the test's temporary folder, confirmed purge returning unused segments to
  `absent` with the reported counts and the re-read list, repeat purge reporting zero, a non-admin member seeing no
  area, and in-use paths asserted only if the scripted Manager holds a job; in-use behaviour is evidenced by the
  simulated project, so this conditional platform assertion is not a gap). Because the materialization worker retries
  (`Segments:Materialization` `MaxAttempts` and `RetryDelay`) and the area never polls, the test clicks the refresh
  action with a bounded wait until the segment is `failed`, and the e2e AppHost uses a short materialization retry
  configuration if one is needed. Register the chained project
  `platform-segments` (dependencies `['platform']`, with `platform-registry` then depending on `platform-segments`, so it runs
  while the seeded default workflow is still the default) in `src/clients/web/playwright.config.ts`; if an earlier platform
  project depends on `forbidden`, apply the fallback of `research.md` R4

**Checkpoint**: User Stories 1 and 2 work independently and together.

## Phase 5: User Story 3 - Understand refusals and failures (Priority: P3)

**Goal**: Not-permitted, session-ended, error, and offline conditions each show a distinct, specific state instead of a
blank or misleading list, with recovery where possible.

**Independent Test**: With scripted `403`, `401`, server-error, and offline conditions the area shows the distinct state
for each and, after the condition clears and the Club Admin retries, shows the list.

### Refusal and failure states

- [ ] T014 [US3] Extend `src/clients/web/src/features/segments/SegmentsSection.tsx` with the not-permitted state for a
  `403` (explanation, no segment data, no purge, inside the area and not the whole-page view), the error state with a
  retry action for network and `5xx` failures that is never an empty list, and the `401` path that follows the existing
  session-ended flow and leaves no stale data; extend
  `src/clients/web/tests/unit/features/segments/SegmentsSection.test.tsx` for `403`, `401`, error, retry, and offline
- [ ] T015 [US3] Extend `src/clients/web/tests/e2e/simulated/segments.spec.ts` with the `403`, `401` (sign-in flow, no
  stale data afterwards), `404` (existing match not-found view), server-error with retry, and offline scenarios, and
  that after sign-out or a `401` no segment data remains and that `localStorage`, `sessionStorage`, IndexedDB, and
  Cache Storage contain none after leaving the match page

**Checkpoint**: All three user stories are independently functional.

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Documentation that matches the merged state and final verification.

- [ ] T016 Make `docs/architecture/` describe the implemented state (FR-020, plan `## Architecture Updates`): in
  `docs/architecture/client-applications.md` (Web UI) add the Club Admin segments area as implemented, connected,
  online only, memory-only data, with `simulated` and `platform` browser tests; in
  `docs/architecture/match-data-pipeline.md` (On-Demand Video Segmentation) name the Web UI's segments area as a way to
  run the manual purge and keep `POL-004` unresolved; in `docs/architecture/platform-implementation.md` (Architecture
  Reassessment and Acceptance Evidence) add the segments area and its browser tests; correct every other statement in
  `docs/architecture/` that presents segment inspection or purge as API-only or the Segment Service's Web UI as absent or
  planned (only statements that actually exist; the Segment Service "no Web UI" wording is in `AGENTS.md`, not in
  `docs/architecture/`); also update `AGENTS.md` "Current State" (the Segment Service "no Web UI" wording and the Web
  UI foundation description), `src/clients/README.md`, and `README.md`, re-reading all of them at implementation time
- [ ] T017 Run the final verification in `src/clients/web` (`npm run build`, `npm test`,
  `npm run test:e2e -- --project simulated`, and, with Docker, the full
  `npm run test:e2e -- --project platform-segments` including its dependency chain, so that all earlier platform
  projects run with the e2e `VideoPolicy` `optional` of T010) and
  `node .github/scripts/check-markdown.mjs` from the repository root; fix any failure caused by this feature

## Dependencies & Execution Order

- Phase 1 (T001, T002) and Phase 2 (T003, T004) are `[P]` groups; T003 needs T001 and T004 needs T001 at implementation
  time, so run Phase 1 before Phase 2.
- Phase 3 (US1) needs Phases 1 and 2; T005 and T006 run in parallel; T007 needs T005; T008 needs T005 to T007.
- Phase 4 (US2) needs US1; T009 and T010 run in parallel: T011 changes the file of T005; T012 extends the file of
  T008; T013 needs T010 and the purge action of T011.
- Phase 5 (US3) needs US1 and edits the same files as US2, so it follows US2.
- Phase 6 follows all stories; T016 and T017 are sequential.

### Parallel opportunities

- T001 and T002; T003 and T004; T005 and T006; T009 and T010 (different files).

## Implementation Strategy

- **MVP**: Phases 1 to 3 (User Story 1): the read-only segments area for Club Admins, with simulated browser evidence.
- **Incremental delivery**: add the purge with its dialog and platform evidence (US2), then the refusal and failure
  states (US3), then the documentation and final verification.
