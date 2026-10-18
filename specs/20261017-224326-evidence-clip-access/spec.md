# Feature Specification: Evidence Clip Access

**Feature Branch**: `20261017-224326-evidence-clip-access`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Evidence Clip Access: the architecture says every accepted concept carries evidence time ranges and segment identifiers, and the API and MCP get_video_clip() operation resolve those references to short-lived, team-authorized match media access (docs/architecture/analyst-capability-catalog.md; docs/architecture/intelligence-and-agents.md#mcp-tools), while video bytes never pass through the API (docs/architecture/terminology-and-principles.md#3-s3-as-universal-data-plane; constitution: the control plane never proxies video bytes). Materialized Video Segments (batch sibling) materializes immutable segment media and Accepted Fact Indexing indexes accepted facts with time ranges. This feature adds a member-facing, team-authorized, match-scoped /api/v1 operation that resolves an accepted, indexed fact of a run (or an explicit match-time range within one run's evidence) to short-lived presigned read access for the materialized segment media that cover it, returning for each covering segment its presigned URL (never stored, never logged, expiring after a configured short lifetime), its media digest, and the start and end offsets within that media that correspond to the requested range, plus explicit states when media is not yet materialized (the operation may request on-demand materialization through the Segment Service and report materializing with retry guidance), purged and rematerializing, failed, or unavailable because the run's recording set version has no such coverage; authorization is re-evaluated on every request, access is audited as artifact authorization without the URL, and rate limits apply. The Web UI analysis review offers a play evidence action on facts that plays the resolved media in the browser from the given offsets (spanning two segments when needed), with explicit loading, materializing, unavailable, and expired-link states and automatic re-resolution when a link expires; the development object-store CORS allow-list and the Web UI Content Security Policy permit media playback from the store's origin only. Out of scope: the MCP get_video_clip() tool and agents, downloads and export, clip editing or new encodes beyond what the Segment Service provides, sharing links, production lifetimes and CDN, offline playback, playback of whole recordings. Depends on specs/20261017-222433-materialized-segments, specs/20261017-175648-fact-indexing-queries (#144), specs/20261017-181118-web-ui-analysis-review (#145), Recording Lineage and Upload, Club and Identity Foundation, Web UI Foundation. Implements the API side of get_video_clip() in docs/architecture/analyst-capability-catalog.md and docs/architecture/intelligence-and-agents.md#mcp-tools (without MCP), docs/architecture/client-applications.md#web-ui (analysis review evidence playback), and the artifact-authorization audit in docs/architecture/security-and-data-governance.md#audit-events."

## Clarifications

### Session 2026-10-17

- Q: When several recordings of the run's recording set cover the same match time, which recording's media should
  evidence resolution return? → A: For each part of the requested range, the first recording in the recording set's
  order that covers it; other recordings are not returned.
- Q: How should a very long fact or range be handled? → A: Resolve at most a configured maximum duration (development
  10 minutes): a fact longer than that is resolved from its start up to the maximum and the response says it was
  shortened; an explicit range longer than the maximum is rejected as invalid.
- Q: Should resolving a part whose segment failed request its materialization again? → A: Only when the request
  explicitly asks to retry failed parts (the player's "try again"); otherwise the part is reported as `failed` without
  a new request.
- Q: Should evidence playback include video before and after a very short fact, and how? → A: A fact resolution may
  ask for context before and after the fact, each bounded by a configured maximum (development 10 seconds), clipped to
  zero; the response marks the fact's own window, and the Web UI asks for 5 seconds on each side and highlights the
  fact's window while playing.

## User Scenarios & Testing *(mandatory)*

The actors are the club members who may read a match's analysis (Club Admins, and Coaches and Viewers of the team
that owns the match), the platform that authorizes media access and asks the Segment Service of
[Materialized Video Segments and Analyst Input Staging](../20261017-222433-materialized-segments/spec.md) for segment
media, and the browser that plays the media directly from object storage. The design follows the evidence rule of the
[Analyst Capability Catalog](../../docs/architecture/analyst-capability-catalog.md), the `get_video_clip()` operation of
[MCP Tools](../../docs/architecture/intelligence-and-agents.md#mcp-tools) (its API side only), and
[S3 as Universal Data Plane](../../docs/architecture/terminology-and-principles.md#3-s3-as-universal-data-plane).

### User Story 1 - Resolve a fact's evidence to short-lived media access (Priority: P1)

A Coach reading a run's facts wants to see the video behind one fact. The client asks the platform to resolve that
fact's evidence. The platform checks, at that moment, that the Coach may read the run's match, finds the materialized
segment media of the run's finalized recording set that cover the fact's match-time range, and returns, for each
covering segment in match-time order, a short-lived read link to that segment's media, the media's digest, and the
start and end offsets inside that media that correspond to the part of the fact's range the segment covers. The
platform never reads or relays the video itself, never stores the links, and never writes them to logs or audit
records. Each grant of access is recorded as an audited artifact authorization.

**Why this priority**: It is the API side of `get_video_clip()` and the only safe way for any client to see evidence
video; the Web UI playback (Story 3) and later agent tools build on it.

**Independent Test**: For a run of a short synthetic recording whose segments are materialized, a Coach of the team
resolves a fact that lies inside one segment and a fact that crosses a segment boundary; the first returns one media
piece and the second two, in match-time order, each with a link that downloads bytes whose digest equals the returned
digest, offsets that select exactly the fact's range, and an expiry within the configured lifetime; a security audit
event exists per resolution without the link; a member without a role on the team receives "not found".

**Acceptance Scenarios**:

1. **Given** an accepted, indexed fact of a run whose covering segments are available, **When** a reader of the run's
   match resolves the fact's evidence, **Then** the response lists, in match-time order, one media piece per covering
   segment (one per covered timeline span of it when its mapping has a cut inside the range) with a read link, the
   media digest, the start and end offsets inside that media, the match-time window the
   piece covers, and the link's expiry, and the pieces together cover the fact's whole range.
2. **Given** a resolved link, **When** its expiry passes, **Then** the link no longer grants access, and resolving
   again returns new links after authorization is evaluated again.
3. **Given** a member whose team role was revoked after an earlier resolution, **When** they resolve again, **Then**
   the platform refuses as for any other non-reader and issues no link.
4. **Given** any resolution, **When** it issues links, **Then** one security audit event records the actor, team,
   match, run, the fact or range, the media pieces' digests, and the expiry, and neither the audit event, the
   application logs, nor any stored record contains a link.
5. **Given** a fact that does not belong to the run, a run that does not exist, or a member who may not read the
   run's match, **When** the member resolves, **Then** the platform answers "not found" and discloses nothing, and
   requests without a valid member session are rejected as unauthorized.
6. **Given** a member who resolves more often than the configured rate allows, **When** the limit is exceeded,
   **Then** the platform refuses with a distinct "too many requests" problem that says when to try again, and issues
   no link.
7. **Given** a fact resolution that asks for context before and after the fact, **When** it is resolved, **Then** the
   pieces cover the fact's range extended by the requested context (not below match time zero), and the response
   states the fact's own window inside it; context above the configured maximum is rejected as invalid.

---

### User Story 2 - Explicit states when evidence media is not ready (Priority: P1)

A Viewer resolves a fact whose segments were never materialized, were purged by a Club Admin, failed to materialize,
or lie outside the recording coverage of the run's recording set. Instead of an error or an empty answer, the platform
says per part of the fact's range what the state is: available with a link, materializing (after the platform asked
the Segment Service to materialize it), rematerializing after a purge, failed with a sanitized reason, or not covered
by the run's recordings. For materializing parts it says how long to wait before asking again.

**Why this priority**: Segment media is materialized on demand and may be purged at any time during development;
without explicit states members would see broken or empty players and could not tell missing video from a fault.

**Independent Test**: Resolve facts whose segments are absent, purged, failed, and outside the mapped coverage; the
absent and purged segments are requested from the Segment Service and reported as materializing and rematerializing
with a retry hint, become available, and a later resolution returns links; the failed segment is reported with its
sanitized reason; the uncovered part is reported as not covered; no link is issued for a part that is not available.

**Acceptance Scenarios**:

1. **Given** a fact whose covering segment has never been materialized, **When** a reader resolves it, **Then** the
   platform requests materialization of that segment through the Segment Service, reports the part as `materializing`
   with a retry hint, issues no link for it, and a later resolution after the segment became available returns a
   link.
2. **Given** a segment that a Club Admin purged, **When** a reader resolves a fact it covers, **Then** the platform
   requests materialization again and reports the part as `rematerializing` with a retry hint.
3. **Given** a segment whose materialization failed, **When** a reader resolves a fact it covers, **Then** the part is
   reported as `failed` with the Segment Service's sanitized reason and no link, and no new materialization is
   requested; **When** the reader resolves again asking to retry failed parts, **Then** materialization is requested
   again and the part is reported as `materializing`.
4. **Given** a fact range that the run's recording set does not cover in some or all of its match time, **When** a
   reader resolves it, **Then** each uncovered part is reported as `not-covered` with its match-time window, and no
   materialization is requested for it.
5. **Given** an available segment whose stored media no longer matches its recorded digest, **When** a reader
   resolves a fact it covers, **Then** no link is issued, the segment is handled as invalid media by the Segment
   Service, and the part is reported as `failed`.
6. **Given** a resolution where some parts are available and others are not, **When** the response is returned,
   **Then** the available parts carry links and the others carry their states, so a client can play what exists and
   explain the rest.

---

### User Story 3 - Play a fact's evidence in the analysis review (Priority: P2)

In the Web UI's analysis review, a Coach browses a capability's facts and chooses "Play evidence" on one fact. A
player opens and plays the video of the fact's match-time range with a few seconds of context around it, continuing
seamlessly from one segment into the next when the range crosses a segment boundary. While the platform materializes media the player says so and
tries again by itself; when parts are failed or not covered it says which; when a link expires during viewing it
fetches new links and continues where the Coach was.

**Why this priority**: It is the member-facing value of evidence access; it needs Stories 1 and 2.

**Independent Test**: In the simulated browser tests, play a fact inside one segment, a fact across two segments, a
fact whose media is materializing and then becomes available, a fact with a failed and a not-covered part, and a fact
whose link expires during playback; each shows its own state, plays the right offsets, and resumes after
re-resolution; against the running platform, play the evidence of a fact of a synthetic recording.

**Acceptance Scenarios**:

1. **Given** a fact in the analysis review, **When** a reader chooses "Play evidence", **Then** a player opens, shows
   a loading state while the evidence is resolved with 5 seconds of context on each side, and plays the media from the
   start of that context to its end, indicating while the position is inside the fact's own window.
2. **Given** a fact whose range crosses a segment boundary, **When** playback reaches the end of the first piece,
   **Then** it continues with the next piece from its start offset without the member acting.
3. **Given** a part that is materializing or rematerializing, **When** the player shows it, **Then** it states that
   the video is being prepared and resolves again after the platform's retry hint, up to a bounded number of times,
   after which it offers to try again.
4. **Given** a part that is failed or not covered, **When** the player reaches it, **Then** it states which match-time
   window cannot be played and why, and continues with the next playable part, if any.
5. **Given** a link that expires while the member watches or pauses, **When** the player needs more media, **Then** it
   resolves the evidence again and continues at the current position; when resolution is refused it shows the
   not-found state.
6. **Given** the player, **When** it is used by keyboard or a screen reader, **Then** it is operable with visible
   focus, announces state changes, and passes automated accessibility rule checks.

---

### User Story 4 - Media playback stays confined to the object store and the session (Priority: P3)

The browser loads evidence media only from the configured object-store origin, with links that come from the
platform for the current member. Media and links are never cached by the service worker or kept in browser storage,
and the review is still online only.

**Why this priority**: It keeps the Web UI's security baseline and the PWA's no-local-data rule with the first feature
that plays remote video.

**Independent Test**: Browser tests check that the Content Security Policy allows media only from local `blob:` and
the configured object-store origin, that the development object store answers playback requests only for the Web UI
origin, that no security-policy violation occurs during playback, and that no media or link is found in service-worker
caches or browser storage afterwards.

**Acceptance Scenarios**:

1. **Given** the Web UI's Content Security Policy, **When** evidence media plays, **Then** media loads only from the
   configured object-store origin (and local `blob:` as before), and no other media origin is allowed.
2. **Given** the development object store, **When** a page of another origin requests evidence media with a valid
   link, **Then** the store does not grant that origin cross-origin access, while the Web UI origin plays it.
3. **Given** a finished playback session, **When** service-worker caches and browser storage are inspected, **Then**
   they contain no evidence media, links, or resolution responses.
4. **Given** the browser is offline, **When** the member chooses "Play evidence", **Then** the connection-required state
   is shown.

### Edge Cases

- A fact range that starts exactly at a segment boundary is covered only by the later segment; one that ends exactly
  at a boundary is covered only by the earlier one.
- A fact whose range is inside one segment but whose mapped coverage has a gap inside the range returns one media
  piece for each covered sub-window and a `not-covered` part for the gap.
- Encoded keyframe context before the requested start is skipped through the start offset; the player never starts
  before the requested range (the fact plus its requested context).
- Two recordings of the set cover the same match time: each part uses the first recording in the set's order that
  covers it, and a later recording is used only where the earlier ones have no coverage.
- A segment is purged while a member watches it: the link stops working, the player resolves again, and reports
  `rematerializing` until the segment is available again.
- Two members resolve the same absent segment at the same time: the Segment Service materializes it once and both
  later receive links to the same media and digest.
- A run that was cancelled, failed, or superseded still resolves the evidence of its accepted facts, because its
  recording set and pinned segmentation policy stay immutable.
- A range given explicitly whose end is not after its start, has negative bounds, or lies completely outside the
  run's match coverage is rejected as invalid or reported as `not-covered` respectively.
- A fact of a match-level capability that spans many minutes produces as many pieces as segments it covers, up to the
  maximum evidence duration; a longer fact is shortened to that maximum from its start and the player says so.
- Context before a fact that starts near match time zero is clipped at zero.
- Clocks of the browser and the platform differ: the player treats a playback failure close to the link's expiry as
  expiry and resolves again rather than trusting its own clock alone.
- Object storage cannot confirm a media checksum or read a segment manifest when links would be issued: the platform
  issues no link and answers with a distinct storage-unavailable problem, and the player reports a recoverable error.

## Requirements *(mandatory)*

### Functional Requirements

#### Evidence resolution

- **FR-001**: The platform MUST provide a member-facing `/api/v1` operation that resolves the evidence of one
  accepted, indexed fact of a run, identified as the fact read of
  [Accepted Fact Indexing and Capability-Scoped Fact Queries](../20261017-175648-fact-indexing-queries/spec.md)
  identifies it (accepted result and position), to the media pieces that cover the fact's half-open match-time range.
  A fact resolution MAY ask for context before and after the fact, each at most the configured maximum context
  (development 10 seconds); the requested range then starts at the fact's start minus the context before (not below
  zero) and ends at the fact's end plus the context after, and the response MUST state the fact's own window.
- **FR-002**: The same operation MUST accept, instead of a fact, an explicit half-open match-time range of the run; the
  range MUST have non-negative integer millisecond bounds, an end after its start, and a duration of at most the
  configured maximum evidence duration (development 10 minutes), and is rejected as invalid otherwise.
- **FR-003**: The platform MUST derive the covering segments only from the run's own finalized recording-set version,
  timeline mappings, and pinned segmentation policy, using the nominal windows and coverage clipping of the
  [Segment Contract](../../docs/architecture/match-data-pipeline.md#segment-contract); it MUST NOT mix media of other
  runs or recording-set versions. Where several recordings of the set cover the same match time, each part of the
  range MUST use the first recording in the recording set's order that covers it.
- **FR-004**: A fact range (including requested context) longer than the configured maximum evidence duration MUST
  be resolved from its start up to that maximum, and the response MUST state that it was shortened and the window it
  covers.
- **FR-005**: For every part of the requested range the response MUST report exactly one of: a media piece
  (`available`), `materializing`, `rematerializing`, `failed`, or `not-covered`, each with its half-open match-time
  window; the parts MUST be ordered by match time and together cover the requested range without overlap.
- **FR-006**: Each available media piece MUST carry a short-lived read link to the segment's media, the media digest,
  the start and end offsets inside that media (in milliseconds of the media's playback timeline) that correspond to
  the piece's match-time window, and the link's expiry.
- **FR-007**: Read links MUST be per-object, read-only, expire no later than the configured evidence link lifetime,
  and grant access to the segment's media object only; the response MUST NOT reveal object-storage keys of other
  objects, source recordings, or credentials.
- **FR-008**: Before issuing a link the platform MUST confirm that the stored media still matches the recorded digest
  and size; on a mismatch it MUST issue no link, hand the segment to the Segment Service's invalid-media handling, and
  report the part as `failed`. When storage cannot confirm the digest, the resolution MUST fail with a distinct
  storage-unavailable problem and issue no link.
- **FR-009**: For a part whose segment is `absent`, the platform MUST request its materialization through the Segment
  Service and report `materializing`, or `rematerializing` when the segment was purged before; for a part whose
  segment is already `materializing` it MUST report it without a new request. Both states MUST carry a retry hint in
  seconds.
- **FR-010**: For a part whose segment is `failed`, the platform MUST report `failed` with the Segment Service's
  sanitized reason and MUST NOT request materialization again, unless the request explicitly asks to retry failed
  parts, in which case it MUST request materialization again and report the part as `materializing`.
- **FR-011**: For a part of the range that the run's recording set does not cover, the platform MUST report
  `not-covered` and MUST NOT request materialization.
- **FR-012**: The platform MUST NOT read, transfer, or relay media bytes for this operation, and MUST NOT store issued
  links or write them to logs, traces, metrics, audit events, or error reports.

#### Authorization, audit, and limits

- **FR-013**: Resolution MUST be authorized on every request as a read of the run's team: Club Admins of the club and
  Coaches and Viewers of the owning team are allowed; every other member, every missing run, and every fact that does
  not belong to the run MUST receive "not found"; requests without a valid member session MUST be rejected as
  unauthorized.
- **FR-014**: Every resolution that issues at least one link MUST write one security audit event of artifact
  authorization with the actor, club, team, match, run, the fact identity or the range, the digests of the media
  granted, and the latest expiry, and without any link.
- **FR-015**: Resolutions MUST be limited per member by a configured rate; requests over the limit MUST be refused with
  a distinct "too many requests" problem that states when to retry, and MUST issue no link and request no
  materialization.
- **FR-016**: The evidence link lifetime, the retry hint, the rate limit, the maximum evidence duration, and the
  maximum context MUST be configurable with validated development values; production values MUST remain unresolved.

#### Web UI evidence playback

- **FR-017**: The analysis review MUST offer a "Play evidence" action for each fact it shows, for every member who may
  read the run.
- **FR-018**: The player MUST resolve the fact's evidence with 5 seconds of context before and after the fact through
  an application-owned adapter over the generated client, play each available piece from its start offset to its end
  offset in match-time order, continue into the next piece without member action, and indicate while playing whether
  the current position is inside the fact's own window.
- **FR-019**: The player MUST show distinct, worded states for: resolving; playing; being prepared (materializing or
  rematerializing), with automatic re-resolution after the retry hint up to a bounded number of times and then a
  "try again" action; a part that failed (with a "try again" action that asks to retry failed parts) or is not
  covered, naming its match-time window; evidence shortened to the maximum duration; link expired and being renewed;
  not found; too many requests, with the waiting time; connection required (offline); and a recoverable error
  (including storage unavailable).
- **FR-020**: When a link expires or media loading fails, the player MUST resolve the evidence again (once per failure)
  and resume at the current playback position.
- **FR-021**: The player MUST be operable by keyboard with visible focus, announce state changes, take all text from
  translation keys, and pass automated WCAG 2.2 AA rule checks.
- **FR-022**: The Web UI's Content Security Policy MUST allow media only from local `blob:` as before and the
  configured object-store origin; the development object store's cross-origin allow-list MUST permit media reads only
  from the Web UI origin.
- **FR-023**: Evidence media, links, and resolution responses MUST NOT be cached by the service worker or kept in
  browser storage, and playback MUST be online only.

#### Evidence and documentation

- **FR-024**: Automated tests MUST cover segment derivation and offsets (inside one segment, across a boundary, at
  boundaries, with coverage gaps), every part state, on-demand and post-purge materialization requests, digest
  checks, link lifetime, authorization on every request, audit content without links, absence of links in logs,
  rate limiting, the Web UI player states, piece sequencing, expiry renewal, the security policy, the online-only
  rule, accessibility, and an end-to-end playback of a synthetic recording's fact against the running platform.
- **FR-025**: The API operation MUST appear in the API's OpenAPI document, and the architecture narratives this feature
  affects MUST describe the implemented state.

### Key Entities *(include if feature involves data)*

- **Evidence Request**: One member's request to resolve either one fact of a run (optionally with context before and
  after it) or one explicit match-time range of a run, optionally asking to retry failed parts.
- **Evidence Part**: One match-time sub-window of the request with its state (`available`, `materializing`,
  `rematerializing`, `failed`, `not-covered`) and, for `failed`, its sanitized reason, and, while being prepared, a
  retry hint.
- **Media Piece**: An available part with a short-lived read link, its expiry, the media digest, and the start and
  end offsets inside the segment media.
- **Evidence Access Audit Event**: The audited artifact authorization of one resolution that issued links, without
  the links.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated tests, 100% of resolved facts are covered by their parts without gap or overlap, and the
  offsets of every media piece select the piece's match-time window within one frame of the synthetic recording.
- **SC-002**: 0 links appear in audit events, application logs, or stored records in the tests that inspect them, and
  every link stops granting access after its expiry.
- **SC-003**: Members without read access to the run's match receive "not found" for 100% of resolutions in the
  authorization tests, including members whose role was revoked after an earlier successful resolution.
- **SC-004**: A purged or never-materialized segment needed for evidence becomes playable without manual steps other
  than waiting, in 100% of the on-demand tests.
- **SC-005**: A Coach goes from a fact in the analysis review to playing its evidence with one action, and playback of
  a fact across two segments continues without a visible stop between pieces in the browser tests.
- **SC-006**: Automated accessibility rule checks report zero violations for every player state, no security-policy
  violation occurs during playback, and no evidence media or link is found in service-worker caches or browser
  storage after a playback session.

## Assumptions

### Dependencies

- [Materialized Video Segments and Analyst Input Staging](../20261017-222433-materialized-segments/spec.md) (batch
  sibling) provides the Segment Service, its materialized-segment states, the segment manifest with its timeline spans
  and encoded interval, on-demand materialization requests, invalid-media handling, and the Club Admin purge; it MUST
  merge before this feature.
- [Accepted Fact Indexing and Capability-Scoped Fact Queries](../20261017-175648-fact-indexing-queries/spec.md)
  (#144) provides indexed facts with their identities and match-time ranges and the team-read authorization of runs;
  it MUST merge before this feature.
- [Web UI Connected Analysis Review](../20261017-181118-web-ui-analysis-review/spec.md) (#145) provides the review and
  its fact browser that gain the "Play evidence" action; it MUST merge before this feature.
- [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md) (#143) provides
  the pinned FFmpeg and ffprobe that the Segment Service and the synthetic test media of this feature's tests need; it
  MUST merge before this feature.
- [Recording Lineage and Upload](../archive/20261005-130702-recording-lineage-upload/spec.md),
  [Club and Identity Foundation](../archive/20261005-130701-club-identity-foundation/spec.md), and
  [Web UI Foundation](../archive/20261012-150657-web-ui-foundation/spec.md) are implemented and provide recording-set
  versions and timeline mappings, object storage with presigned grants and the development cross-origin allow-list,
  member sessions and team authorization, and the Web UI's adapters, security headers, and test projects.

### Architecture Alignment

- Implements the API side of `get_video_clip()` from the
  [Analyst Capability Catalog](../../docs/architecture/analyst-capability-catalog.md) and
  [MCP Tools](../../docs/architecture/intelligence-and-agents.md#mcp-tools), evidence playback in the analysis review of
  [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui), and the artifact-authorization
  evidence of [Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events) for `DAT-004`
  access.
- For evidence playback, video bytes flow only between object storage and the browser, as
  [S3 as Universal Data Plane](../../docs/architecture/terminology-and-principles.md#3-s3-as-universal-data-plane)
  requires.

### Scope Boundaries

- The MCP `get_video_clip()` tool and agents, downloads and export, clip editing and new encodes beyond what the
  Segment Service provides, sharing links, production link lifetimes, rate limits, and CDN delivery, offline playback,
  playback of whole recordings, and a Web UI for explicit ranges are out of scope.
- Evidence resolution reads are audited only when they issue links; refusals follow the platform's existing
  authorization handling.
