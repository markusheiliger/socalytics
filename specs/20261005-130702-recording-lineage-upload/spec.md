# Feature Specification: Recording Lineage and Upload

**Feature Branch**: `20261005-130702-recording-lineage-upload`

**Created**: 2026-10-05

**Status**: Draft

**Input**: User description: "Recording Lineage and Upload: let an authorized Coach or Club Admin upload a Match's source recordings directly to object storage without the platform relaying media bytes, accept each completed upload as immutable recording lineage with its timeline mapping, and finalize an ordered Match recording set whose frozen lineage and finalized-event evidence are committed atomically for later analysis."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Upload a Source Recording for a Match (Priority: P1)

A Coach of a Team (or a Club Admin) has video of a Match the Team played. They start an upload for that Match, transfer the video directly to the club's object storage using a short-lived upload grant issued by the platform, and then complete the upload by confirming the recording and supplying how the recording's media time maps to match time. The platform verifies that the stored video matches what was declared and accepts it as an immutable recording version with its timeline mapping. The video never passes through the platform service itself.

**Why this priority**: Nothing downstream (finalization, analysis, segments) can happen until source recordings exist as trustworthy, verified, immutable evidence. This story alone delivers a usable outcome: a Match's recordings are securely stored and recorded as accepted evidence.

**Independent Test**: For an existing Match, an authorized Coach starts an upload, transfers a test video directly to storage with the returned grant, completes the upload with a valid timeline mapping, and receives the new recording-version and timeline-mapping identities with their digests; inspection confirms the platform service received no media bytes and that denied, mismatched, or unauthorized attempts left no accepted recording.

**Acceptance Scenarios**:

1. **Given** an authenticated Coach of Team A and an existing Match owned by Team A, **When** the Coach starts an upload declaring the recording's descriptive metadata, expected size, and content digest, **Then** the platform creates a pending upload session for that Match and returns a time-limited upload grant for exactly one platform-chosen storage location, and no recording version exists yet.
2. **Given** a pending upload session and its grant, **When** the Coach transfers the video directly to object storage and completes the upload with a valid timeline mapping, **Then** the platform verifies the stored object's existence, size, and digest against the declaration and returns one new immutable recording version and one timeline mapping bound to it, each with its identity and digest.
3. **Given** an authenticated Club Admin, **When** they perform the same upload workflow for any Team's Match, **Then** the workflow succeeds exactly as for that Team's Coach.
4. **Given** a Coach of Team A, **When** they attempt to start or complete an upload for a Match owned by Team B, **Then** the request is forbidden and no upload session, grant, recording version, or timeline mapping is created or changed.
5. **Given** a member with only the Viewer role for the Match's Team, **When** they attempt to start an upload, **Then** the request is forbidden and no state is created.
6. **Given** an unauthenticated caller, **When** they attempt any upload operation, **Then** the request is rejected as unauthenticated and no state is created.
7. **Given** a pending upload session, **When** completion is requested but the stored object is missing or its size or digest differs from the declaration, **Then** completion is rejected, no recording version or timeline mapping is accepted, and the session remains pending so the upload can be corrected and completion retried.
8. **Given** a pending upload session whose grant has expired, **When** the authorized Coach requests a new grant for that session, **Then** a fresh time-limited grant for the same single storage location is issued and no other authority is conveyed.
9. **Given** a completed upload, **When** the same completion request is retried with the same retry key, **Then** the original recording-version and timeline-mapping identities are returned and no duplicate is created.

---

### User Story 2 - Finalize a Match Recording Set for Analysis (Priority: P2)

Once all of a Match's recordings are uploaded, the Coach (or a Club Admin) finalizes the Match's recording set: an ordered list of exactly which recording versions, each paired with its exact timeline mapping, constitute the evidence for analyzing that Match. Finalization freezes that set as a new immutable recording-set version and, in the same all-or-nothing step, records the evidence that the set was finalized so that analysis can later be started from it. Downstream analysis can look up the frozen lineage without reinterpreting it.

**Why this priority**: Finalization is the explicit hand-off from upload to analysis required by the architecture; analysis runs (`specs/20261005-130703-durable-analysis-workflow`) consume exactly this frozen lineage and its finalized-event evidence. It depends on Story 1 providing recordings.

**Independent Test**: With accepted recording versions from Story 1, an authorized Coach finalizes an ordered set and receives a new recording-set-version identity; reading the set returns the same order and identities; exactly one finalized-event record exists for it; invalid, empty, cross-scope, or unauthorized requests leave no set and no event; a simulated failure during finalization leaves neither.

**Acceptance Scenarios**:

1. **Given** a Match with accepted recording versions and timeline mappings, **When** an authorized Coach finalizes a nonempty ordered list of recording-version and timeline-mapping pairs from that Match, **Then** one new immutable recording-set version is created with that exact order, and exactly one recordings-finalized event record describing the same frozen lineage is committed together with it.
2. **Given** a finalization request with no members, **When** it is submitted, **Then** it is rejected as invalid and no recording-set version or event record is created.
3. **Given** a finalization request in which any member belongs to another Match or Team, **When** it is submitted, **Then** the entire request is rejected and no partial set, membership, or event record is created.
4. **Given** a finalization request pairing a recording version with a timeline mapping that belongs to a different recording version, **When** it is submitted, **Then** the entire request is rejected and nothing is created.
5. **Given** a Coach of Team A attempting to finalize for a Match owned by Team B, or a member holding only the Viewer role for the Match's Team, **When** either submits a finalization request, **Then** the request is forbidden and nothing is created.
6. **Given** a failure occurs after finalization processing begins but before it completes, **When** the outcome is inspected, **Then** no recording-set version, membership, successful retry outcome, or event record from that request exists.
7. **Given** a committed finalization, **When** the same request is retried with the same retry key, **Then** the original recording-set version is returned and the number of event records for it remains exactly one.
8. **Given** a finalized recording-set version, **When** an authorized downstream platform capability looks it up, **Then** it receives the Match, Team, ordered recording-version and timeline-mapping identities, and their digests through one lookup operation, without needing object storage access.

---

### User Story 3 - Correct Evidence Without Rewriting History (Priority: P3)

A Coach discovers that a recording's timeline mapping was wrong (for example, the recording actually starts several minutes into the match), that a wrong file was used, or that a recording is missing from the finalized set. They correct it by supplying a revised timeline mapping, uploading a corrected recording as a new recording version, and finalizing a new recording-set version. Everything previously accepted or finalized remains exactly as it was, so any analysis already based on it stays auditable.

**Why this priority**: Corrections are expected in practice and the architecture requires them to create new versions rather than overwrite evidence. The core upload and finalization flows (P1, P2) are usable without corrections, so this follows them.

**Independent Test**: After a set is finalized, revise one recording's timeline mapping and finalize a new set referencing the revised mapping; verify the revised mapping has a new identity and digest, the new set has a new identity, and the original mapping and set read back unchanged.

**Acceptance Scenarios**:

1. **Given** an accepted recording version and its timeline mapping, **When** an authorized Coach submits a revised timeline mapping for that recording version, **Then** a new timeline mapping with a new identity and digest is bound to the same recording version and the prior mapping remains unchanged and addressable.
2. **Given** an accepted recording version, **When** the Coach uploads a corrected video for the same Match, **Then** the correction is accepted as a new recording version with new identities, and the prior recording version remains unchanged and addressable.
3. **Given** a finalized recording-set version, **When** the Coach finalizes a corrected membership or order, **Then** a new recording-set version with its own single event record is created, and the earlier set version, its order, and its event record remain unchanged.
4. **Given** any accepted recording version, timeline mapping, or finalized recording-set version, **When** any actor (including a Club Admin) attempts to modify, reorder, reassign, or delete it, **Then** no operation permits the change and the record remains unchanged.

---

### User Story 4 - Review a Match's Recording Lineage (Priority: P4)

Members with access to a Team (its Coaches and Viewers, and Club Admins) can see which recording versions, timeline mappings, and finalized recording-set versions exist for one of the Team's Matches, so they can choose what to finalize and understand which evidence an analysis used.

**Why this priority**: Useful for building a correct set and for transparency, but finalization can be performed with identities returned by the upload workflow, so it is not required for the MVP.

**Independent Test**: For a Match with uploads, mapping revisions, and two finalized set versions, a Viewer of the Match's Team lists the Match's lineage and sees every recording version, mapping, and set version with ordered members and digests; a member of another Team is forbidden.

**Acceptance Scenarios**:

1. **Given** a Match with accepted recordings and finalized set versions, **When** a Viewer, Coach, or Club Admin with access to the Match's Team requests its recording lineage, **Then** they receive its recording versions, timeline mappings, and finalized recording-set versions with ordered memberships and digests, and no media access or upload grant.
2. **Given** a member with no role for the Match's Team, **When** they request that Match's recording lineage, **Then** the request is forbidden and no lineage is disclosed.

---

### Edge Cases

- **Abandoned or partial upload**: A session whose upload is never completed stays pending; any object stored at its location never becomes accepted lineage, cannot be finalized, and is not listed as a recording version. Automatic expiry and cleanup of abandoned sessions and objects are deferred.
- **Wrong file uploaded**: Completion with a size or digest mismatch is rejected; the session stays pending and the caller may re-upload to the same single location with a valid grant and retry completion.
- **Storage cannot attest integrity**: If object storage cannot provide integrity evidence sufficient to confirm the declared digest, completion is rejected (fail closed) rather than trusting the caller's declaration.
- **Object storage unavailable**: Start, grant, or completion requests fail explicitly with a dependency-unavailable outcome and change no state.
- **Duplicate completion**: A second completion of an already-completed session with a different retry key is rejected as a conflict and creates no second recording version.
- **Duplicate finalization**: Identical concurrent or repeated finalization requests with the same retry key yield one recording-set version and one event record; requests with distinct retry keys are distinct finalizations that each create their own set version.
- **Retry-key reuse with different content**: Reusing a retry key for the same operation and Match with different request content is rejected as a conflict; the original outcome is unchanged.
- **Same retry key, different Match or operation**: Evaluated independently and conveys no authority across Matches.
- **Membership or role revoked mid-upload**: Authorization is re-evaluated at grant issuance, completion, and finalization; after revocation these are forbidden. An already-issued grant may still permit storing bytes until it expires, but those bytes can never be accepted as lineage by the revoked member.
- **Replay after revocation**: A retried request with a previously successful retry key is re-authorized first; a member who lost authority is forbidden and the stored outcome is not disclosed.
- **Finalized set modification attempts**: No operation modifies, reorders, or deletes a finalized set version, its memberships, or its event record; corrections only create new versions.
- **Same recording twice in one set**: A finalization listing the same recording version more than once is rejected.
- **Match in an archived Season**: Archived Seasons are read-only, so mutating recording operations for their Matches are rejected; lineage remains readable.
- **Match deleted or not found / Team scope unresolvable**: The request fails closed with not-found or forbidden and changes no state.
- **Grant misuse**: A grant cannot be used to list, read, delete, or write any location other than its session's single location, or to access another Match or Team.

## Requirements *(mandatory)*

### Functional Requirements

#### Authorization and scope

- **FR-001**: Every recording operation MUST target exactly one Match, and the system MUST resolve that Match's owning Team through the club hierarchy established by `specs/20261005-130701-club-identity-foundation` at the time of each request.
- **FR-002**: The system MUST permit the mutating recording operations (start upload, obtain upload grant, complete upload, revise timeline mapping, finalize recording set) only for an authenticated, active club member who currently is a Club Admin or currently holds the Coach role for the Match's Team.
- **FR-003**: The team-role authorization from `specs/20261005-130701-club-identity-foundation` MUST be extended so that the Coach role conveys recording-management authority for its own Team only; the Viewer role MUST convey no recording-management authority; a role for one Team MUST convey no authority for another Team's Matches.
- **FR-004**: The system MUST re-evaluate current authorization at every operation, including grant issuance, completion, and finalization of work started earlier and replays of earlier successful requests; earlier authorization or an issued grant MUST NOT substitute for current authority.
- **FR-005**: The system MUST fail closed: unauthenticated requests MUST be rejected as unauthenticated; a missing Match MUST yield not found; an unresolvable Team scope, membership, or role MUST yield forbidden; and no denied or failed request MUST create or change any recording state.
- **FR-006**: The system MUST reject mutating recording operations for a Match whose Team belongs to an archived Season, while still permitting authorized reads of its lineage.

#### Direct-to-storage upload

- **FR-007**: Authorized actors MUST be able to start an upload for a Match by declaring the recording's descriptive metadata, expected size in bytes, and algorithm-qualified content digest; the system MUST create a pending upload session bound to that Match and its Team and MUST NOT accept media bytes in this operation.
- **FR-008**: The system MUST provide, for a pending upload session, a time-limited upload grant that permits writing exactly one object at a location chosen by the platform for that session; the caller MUST NOT be able to choose the location, and the grant MUST NOT permit listing, reading, or deleting objects, writing any other location, or accessing any other Match or Team.
- **FR-009**: Authorized actors MUST be able to obtain a fresh grant for the same pending session (for example after expiry); the system MUST refuse grants for unknown, completed, or differently scoped sessions and MUST NOT issue broader or replacement authority in that case.
- **FR-010**: The platform service MUST NOT receive, relay, or proxy recording media bytes in any operation of this feature; media bytes MUST flow only between the client and object storage.
- **FR-011**: On upload completion the system MUST verify, using integrity evidence obtained from object storage rather than by relaying the media, that an object exists at the session's location and that its size and content digest equal the session's declaration; if storage cannot provide evidence sufficient for this verification, completion MUST be rejected.
- **FR-012**: A successful completion MUST, in one all-or-nothing step, create one new immutable recording version (recording its Match, Team, storage object reference, size, content digest, descriptive metadata, creating actor, and creation time), create one timeline mapping bound to that recording version (recording its mapping content and mapping digest), and mark the session completed; a session MUST be completed successfully at most once.
- **FR-013**: The system MUST reject a completion or mapping revision whose timeline mapping is structurally invalid or maps no media time to match time.
- **FR-014**: A rejected completion (verification mismatch, missing object, invalid mapping, storage unavailable, or denial) MUST create no recording version or timeline mapping and MUST leave a pending session pending.

#### Immutable lineage and corrections

- **FR-015**: Accepted recording versions and timeline mappings MUST NOT be modified, reassigned, or deleted by any operation of this feature; their identities, content, and digests MUST remain stable and addressable.
- **FR-016**: Authorized actors MUST be able to submit a revised timeline mapping for an existing accepted recording version of the same Match; the system MUST create a new timeline mapping with a new identity and digest bound to that recording version and leave all prior mappings unchanged. Submitting content identical to an existing mapping of that recording version MUST return the existing mapping rather than create a duplicate.
- **FR-017**: A timeline mapping MUST be bound to exactly one recording version; the system MUST reject any request that associates a mapping with a different recording version.
- **FR-018**: A correction to a recording's media or descriptive metadata MUST be made by a new upload that produces a new recording version; prior recording versions MUST remain unchanged and addressable.

#### Finalization and finalized-event evidence

- **FR-019**: Authorized actors MUST be able to finalize a Match's recording set by submitting a nonempty ordered list of recording-version and timeline-mapping pairs; a successful finalization MUST create a new immutable recording-set version for that Match and Team whose memberships preserve the submitted order.
- **FR-020**: The system MUST reject the entire finalization request, creating nothing, if the list is empty, if any recording version or timeline mapping does not exist or is not accepted, if any member belongs to another Match or Team, if any mapping is not bound to its paired recording version, or if the same recording version appears more than once.
- **FR-021**: Finalized recording-set versions and their memberships MUST NOT be modified, reordered, or deleted; a correction MUST be made by a new finalization producing a new recording-set version, and earlier versions MUST remain unchanged and addressable alongside it.
- **FR-022**: A successful finalization MUST commit the recording-set version, all of its ordered memberships, its successful retry outcome, and exactly one recordings-finalized event record together, such that either all are durably recorded or none are.
- **FR-023**: The recordings-finalized event record MUST contain an event identity, its event contract version, the recording-set-version identity, the Match and Team, the occurrence time, and the ordered membership lineage (recording-version identities, timeline-mapping identities, and their digests), and MUST describe the same frozen lineage returned to the caller.
- **FR-024**: The system MUST hold exactly one recordings-finalized event record per recording-set version, including under replayed and concurrent duplicate finalization requests.

#### Safe retries

- **FR-025**: Start upload, complete upload, revise timeline mapping, and finalize recording set MUST require a caller-supplied opaque retry key scoped to the operation and Match. Repeating a request with the same scoped key and the same request content MUST return the original outcome without repeating the change; reusing the key with different content MUST be rejected as a conflict; the same key in a different operation or Match MUST be evaluated independently.
- **FR-026**: Requests that fail validation, authorization, or storage verification MUST NOT record a successful retry outcome and MAY be retried after correction.

#### Lineage reads

- **FR-027**: Club Admins and members holding the Coach or Viewer role for the Match's Team MUST be able to list that Match's recording versions, timeline mappings, and finalized recording-set versions with ordered memberships and digests; this read MUST disclose metadata only and MUST NOT issue media access or upload grants.
- **FR-028**: The system MUST provide other platform capabilities (notably `specs/20261005-130703-durable-analysis-workflow`) a read-only lookup of a recording-set version that returns its Match, Team, ordered recording-version and timeline-mapping identities, and digests, so that they obtain frozen lineage through this operation rather than through object storage.

#### Security, governance, and API visibility

- **FR-029**: Upload grants, storage credentials, session secrets, and media bytes MUST NOT appear in durable records, retry outcomes, event records, audit records, logs, or telemetry.
- **FR-030**: The system MUST record minimized audit evidence (actor, Match, Team, action, outcome, correlation, time) for upload start, grant issuance, upload completion, timeline-mapping revision, finalization, and authorization denials, using the audit capability established by `specs/20261005-130701-club-identity-foundation`, and containing no secret values.
- **FR-031**: Every operation in this feature MUST be described in the platform's published, versioned, machine-readable API description, including its success, validation, unauthenticated, forbidden, not-found, conflict, and storage-unavailable outcomes; no segment, analysis-scheduling, storage-administration, or client-interface operation is introduced.
- **FR-032**: Recording state for this feature MUST be persisted on the durable storage foundation of `specs/20261005-130700-platform-persistence-foundation`, and changes to it MUST go through the operations defined here so that immutability, finalization, and audit rules always apply.

### Key Entities *(include if feature involves data)*

- **Match**: Existing Team-owned container defined by `specs/20261005-130701-club-identity-foundation`; this feature attaches recordings and recording sets to it and inherits its Team scope.
- **Upload Session**: A pending-then-completed record of one intended source-recording upload for a Match: its Team, declared descriptive metadata, expected size, declared content digest, platform-chosen storage location, initiating actor, and state. Workflow state, not accepted evidence.
- **Upload Grant**: A transient, time-limited authorization to write one object at one session's storage location. Never stored durably.
- **Recording Version**: An immutable accepted source-video version owned by one Match and Team, referencing its stored object, size, content digest, and descriptive metadata. Corrections produce new versions.
- **Timeline Mapping**: An immutable mapping of one recording version's media time to match time, with its own identity and digest. A recording version may have several mappings; each mapping belongs to exactly one recording version.
- **Recording Set Version**: An immutable, finalized, ordered selection of recording-version and timeline-mapping pairs for one Match and Team that constitutes the evidence for analysis. New versions coexist with earlier ones.
- **Recording Set Membership**: One ordered position within a recording-set version, binding an exact recording version and timeline mapping.
- **Retry Outcome**: The durable record binding a scoped retry key and request content to the original successful outcome of a mutating operation; contains no secrets.
- **Recordings-Finalized Event Record**: The single durable event evidence committed with each recording-set version, describing its frozen lineage for later delivery to consumers.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In acceptance testing, 100% of recording media bytes travel directly between the client and object storage, and the platform service receives 0 media bytes across all upload workflows.
- **SC-002**: An authorized Coach can take a Match from no recordings to a finalized recording set of at least three recordings using only start, complete, and finalize operations, with each platform operation (excluding the media transfer itself) acknowledged within 2 seconds in 95% of acceptance-test runs.
- **SC-003**: 100% of tested unauthenticated, cross-Team, Viewer-only, revoked-membership, archived-Season, and missing-Match attempts are denied, and each leaves 0 new or changed recording records.
- **SC-004**: 100% of completions whose stored object is missing or differs in size or digest from the declaration are rejected, and 0 such objects ever appear as accepted recording versions.
- **SC-005**: Across repeated reads, every finalized recording-set version returns identical members, order, and digests 100% of the time, including after later corrections and new set versions.
- **SC-006**: When 10 identical finalization requests with the same retry key are submitted concurrently, exactly 1 recording-set version and exactly 1 recordings-finalized event record result.
- **SC-007**: In 100% of injected failures during finalization, 0 partial results remain (no set version, membership, success outcome, or event record from the failed request).
- **SC-008**: 0 upload grants, storage credentials, or session secrets are found in durable records, retry outcomes, event records, audit records, logs, or telemetry captured during acceptance testing.
- **SC-009**: A downstream analysis capability can obtain the complete frozen lineage of any finalized recording-set version through the provided lookup in a single request, with 0 object-storage reads.

## Assumptions

- Actors are the architecture's roles: Club Admin (club-wide authority, inherits access to every Team) and Coach and Viewer (per-Team roles). Authenticated sessions, current club membership, Club Admin authority, the Club > Season > Team > Match hierarchy, Match-to-Team scoping, and the audit capability come from `specs/20261005-130701-club-identity-foundation`; Match creation is part of that feature, not this one.
- Durable storage, transactional all-or-nothing writes, optimistic concurrency, and disposable test infrastructure come from `specs/20261005-130700-platform-persistence-foundation`; this feature adds only recording records to the shared application data area.
- Each upload transfers one whole object per session; resumable or multi-part upload experiences are out of scope for this slice.
- The client computes and declares the content digest and size at upload start; the platform verifies them against storage-provided integrity evidence and never trusts the declaration alone.
- The exact timeline-mapping representation (beyond mapping media time to match time with an identity and digest) and the canonical form used to compute digests and compare retry-request content are decided during planning, consistent with the platform's digest and identifier conventions.
- Upload-grant lifetime, maximum upload size, accepted media formats, storage location layout, and storage credentials are configuration and policy inputs with no approved production defaults; tests use explicit ephemeral values.
- The recordings-finalized event is recorded durably for later delivery; its transport publication, delivery retries, consumer behavior, and cleanup of delivered records are out of scope and belong to later work.
- Out of scope and deferred exactly as the architecture leaves them: segment materialization and the Segment Service; analysis scheduling and analysis-run creation (`specs/20261005-130703-durable-analysis-workflow`); event transport publication; production object-storage selection, topology, encryption, and credentials; lifecycle-policy values (retention, deletion, holds, purge propagation, and cleanup of abandoned uploads under POL-003); media playback or download grants; and any client user interface.
- Successful development evidence for this feature does not constitute production approval; the production-blocking governance items for recordings (accountable owner, POL-003, THR-003 grant lifetime and detection, independent audit authority) remain unresolved.
- Architecture References: [match-data-pipeline.md](../../docs/architecture/match-data-pipeline.md), [terminology-and-principles.md](../../docs/architecture/terminology-and-principles.md), [security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md), [platform-implementation.md](../../docs/architecture/platform-implementation.md), [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md).
