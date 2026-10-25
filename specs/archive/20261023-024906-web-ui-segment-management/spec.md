# Feature Specification: Web UI Materialized Segment Management for Club Admins

**Feature Branch**: `20261023-024906-web-ui-segment-management`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Give Club Admins a connected Web UI area on the match page to see and manage the materialized video segments of a match through the already implemented member operations listMaterializedSegments and purgeMaterializedSegments under /api/v1/matches/{matchId}/materialized-segments. A Club Admin can see which segments of a match are absent, materializing, available, or failed (interval, size, state, failure reason, whether a waiting, ready, or claimed job pins it) and can purge the unused ones after a confirmation that states the outcome (pinned segments are kept and reported, purged segments return to absent and are rematerialized on demand), without calling the API by hand. Online only, memory-only data, Club Admin only, explicit loading, empty, refused, and error states, a refresh action without automatic polling, i18n texts, unit tests and browser tests in the simulated and platform projects. Out of scope: any new or changed platform operation or contract, automatic eviction, transcoding, segment playback or download, storage cost views, and production toolchain, storage, or retention values (POL-004 stays unresolved)."

## User Scenarios & Testing *(mandatory)*

The actor is the Club Admin of the club that owns a match. The platform already lists and purges the materialized
segments of one match for Club Admins ([Materialized Video Segments and Analyst Input Staging](../20261017-222433-materialized-segments/spec.md),
[On-Demand Video Segmentation](../../../docs/architecture/match-data-pipeline.md#on-demand-video-segmentation)); today that
is only reachable by calling the API by hand. This feature adds the area to the match page of the Web UI
([Web UI](../../../docs/architecture/client-applications.md#web-ui)). It changes no platform operation or contract.

### User Story 1 - See the materialized segments of a match (Priority: P1)

A Club Admin opens a match and finds a segments area on the match page. It lists the match's materialized segments as
the platform reports them, in the platform's order, each with its recording version, segment number, state (absent,
materializing, available, or failed), media size when available, failure reason when failed, when it became available,
and whether a waiting, ready, or claimed job (or an ongoing materialization) keeps it in use. A summary above the list
counts the segments per state and the segments in use.

**Why this priority**: It answers "what media is cached for this match, and is anything failing?" without the API, and
changes nothing, so it is valuable on its own and is the base for the purge.

**Independent Test**: With a match that has a finalized recording set and segments in several states, a Club Admin opens
the match page and sees every segment with its state, size, failure reason, and in-use marker, and the summary counts;
a Coach or a Club Admin of another club sees neither the area nor any segment data.

**Acceptance Scenarios**:

1. **Given** a signed-in Club Admin of the match's club, **When** the match page is shown, **Then** it contains the
   segments area; **Given** any other member (Coach, Analyst, Viewer, or a Club Admin of another club), **Then** the area
   is not shown and no segment data is requested.
2. **Given** the segments area is shown, **When** its data loads, **Then** an explicit loading state is shown first and
   then one row per segment with its recording version, segment number, state, media size (only for an available
   segment), failure reason (only for a failed segment), availability time (where reported), and an in-use marker.
3. **Given** segments in different states, **When** the list is shown, **Then** each state is told apart by text, not by
   colour alone, and absent values (size, reason, availability time) are shown as absent rather than as zero or empty
   text.
4. **Given** the list, **When** the summary is shown, **Then** it states how many segments are absent, materializing,
   available, and failed, and how many are in use.
5. **Given** the match has no materialized segments (for example no analysis has requested media yet), **When** the area
   loads, **Then** it states plainly that no materialized segments exist for the match and that segments are created on
   demand when an analysis needs them, and offers no purge action.
6. **Given** the area is shown, **When** the Club Admin refreshes it, **Then** the list is read again from the platform,
   the previous list stays visible until the new one arrives, and the data is never refreshed automatically.

---

### User Story 2 - Purge unused segments after a confirmation (Priority: P2)

A Club Admin who wants to free cached media chooses to purge the match's segments. A confirmation states the outcome
before anything happens: unused segments are deleted and return to absent, segments in use by waiting, ready, or claimed
jobs are kept, and a purged segment is rematerialized on demand when a later job needs it. After the Club Admin confirms,
the area reports how many segments were purged and how many were kept because they are in use, and shows the refreshed
list.

**Why this priority**: The manual purge is the only cleanup of segment media available during initial development; it
depends on seeing the segments (User Story 1) but delivers the actual management value.

**Independent Test**: Against a match with unused and in-use segments, the Club Admin starts the purge, reads the
confirmation, cancels once (nothing changes), then confirms; the result states the purged and in-use counts, the
refreshed list shows the purged segments as absent and the in-use ones unchanged, and repeating the purge reports zero
purged.

**Acceptance Scenarios**:

1. **Given** at least one segment is neither in use nor already absent, **When** the Club Admin chooses to purge,
   **Then** a confirmation is shown that states the match, that unused segments are deleted and become absent, that
   segments in use are kept, and that a purged segment is rematerialized on demand; nothing is sent to the platform until
   the Club Admin confirms.
2. **Given** the confirmation, **When** the Club Admin cancels or dismisses it, **Then** nothing changes and the list is
   as before.
3. **Given** the Club Admin confirms, **When** the purge completes, **Then** the area reports the number of segments
   purged and the number kept because they are in use, and the list is read again so it reflects the new states.
4. **Given** every listed segment is in use or already absent, **When** the area is shown, **Then** the purge action is
   not offered as available and the area states why (nothing to purge); **Given** a purge repeated against stale data,
   **Then** it reports zero purged without an error.
5. **Given** a purge is running, **When** the Club Admin tries to confirm again or refresh, **Then** a second purge is not
   started and the controls show that the purge is in progress.
6. **Given** the platform refuses the purge or the object storage is unavailable, **When** the purge ends, **Then** the
   area explains the refusal or the temporary failure in plain language, states that segments already purged by the call
   stay purged, re-reads the list so the true state is shown, and offers to try again.
7. **Given** a segment was purged, **When** a later analysis job needs it and the Club Admin refreshes, **Then** the area
   shows it as materializing and then available (rematerialized on demand), without any action by the Club Admin.

---

### User Story 3 - Understand refusals and failures (Priority: P3)

A Club Admin who is not permitted, whose session ended, whose match does not exist, or whose platform is temporarily
unavailable sees a clear, specific state in the segments area instead of a blank or misleading list, and can recover
where recovery is possible.

**Why this priority**: The data is operational; explicit states keep an empty list from being mistaken for "no segments"
when the platform could not answer.

**Independent Test**: With scripted platform responses for forbidden, not found, expired session, and server error, the
area shows the distinct state for each and, after the condition clears and the Club Admin retries, shows the list.

**Acceptance Scenarios**:

1. **Given** the platform refuses the list as not permitted (for example the role changed since the page loaded),
   **When** the response arrives, **Then** the area shows a not-permitted state that discloses no segment data and offers
   no purge.
2. **Given** the match is not found, **When** the match address is opened, **Then** the existing not-found view of the
   match page is shown, as for any match page.
3. **Given** the Club Admin's session has ended, **When** a list or purge request is refused as unauthenticated, **Then**
   the existing sign-in flow applies and the area shows no stale data afterwards.
4. **Given** the platform is unreachable or answers with a server error, **When** the list is requested, **Then** the
   area shows an error state with a retry action and does not show an empty list as if no segments existed.
5. **Given** the browser is offline, **When** the segments area is shown, **Then** it states that segment management needs
   a connection, and the purge action is unavailable until the connection returns.

---

### Edge Cases

- A segment changes state between loading the list and confirming the purge (for example a job starts pinning it): the
  platform's answer decides; the reported counts are shown as returned and the list is re-read.
- A very long list: the area shows all segments the platform returns (the operation is not paged) in a readable,
  scrollable list that stays usable at narrow widths.
- A segment is `materializing` or in use for a non-terminal job: it is marked in use and is never offered a separate
  purge action; purge is only per match.
- A segment failed with a reason the Web UI does not know: the platform's reason is shown as reported, with the state
  stated in text.
- The match has no finalized recording set: the platform returns no segments and the empty state is shown.
- The Club Admin leaves the match page while the list or purge is in flight: the request result is discarded without
  an error shown elsewhere, and a purge already sent still completes on the platform.
- Segment identifiers (recording version, digests) are long: they are shown shortened with the full value available to
  read and copy, and no object key, address, or media digest is shown because the platform returns none.

## Requirements *(mandatory)*

### Functional Requirements

#### Access

- **FR-001**: The match page MUST show the segments area only to a signed-in Club Admin of the club that owns the match;
  for every other member the area MUST NOT be shown and the Web UI MUST NOT request segment data.
- **FR-002**: The Web UI MUST NOT rely on hiding alone: a refusal by the platform (not permitted) MUST show the
  not-permitted state and no segment data, and an unauthenticated refusal MUST follow the existing session-ended flow.

#### Listing

- **FR-003**: The area MUST load the match's materialized segments from the platform's list operation when it is shown,
  with an explicit loading state, and MUST show each returned segment's recording version, segment number, state,
  media size (only when reported), failure reason (only when reported), availability time (only when reported), and
  whether it is in use. The segment number column MUST be labelled "Segment number" (not "Interval") so it is not read
  as a time range.
- **FR-004**: The area MUST keep the platform's order and MUST show states, in-use markers, and failures as text so that
  they do not depend on colour.
- **FR-005**: The area MUST show a summary of the number of segments per state and the number in use, derived from the
  list as returned.
- **FR-006**: The area MUST show an explicit empty state when the platform returns no segments, an explicit error state
  with retry when the list cannot be read, and MUST never present an unread list as an empty one.
- **FR-007**: The area MUST offer a refresh action that reads the list again; the previous list MUST stay visible until
  the new one arrives; the area MUST NOT refresh automatically or poll.
- **FR-008**: Segment data MUST be held in memory only: it MUST NOT be written to local storage, the offline journal, a
  service-worker cache, or any other persistent browser store, and MUST be discarded when the session ends or the match
  page is left.

#### Purge

- **FR-009**: The area MUST offer a purge action for the match only while at least one listed segment is neither in use
  nor already absent; otherwise it MUST state why no purge is possible.
- **FR-010**: The purge MUST require a confirmation that states the match, that segments not in use are deleted and
  become absent, that segments in use are kept and reported, and that a purged segment is rematerialized on demand;
  cancelling MUST send nothing.
- **FR-011**: After confirmation the Web UI MUST call the platform's purge operation once for the match, prevent a second
  concurrent purge or refresh from the same area, and MUST then report the returned purged and in-use counts and read
  the list again.
- **FR-012**: A purge repeated with nothing left to purge MUST be reported as zero purged, not as an error.
- **FR-013**: When the platform refuses the purge or the storage is unavailable, the area MUST explain the cause in plain
  language, state that segments already purged during the call stay purged, re-read the list, and offer to retry.
- **FR-014**: A purged segment MUST be shown as absent with its identity unchanged, and the area MUST state that it is
  rematerialized on demand.

#### Experience and boundaries

- **FR-015**: The segments area MUST be usable by keyboard and assistive technology: the confirmation traps and returns
  focus, results and errors are announced, and controls have accessible names, consistent with the rest of the Web UI.
- **FR-016**: Every text of the area MUST come from the Web UI's translation resources, with the same languages as the
  rest of the Web UI, and dates, times, and sizes MUST be formatted for the user's locale.
- **FR-017**: The feature MUST be online only: when offline the area MUST say that it needs a connection and MUST NOT
  queue a purge for later.
- **FR-018**: The feature MUST NOT add or change any platform operation, contract, or OpenAPI document, and MUST NOT offer
  segment playback, download, storage cost views, automatic eviction, or transcoding; it MUST NOT present provisional
  toolchain, storage, or retention values as adopted (the production cache and deletion policy `POL-004` stays
  unresolved).
- **FR-019**: Unit and browser tests MUST cover the access rules, loading, listing of every state, summary, empty,
  refused, error, offline, refresh, and the purge confirmation, cancellation, success, repeat, and failure paths, in the
  simulated browser project and, against the real platform with real segment states, in the platform browser project.
- **FR-020**: The architecture documentation (`docs/architecture/`) MUST describe the implemented state: the Web UI
  description names the Club Admin segments area, and the manual purge described for on-demand video segmentation names
  the Web UI as a way to run it. Separately, the "no Web UI" wording of the Segment Service in `AGENTS.md` "Current
  State" MUST be corrected, and `README.md` and `src/clients/README.md` MUST name the segments area.

### Key Entities

- **Materialized Segment (as shown)**: One cached media segment of a match, identified by its recording version, timeline
  mapping digest, segmentation policy digest, and segment number, with a state (absent, materializing, available, or
  failed), a media size when available, a failure reason when failed, an availability time when available, and an in-use
  marker. Read-only in the Web UI.
- **Segment Summary**: The counts per state and of in-use segments, computed from the list shown.
- **Purge Outcome**: The counts of purged and in-use segments the platform returns for one purge of one match.
- **Segments Area**: The Club Admin-only region of the match page that holds the summary, the list, the refresh action, and
  the purge action with its confirmation.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Club Admin can see the state of every materialized segment of a match, including failure reasons and
  in-use markers, from the match page in one step and without calling the API by hand.
- **SC-002**: 100% of members without the Club Admin role of the owning club see no segments area and cause no segment
  data request.
- **SC-003**: A Club Admin can purge a match's unused segments in no more than two deliberate actions (choose purge,
  confirm), and 100% of purges are preceded by a confirmation that states the outcome.
- **SC-004**: After a purge, 100% of segments in use remain unchanged in the list and the reported purged and in-use
  counts equal the changes visible after the list is read again.
- **SC-005**: 100% of unreadable states (loading, not permitted, session ended, offline, platform error) are
  distinguishable from a list with segments, and 0 error conditions are shown as an empty list.
- **SC-006**: 0 segment data items are persisted in the browser after the match page is left or the session ends.
- **SC-007**: 0 platform operations, contracts, or OpenAPI documents change with this feature.

## Assumptions

- The list and purge operations, their roles (Club Admin of the owning club), responses, and refusals are implemented as
  described in the archived materialized-segments spec; this feature only consumes them.
- The list operation returns the whole list (no paging, no filtering), so the area shows it in full; segments are
  identified for display by recording version and segment number, with the longer digests available on demand.
- The platform's list does not report a segment's time interval; "interval" is shown as the segment number within its
  recording version, and a time interval is shown only if the list already reports one. Extending the list's fields is a
  separate platform-side change and is out of scope.
- Purge is per match only, as the platform defines it; there is no per-segment purge.
- The segments area sits on the existing match page next to the recordings and analysis run list, follows the Web UI's
  existing patterns for confirmations, accessibility, i18n, memory-only data, and session handling, and reuses the
  Kiota client generated from the committed OpenAPI snapshot, which already contains both operations.
- The simulated browser project provides a scripted platform for every state; the platform browser project drives real
  segment states created through the connected workflows.
- Depends on the implemented
  [Materialized Video Segments and Analyst Input Staging](../20261017-222433-materialized-segments/spec.md),
  [Web UI Foundation](../20261012-150657-web-ui-foundation/spec.md), and
  [Web UI Match Workflow and Recording Upload](../20261013-112844-web-ui-match-upload/spec.md).
