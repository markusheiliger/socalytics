# Feature Specification: Web UI Match Workflow and Recording Upload

**Feature Branch**: `20261013-112844-web-ui-match-upload`

**Created**: 2026-10-13

**Status**: Draft

**Input**: User description: "Web UI Match Workflow and Recording Upload: the second Web UI slice in src/clients/web/, building on Web UI Foundation, that gives Coaches and Club Admins the path from a match to a finalized recording set the platform can analyze. Coaches and Club Admins list, create, and edit their teams' matches (Viewers read-only); an authorized member uploads one or more source recordings (camera angles) directly to the stamp's object storage, never through the API, with multipart grants, per-file progress, retry of failed parts, resume of a pending upload session after a reload, and explicit abandon; sets and revises each recording version's timeline mapping to match time; finalizes an ordered recording set; and sees on the match the recording lineage (versions, mappings, finalized set versions with digests) and, read-only, the states of the analysis runs created from finalized sets. It uses the existing match API and recording API (upload sessions, grants, completion, timeline mappings, recording-set versions, lineage, analysis runs per match) only through the Kiota-generated client behind application-owned adapters, and shows refusals and conflicts (If-Match, idempotency keys) as the API's problem codes define. Local development allows the Web UI origin for CORS on the development object-storage bucket and the strict Content Security Policy allows connect-src to the stamp's object-storage origin. Out of scope: video playback and download, segment materialization, analysis results and review, agent conversations, the offline mode and queued uploads, the fleet view, and production storage, CORS, and deployment values. Depends on Web UI Foundation, Recording Lineage and Upload, and Club and Identity Foundation. Implements docs/architecture/client-applications.md#web-ui (match creation, recording uploads, upload finalization) and docs/architecture/match-data-pipeline.md (Upload, lineage, timeline mapping)."

## Clarifications

### Session 2026-10-13

- Q: What happens to the browser's remembered pending uploads when the member explicitly signs out? → A: Explicit sign-out deletes all of the member's remembered uploads; they survive only reloads, session expiry, and a session ended by the platform.
- Q: How does the Web UI propose a recording's timeline mapping? → A: It reads the duration from the local file and proposes one span covering the whole recording; the member enters only the match time at which the recording starts and can add spans for gaps.
- Q: What happens when a member finalizes a set identical to the match's newest finalized set? → A: The Web UI warns that this starts another analysis and finalizes only after explicit confirmation.
- Q: Which files may a member select as a recording? → A: Only files the browser identifies as video; the Web UI refuses anything else, including unidentified types.
- Q: How many recordings transfer at the same time when several files are added? → A: One file at a time with the others in a visible queue; within that file, a small fixed number of parts transfer in parallel.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Manage a team's matches (Priority: P1)

A Coach opens one of their teams and sees its matches, newest first. They create a match with the opponent, kickoff time, venue (home, away, or neutral), and competition, and later correct the kickoff time, venue (home, away, or neutral), or competition. A Club Admin can do the same for every team. A Viewer of the team sees the matches but cannot change them. The opponent is a snapshot recorded when the match is created and cannot be edited afterwards.

**Why this priority**: Every recording belongs to a match, so nothing else in this feature works without matches. On its own it already replaces API calls with a usable view of a team's fixtures.

**Independent Test**: Sign in as a Coach of a team, create two matches, edit one, and see both in the team's match list; sign in as a Viewer of the team and observe the list read-only; sign in as a Coach of another team and observe that the first team's matches are not reachable.

**Acceptance Scenarios**:

1. **Given** a Coach of Team A, **When** they open Team A, **Then** the Web UI lists Team A's matches with opponent, kickoff time, venue (home, away, or neutral), and competition, newest kickoff first.
2. **Given** a Coach of Team A or a Club Admin, **When** they create a match for Team A with the required details, **Then** the match is saved and appears in the team's match list and opens on its match view.
3. **Given** an existing match, **When** a Coach of its team or a Club Admin edits its kickoff time, venue (home, away, or neutral), or competition, **Then** the change is saved; the opponent is shown read-only and is never sent as a change.
4. **Given** a Viewer of Team A, **When** they open Team A and one of its matches, **Then** they see the matches and match details without any create, edit, upload, mapping, or finalize action.
5. **Given** a match that someone else changed since it was loaded, **When** a member saves their edit, **Then** the save is refused, nothing is overwritten, and the Web UI offers to reload the current version.
6. **Given** a team in an archived season, **When** a Coach opens it, **Then** its matches are shown read-only as the platform defines, and any refused change is shown with the platform's reason.

---

### User Story 2 - Upload a recording to a match (Priority: P2)

A Coach opens a match and adds one or more recordings, for example the main camera and a second angle. For each file, the Web UI prepares the upload by computing the file's integrity information, starts an upload for the match, and transfers the file in parts directly to the club's object storage with the short-lived grants the platform issued. The platform never receives the video bytes. Each file shows its own progress. When a part fails, the Web UI retries it. When all parts are stored, the Coach confirms how the recording's media time maps to match time, and the Web UI completes the upload. The recording then appears on the match as an accepted recording version with its timeline mapping.

**Why this priority**: Uploading is the reason Coaches open the Web UI after a match. It needs matches (Story 1) and delivers accepted, verified recordings that can be finalized.

**Independent Test**: As a Coach, add two small test files to a match, observe per-file preparation and transfer progress, set a timeline mapping for each, and complete both; inspection shows two accepted recording versions with their mappings, and the platform API received no media bytes.

**Acceptance Scenarios**:

1. **Given** a Coach of the match's team or a Club Admin, **When** they add a recording file with a display name and optional description, **Then** the Web UI prepares it with visible progress, starts an upload session on the platform, and transfers its parts directly to object storage with visible progress, without sending media bytes to the platform API.
2. **Given** several files added to the same match, **When** they are uploaded, **Then** one file transfers at a time while the others wait in a visible queue, each file has its own progress and outcome, the member can remove a waiting file from the queue, and one file's failure does not stop or corrupt the others.
3. **Given** a part transfer that fails because of a network error or a refused write, **When** the failure is retryable, **Then** the Web UI retries that part with increasing delays a bounded number of times and continues; when retries are exhausted, it shows the file as paused with a retry action and keeps the parts already stored.
4. **Given** part grants that expired during a long transfer, **When** the Web UI needs to send further parts, **Then** it requests fresh grants for the remaining parts and continues without restarting the upload.
5. **Given** all parts stored, **When** the member sets a timeline mapping and completes the upload, **Then** the recording appears on the match as an accepted recording version with its timeline mapping, size, and content digest.
6. **Given** a completion the platform refuses because a part is missing or does not match the declaration, **When** the refusal arrives, **Then** the Web UI shows which problem occurred, keeps the session pending, and offers to transfer the affected parts again and retry completion.
7. **Given** a file that is not identified as video by the browser, **When** the member tries to add it, **Then** the Web UI refuses it with a message that only video files are accepted; **and Given** a video file larger than the platform's maximum recording size or otherwise refused at upload start, **When** the member adds it, **Then** the Web UI shows the platform's reason; in both cases no upload is created.
8. **Given** a Viewer of the team or a Coach of another team, **When** they open the match, **Then** no upload action is offered, and a directly attempted upload is refused by the platform and shown as not permitted.
9. **Given** an upload in progress, **When** the member navigates to another view of the Web UI, **Then** the upload continues and stays visible in an upload indicator; **and When** they try to close or reload the page, **Then** the browser asks for confirmation before leaving.

---

### User Story 3 - Finalize the match's recording set (Priority: P3)

When all recordings of a match are uploaded, the Coach finalizes the recording set: they choose which recording versions belong to it, put them in order, and confirm the timeline mapping used for each, by default the newest mapping of that version. Finalizing freezes the set as a new recording-set version, which the platform uses to start analysis. The match view then shows the finalized set and the state of the analysis run created from it.

**Why this priority**: Finalization is the hand-off from upload to analysis. It needs accepted recordings (Story 2).

**Independent Test**: With two accepted recordings on a match, finalize a set with both in a chosen order; the match view shows the new set version with its ordered members and digests, and, once the platform creates it, the analysis run for the set with its current state.

**Acceptance Scenarios**:

1. **Given** a match with accepted recording versions, **When** a Coach of its team or a Club Admin selects versions, orders them, and finalizes, **Then** a new recording-set version is created with exactly that order and the chosen mapping per version, and is shown on the match.
2. **Given** a selection, **When** the member has not chosen a mapping for a version, **Then** the Web UI uses that version's newest mapping and shows it before confirmation.
3. **Given** a finalize request that times out or loses its connection, **When** the Web UI retries it, **Then** it reuses the same retry key, so at most one recording-set version results.
4. **Given** an empty selection or more recordings than the platform allows in one set, **When** the member tries to finalize, **Then** finalization is not sent or is refused with the platform's reason, and nothing is created.
5. **Given** a finalized recording-set version, **When** the platform has created analysis runs for it, **Then** the match view shows each run's state as the platform reports it and refreshes it while the run is not finished; no analysis result is shown.
6. **Given** a selection identical in versions, order, and mappings to the match's newest finalized set, **When** the member finalizes, **Then** the Web UI warns that this starts another analysis and finalizes only after explicit confirmation; cancelling creates nothing.

---

### User Story 4 - Recover an interrupted upload (Priority: P4)

Long recordings take a long time to upload, and uploads get interrupted: the browser reloads, the network drops, the session ends, or the laptop goes to sleep. When the Coach comes back to the match in the same browser, the Web UI shows each pending upload with its progress. The Coach selects the same file again, the Web UI checks that it is the same file, and the transfer resumes with the parts that are still missing. A Coach who no longer wants an upload abandons it; the stored parts are then discarded when the platform's upload session expires.

**Why this priority**: Without recovery, a dropped connection near the end of a large upload means starting over. It builds on Story 2.

**Independent Test**: Start an upload of a multi-part test file, reload the page after some parts are stored, select the same file again, and observe that only the missing parts are transferred and the upload completes; select a different file and observe the refusal; abandon another pending upload and observe that it disappears from the match.

**Acceptance Scenarios**:

1. **Given** a pending upload started in this browser, **When** the member opens the match again after a reload or restart, **Then** the Web UI lists the pending upload with its file name, progress, and the time the platform session expires.
2. **Given** a pending upload, **When** the member selects a file to resume it, **Then** the Web UI checks that the file matches the upload's declaration and, if it does, transfers only the parts not yet confirmed as stored; if it does not match, it refuses to resume with a clear message and changes nothing.
3. **Given** a member whose platform session ends during an upload, **When** the next platform request is refused, **Then** transfers pause, the member is returned to sign-in, and after signing in as the same member they can resume the upload from the match.
4. **Given** the platform is unreachable or the network drops during an upload, **When** connectivity returns, **Then** paused transfers resume without a page reload.
5. **Given** a pending upload, **When** the member abandons it, **Then** the Web UI stops its transfers, forgets it in this browser, and states that the stored parts are discarded when the upload expires.
6. **Given** a pending upload whose platform session has expired, **When** the member opens the match, **Then** the Web UI shows it as expired, offers to start a new upload with the same file, and removes it from the list when acknowledged.
7. **Given** a member with pending uploads, **When** they explicitly sign out (confirming first if an upload is transferring), **Then** all of their remembered uploads are deleted from the browser and are not offered after the next sign-in.

---

### User Story 5 - Review the lineage and correct evidence (Priority: P5)

Every member who can see a match (its team's Coaches and Viewers, and Club Admins) sees its recording lineage: every recording version with size, content digest, and upload time; every timeline mapping with its spans and digest; and every finalized recording-set version with its ordered members, digests, and the state of its analysis runs. A Coach who notices a wrong mapping adds a revised mapping for that recording version, uploads a corrected recording as a new version, and finalizes a new set version. Earlier versions stay visible and unchanged.

**Why this priority**: Corrections are expected in practice, and lineage review explains what an analysis was based on. The core path (Stories 1–3) works without it.

**Independent Test**: For a match with a finalized set, revise one recording's mapping and finalize a new set using it; the lineage shows both mappings and both set versions unchanged, and a Viewer of the team sees the same lineage without change actions.

**Acceptance Scenarios**:

1. **Given** a match with recordings, mappings, and finalized sets, **When** a Viewer, Coach, or Club Admin with access opens its lineage, **Then** the Web UI shows all recording versions, mappings, and set versions with their identities, digests, and order, and offers no media playback or download.
2. **Given** an accepted recording version, **When** a Coach of the team or a Club Admin submits a revised timeline mapping, **Then** a new mapping is shown for that version and the earlier mapping remains shown and unchanged.
3. **Given** a finalized set, **When** the Coach finalizes a corrected selection or order, **Then** a new set version is shown alongside the earlier one, which remains unchanged.
4. **Given** any accepted recording version, mapping, or finalized set, **When** any member views it, **Then** the Web UI offers no action to edit, reorder, or delete it.
5. **Given** a timeline mapping the platform refuses as invalid (for example overlapping spans or an end before its start), **When** the member submits it, **Then** the Web UI shows the platform's reason next to the affected span and nothing is created.

### Edge Cases

- **Very large files**: Preparing a multi-gigabyte file takes noticeable time; the Web UI stays responsive, shows preparation progress, and lets the member cancel preparation without starting an upload.
- **Non-video file**: A file the browser does not identify as video (for example a document, an image, or an unrecognized type) cannot be selected or dropped as a recording; the Web UI explains that only video files are accepted and starts nothing.
- **Same file added twice**: The Web UI warns that the file seems to be already uploaded or uploading for this match and lets the member continue or cancel; the platform still accepts a deliberate second upload as a new recording version.
- **File changed on disk after preparation**: Parts whose bytes no longer match the declaration are refused by storage; the Web UI shows the upload as failed and asks the member to start a new upload.
- **Membership or role revoked during an upload**: Further grants and completion are refused; the Web UI shows the upload as not permitted, stops transfers, and offers only to abandon it.
- **Match opened in two tabs**: Both tabs show the same pending uploads; only one tab transfers a given upload at a time, and the other shows it as running elsewhere.
- **Different member in the same browser**: Pending uploads remembered in the browser are shown only to the member who started them; an explicit sign-out deletes all of that member's remembered uploads, so the next member sees none of them, and the platform discards their stored parts at expiry.
- **Object storage unreachable while the platform is reachable**: Transfers pause with a storage-unavailable message and retry; no recording is reported as accepted until the platform confirms completion.
- **Platform refusal during completion because the session expired**: The Web UI shows the upload as expired and offers to start again with the same file.
- **Timeline mapping input**: Minutes and seconds are entered in a familiar format; the Web UI converts them to the platform's representation and shows the platform's refusal for any invalid mapping. When the local file's duration cannot be read (for example an unusual container format), no span end is proposed and the member enters it.
- **Unexpected platform error**: The Web UI shows a generic error with a correlation reference and no internal details, as in the foundation.

## Requirements *(mandatory)*

### Functional Requirements

#### Matches

- **FR-001**: The Web UI MUST list a team's matches for every member whose roles allow viewing that team, with opponent, kickoff time, venue (home, away, or neutral), and competition, newest kickoff first, using the platform's paging where the list is long.
- **FR-002**: Coaches of the team and Club Admins MUST be able to create a match with the details the platform requires and edit its kickoff time, venue (home, away, or neutral), and competition; the opponent MUST be shown read-only after creation.
- **FR-003**: Match edits MUST send the version the match was loaded with; a refused stale save MUST NOT overwrite anything and MUST offer to reload the current version.
- **FR-004**: Viewers MUST see matches, recordings, lineage, and analysis run states without any create, edit, upload, mapping, or finalize action; the platform's refusal remains authoritative for any attempted change.

#### Upload

- **FR-005**: Coaches of the match's team and Club Admins MUST be able to add one or more recording files to a match, each with a display name and an optional description. Only files the browser identifies as video MAY be selected; the Web UI MUST refuse any other file, including one whose type the browser cannot identify, with a clear message, and MUST declare the file's video content type to the platform.
- **FR-006**: Before starting an upload, the Web UI MUST compute the integrity declaration the platform requires (total size, fixed part size, and a content digest per part) locally in the browser, without uploading the file anywhere, and MUST stay responsive and show progress while doing so.
- **FR-007**: The Web UI MUST transfer recording bytes only to object storage, using the per-part grants the platform issues and the headers each grant requires, and MUST NOT send media bytes to the platform API.
- **FR-008**: The Web UI MUST transfer one file at a time per browser, with a small fixed number of that file's parts in parallel, while further files wait in a visible queue from which the member can remove them; it MUST show per-file preparation and transfer progress and outcome, and one file's failure MUST NOT stop or corrupt other files' uploads (a paused or failed file yields its turn to the next file in the queue).
- **FR-009**: The Web UI MUST retry a failed retryable part transfer with increasing delays a bounded number of times, then pause the file with a manual retry action, keeping parts already stored.
- **FR-010**: The Web UI MUST request fresh grants for the remaining parts when grants expire and continue the upload without restarting it.
- **FR-011**: The Web UI MUST complete an upload only after every declared part is confirmed as stored and the member has provided a timeline mapping; a refused completion MUST keep the session pending and offer to transfer the affected parts again.
- **FR-012**: Uploads MUST continue while the member navigates within the Web UI and MUST be visible in an upload indicator; leaving or reloading the page while an upload is transferring MUST ask for confirmation.
- **FR-013**: State-changing upload, completion, and finalization requests MUST carry retry keys and reuse them on retries of the same intent, so a retried request never creates a second upload session, recording version, or recording-set version.

#### Recovery

- **FR-014**: The Web UI MUST remember pending uploads in the browser per member, holding only identifiers, file name, size, declaration, and the parts confirmed as stored; it MUST NOT remember upload grants, session secrets, or file contents. Remembered uploads MUST survive reloads, session expiry, and a session ended by the platform, and MUST all be deleted when the member explicitly signs out; signing out while an upload is transferring MUST ask for confirmation first and then stop its transfers.
- **FR-015**: After a reload, restart, session end, or network loss, the Web UI MUST let the member resume a pending upload by selecting the file again, MUST verify that the file matches the upload's declaration before transferring anything, and MUST transfer only the parts not yet confirmed as stored.
- **FR-016**: Transfers MUST pause when the platform or object storage is unreachable or the session ends, and MUST resume without a page reload once connectivity or the member's session is restored.
- **FR-017**: The member MUST be able to abandon a pending upload; the Web UI MUST stop its transfers, forget it in this browser, and state that the platform discards the stored parts when the upload expires. Expired uploads MUST be shown as expired with an offer to start again.

#### Timeline mapping and finalization

- **FR-018**: The Web UI MUST let the member define a timeline mapping as one or more spans, each mapping a media time range to the match time at which it starts. It MUST read the recording's duration from the local file without playing or uploading it and propose one span covering the whole recording, so the member enters only the match time at which the recording starts; the member MUST be able to add, edit, and remove spans for gaps such as half-time. When the duration cannot be read, the member enters the span's media end. The Web UI MUST show the platform's refusal for an invalid mapping next to the affected span.
- **FR-019**: Coaches of the team and Club Admins MUST be able to add a revised timeline mapping to an accepted recording version; earlier mappings MUST remain shown and unchanged.
- **FR-020**: Coaches of the team and Club Admins MUST be able to finalize a recording set by selecting accepted recording versions, ordering them, and choosing one mapping per version (the newest by default); the Web UI MUST NOT send an empty selection or one larger than the platform's limit. When the selection (versions, order, and mappings) is identical to the match's newest finalized set, the Web UI MUST warn that finalizing starts another analysis and finalize only after explicit confirmation.
- **FR-021**: The Web UI MUST offer no action that edits, reorders, or deletes an accepted recording version, timeline mapping, or finalized recording-set version.

#### Lineage and analysis run states

- **FR-022**: Every member who can view the match MUST see its recording lineage: recording versions (display name, size, content digest, upload time), timeline mappings (spans, digest, time), and finalized recording-set versions (ordered members with their digests, finalization time).
- **FR-023**: For each finalized recording-set version, the Web UI MUST show the analysis runs the platform created from it with their current state as the platform reports it, and MUST refresh unfinished runs while the match view is open; it MUST NOT show analysis results.
- **FR-024**: The Web UI MUST offer no media playback, preview, or download of recordings.

#### Platform access, security, and quality

- **FR-025**: The Web UI MUST access the platform only through the generated transport client behind application-owned adapters, as the foundation established, and MUST show platform refusals (authorization, validation, conflict, version required, idempotency) with their reasons rather than second-guessing them.
- **FR-026**: The Web UI's Content Security Policy MUST allow connections to the stamp's object-storage origin only for transfers and MUST otherwise keep the foundation's client security baseline (Trusted Types, no third-party scripts, Subresource Integrity).
- **FR-027**: In local development through the Aspire AppHost, the development object storage MUST accept the browser transfers from the Web UI origin (allowing only the methods and headers the grants require and exposing the response headers the Web UI needs to confirm a stored part).
- **FR-028**: All new user-facing text MUST come from translation keys with English as the only shipped language, and all new views MUST be operable by keyboard with visible focus and pass automated WCAG 2.2 AA rule checks, as the foundation requires.

### Key Entities

- **Match (client view)**: A team's fixture with opponent snapshot, kickoff time, venue (home, away, or neutral), competition, and version, as exposed by the platform.
- **Upload session**: The platform's pending, completed, or expired upload of one recording for a match, with its declaration (total size, part size, part count, composite digest) and expiry time.
- **Local upload record**: What the browser remembers about a pending upload for one member: session identity, match, file name and size, declaration, and parts confirmed as stored; never grants, secrets, or file contents.
- **Part grant**: A short-lived permission from the platform to store one declared part in object storage, used once and never remembered.
- **Recording version**: An accepted, immutable recording of a match with its size and content digest.
- **Timeline mapping**: An immutable set of spans relating a recording version's media time to match time, with its digest; revisions are new mappings.
- **Recording-set version**: An immutable, ordered selection of recording versions with one mapping each, finalized for analysis.
- **Analysis run (state only)**: A run the platform created from a finalized recording-set version, shown only with its state.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Coach can go from opening a team to a finalized recording set of a new match with two recordings in under 5 minutes of their own interaction, excluding file preparation and transfer time, using only the Web UI.
- **SC-002**: 100% of the acceptance scenarios of this specification are covered by automated unit or browser tests that run in the Web UI verification environment, and an end-to-end set against the real platform and a disposable object storage (create a match, upload a multi-part recording, complete it with a mapping, finalize a set, see the analysis run state) passes on every merge.
- **SC-003**: In 100% of browser tests, the platform API receives 0 bytes of recording content; all recording bytes go to object storage.
- **SC-004**: After an interruption (reload, network loss, expired grants, or session end), a resumed upload transfers 0 parts that were already confirmed as stored and completes successfully in 100% of the recovery browser tests.
- **SC-005**: In 100% of browser tests that retry a state-changing request, at most one upload session, recording version, or recording-set version results from one member intent.
- **SC-006**: While a recording of at least 1 GiB is prepared and transferred, the member can navigate between views, and no interaction with the Web UI is blocked for more than 200 ms.
- **SC-007**: 0 upload grants, session secrets, or recording bytes are found in browser storage in the browser tests.
- **SC-008**: 0 Content Security Policy violations are reported in the browser tests, and automated WCAG 2.2 AA rule checks report 0 violations on every new view.

## Assumptions

- **Actors**: As the architecture states, only Club Admins and Coaches of the match's team create and edit matches, upload recordings, revise mappings, and finalize sets; Viewers of the team see matches, lineage, and analysis run states read-only; Registrars have no additional views in this slice.
- **API coverage**: Every operation used here already exists in the platform API from Club and Identity Foundation, Recording Lineage and Upload, and Durable Analysis Workflow (team match list, match create, read, and update; upload session start, read, grants, and completion; timeline mapping revision; recording-set finalization and read; recording lineage; recording version and mapping reads; analysis runs per match). This feature adds no platform behavior. If a needed read operation proves missing, the plan names it and keeps the platform change minimal and within that feature's design.
- **Pending uploads across browsers**: The platform offers no list of pending upload sessions, so recovery works in the browser that started the upload. In another browser, a pending upload is not shown and expires as the platform defines; the member starts a new upload there.
- **Abandon**: The platform has no abandon operation. Abandoning forgets the upload in the browser; the platform discards the stored parts when the session expires.
- **Part size and limits**: The Web UI chooses a fixed part size within the platform's limits (storage minimum part size, maximum part count, and maximum recording size); the platform's refusals remain authoritative.
- **Local development storage**: Browser transfers in local development need the development object storage to accept requests from the Web UI origin, and the Web UI's Content Security Policy to allow its origin. These are development settings only; production storage origins, cross-origin settings, and deployment values remain unresolved and out of scope.
- **Browser tests and the platform**: Most browser tests run against a simulated API and a simulated object storage in the browser. The end-to-end set runs against the real platform API with a disposable database and a disposable object storage, which the verification environment supports with the .NET SDK and the container runtime; the plan decides how it starts them, following the foundation's end-to-end setup.
- **Browsers and devices**: Automated tests use Chromium; the supported-browser matrix starts with Chromium-based browsers on Windows, as the architecture states. Uploads from phones are possible but not optimized in this slice.
- **Language and accessibility**: English only through translation keys, keyboard operation, and WCAG 2.2 AA rule checks, as in the foundation.
- **Out of scope**: video playback, preview, and download; segment materialization; analysis results, review, and analytics; agent conversations; the service worker, offline mode, and queued uploads; the Analyst Manager fleet view; a platform list of pending upload sessions or an abandon operation; production storage, cross-origin, and deployment values; browsers other than Chromium in automated tests.
- **Evidence**: Passing validation is development evidence only and does not establish deployment support or production readiness.
- **Dependencies**: `specs/archive/20261012-150657-web-ui-foundation` (Web UI Foundation) is merged before this feature starts and provides the Web UI project, session, navigation, generated client, security baseline, and browser-test setup; it in turn depends on `specs/archive/20261012-145505-web-ui-environment`. `specs/archive/20261005-130702-recording-lineage-upload` provides the upload, timeline mapping, finalization, and lineage API. `specs/archive/20261005-130701-club-identity-foundation` provides the match API, roles, and team authorization. `specs/archive/20261005-130703-durable-analysis-workflow` provides the analysis runs per match.
- **Architecture References**: [docs/architecture/client-applications.md](../../../docs/architecture/client-applications.md) (Web UI), [docs/architecture/match-data-pipeline.md](../../../docs/architecture/match-data-pipeline.md) (Upload; Planned Recording-Lineage Foundation), [docs/architecture/terminology-and-principles.md](../../../docs/architecture/terminology-and-principles.md) (S3 as universal data plane), [docs/architecture/contracts-and-compatibility.md](../../../docs/architecture/contracts-and-compatibility.md) (Generated Client Boundary), [docs/architecture/security-and-data-governance.md](../../../docs/architecture/security-and-data-governance.md) (Web UI security baseline).
