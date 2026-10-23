# Research: Web UI Materialized Segment Management

## R1: OpenAPI schemas

- **Decision**: Use the generated Kiota client as is. The committed OpenAPI snapshot already contains
  `listMaterializedSegments` (`GET /api/v1/matches/{matchId}/materialized-segments`) and `purgeMaterializedSegments`
  (`POST …/materialized-segments/purge`) with `MaterializedSegmentListDto`
  (`matchId`, `segments[]`: `recordingVersionId`, `timelineMappingDigest`, `segmentationPolicyDigest`, `segmentNumber`,
  `state`, `mediaSizeBytes?`, `failureReason?`, `inUse`, `availableAt?`) and `SegmentPurgeResultDto`
  (`matchId`, `purged`, `inUse`); refusals are `401`, `403`, `404`, and for purge `503`.
- **Rationale**: FR-018 forbids platform changes; the adapter pattern of `operationsAdapter` already normalizes nullable
  fields and unknown values.
- **Alternatives considered**: Hand-written `fetch` calls (rejected: bypasses the generated client, anti-forgery, and
  failure middleware); extending the list with an interval (rejected: out of scope, see spec assumptions).
- **Check at implementation**: if the snapshot types are looser than the DTO, the adapter narrows `state` to the four
  known values and keeps unknown values as reported text (spec edge case); no snapshot refresh is allowed.

## R2: Access and request discipline

- **Decision**: Show the area only when `isClubAdmin(session.member)` and the match loaded; enable the list query only
  then, so other roles cause no request. The platform stays authoritative: `403` renders a not-permitted state inside the
  area (not the whole-page `NotPermittedPage`, since the rest of the match page is still permitted), `401` follows the
  existing session-outcome middleware, `404` is already covered by the match page's own not-found handling.
- **Rationale**: FR-001, FR-002, spec User Story 3. A Club Admin of another club is already refused the whole match page.
- **Alternatives considered**: Whole-page not-permitted on `403` (rejected: hides the match the member may still read).

## R3: Query, refresh, and purge behaviour

- **Decision**: One TanStack Query per match (`['matches','segments',matchId]`, `retry: false`,
  `refetchOnWindowFocus: false`, no `refetchInterval`); refresh calls `refetch` and keeps the previous data
  (`isFetching` indicates the refresh). Purge is a mutation in the dialog, sent once (a ref guards a second confirm),
  followed by a list refetch on success and on failure (FR-013); the dialog closes only when not pending. Leaving the
  page discards results because the query is unmounted and `gcTime` is `0` for segment data so nothing outlives the
  page (SC-006).
- **Rationale**: Mirrors `ReconcileDialog` and the existing "no automatic refresh" areas; satisfies FR-007, FR-011, FR-012.
- **Alternatives considered**: Polling while a segment is `materializing` (rejected: FR-007 forbids automatic refresh);
  optimistic update after purge (rejected: SC-004 requires the list to reflect the platform).

## R4: Real segment states in the platform project

- **Decision**: Add the chained Playwright project `platform-segments` (placed after `platform` and before
  `platform-registry`, which then depends on it, so it runs while the seeded default workflow is still the default;
  the registry spec makes a new workflow version the default) with
  `tests/e2e/platform/segments.spec.ts`. The Development workflow seed declares `VideoPolicy` `forbidden`
  (`appsettings.Development.json`), so no segment is ever requested; in e2e mode only, the AppHost sets
  `Analysis__DefaultWorkflow__Nodes__0__VideoPolicy=optional` so finalizing an uploaded recording set requests segments.
  An uploaded file that is not decodable media makes the real materialization worker end the segment as `failed` with a
  platform-reported reason (real `failed` state); a valid tiny MP4 generated with the pinned `ffmpeg` (a lavfi test
  source, produced in the test's temporary folder) yields a real `available` segment; a purge returns the unused ones to
  `absent`. The `optional` policy lets the run continue without media when a segment fails, so the other platform
  projects' expectations (simulated Analyst) hold.
- **Rationale**: FR-019 requires real segment states without creating platform operations; the segment worker, toolchain,
  and the policy knob already exist as development evidence. The `in use` state of a waiting, ready, or claimed job is
  covered by the simulated project and by the platform project only if the scripted Manager holds a job (otherwise the
  platform test asserts the unpinned paths and the simulated project covers pinned ones).
- **Alternatives considered**: Inserting rows into `materialized_segments` from the test (rejected: tests must not
  bypass the platform and the database is not exposed to browser tests); changing the global Development seed (rejected:
  affects non-e2e development and the Docker-backed tests); a new platform seeding operation (rejected: FR-018).
- **Risk**: The e2e policy value applies to the whole e2e AppHost, so it must not change expectations of earlier chained
  projects (analysis, cancellation, supersession, which now request segments); the implementation runs the full
  `platform-segments` project with its dependency chain, so every earlier platform project runs with the new policy. If
  a project depends on `forbidden`, the fallback is to limit the value to a separate e2e configuration used only for the
  `platform-segments` run.

## R5: Presentation

- **Decision**: A semantic table (`<table>` with caption and column headers) of segments in platform order, columns
  recording version (shortened id, full value in a `title`/copy control), segment number, state (text), size
  (`Intl.NumberFormat` with the active i18n language), failure reason, availability time (`Intl.DateTimeFormat` with the
  active i18n language), and in-use text. The existing `formatBytes` (fixed `KiB` units and `.` decimal) and
  `formatTimestamp` (hard-coded `en` locale) are not locale-aware, so a small formatter in `features/segments` is used
  to meet FR-016. The segment number column is labelled "Segment number", never "Interval". Absent values show a localized "not reported" text. The summary is a
  `role="status"` line with counts. The interval is shown as the segment number within its recording version (spec
  assumption). Digests are shown in a details disclosure with copy.
- **Rationale**: FR-003 to FR-005, FR-015, FR-016; a scrollable container keeps narrow widths usable.
- **Alternatives considered**: MUI `DataGrid` (rejected: new dependency); cards per segment (rejected: harder to scan
  long lists).

## R6: Offline and connectivity

- **Decision**: Use `useConnectivity().isUnreachable`: when offline the area shows the existing
  `shell` connection-required text, keeps any in-memory list, and disables purge and refresh; nothing is queued.
- **Rationale**: FR-017; same as `ReconcileDialog`.
- **Alternatives considered**: Queue purge for later (rejected: forbidden).
