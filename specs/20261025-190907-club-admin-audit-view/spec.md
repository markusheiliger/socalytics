# Feature Specification: Club Admin Security Audit Evidence View

**Feature Branch**: `20261025-190907-club-admin-audit-view`

**Created**: 2026-10-25

**Status**: Draft

**Input**: User description: "Give Club Admins a read-only, paged, filterable view of the append-protected security audit events the platform already records (authentication and authorization decisions, role changes, Manager registration and revocation, credential lifecycle, invitations, password recovery, evidence-media grants, agent catalog changes). A member operation under /api/v1 restricted to Club Admins lists events newest first with filters for time range, event type, outcome, actor account, resource type and identifier, and team, with a stable cursor and a typed OpenAPI schema; each read returns only the minimized stored fields; every read is itself recorded as a security audit event with the applied filter shape but no result content; denied reads for non-admins are audited and refused without revealing event counts. Includes a Club Admin Web UI screen (online only, memory-only data, manual refresh) tested by a new simulated browser project and a platform browser project, plus integration tests proving authorization, filtering, paging, minimization, and the self-audit. Out of scope: export, redaction, deletion, retention, integrity verification, holds, the production audit store, alerting, and any per-event-class access policy; POL-009 stays unresolved and Blocking, and the values here are development evidence only."

## User Scenarios & Testing *(mandatory)*

The actor is the Club Admin, the only role allowed to read audit evidence in this feature. The platform already records security audit events in an append-protected store ([Security And Data Governance → Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events); [Platform Implementation → Data Organization](../../docs/architecture/platform-implementation.md#data-organization)), but nobody can read them without direct database access. This feature gives Club Admins a controlled, audited, read-only view of that evidence and implements the architecture's requirement that "access to audit evidence ... MUST itself be audited" for this read path. It adds a connected area to the Web UI ([Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui)). The platform decides and checks everything; the Web UI shows what the platform returns and explains its refusals. Everything here is development evidence: the production audit store, independent audit authority, and event-class retention and integrity rules remain unresolved under POL-009 (Open / Blocking).

### User Story 1 - Review recent security events (Priority: P1)

A Club Admin lists the security audit events the platform recorded, newest first, one page at a time. Each event shows when it occurred, its event type, action, outcome, who acted (member account, system, or anonymous), the affected resource type and identifier, the team it concerns where present, the reason code where present, and its correlation identifier. The Club Admin pages forward without any event being skipped or repeated, even while new events are being recorded.

**Why this priority**: Reading the recorded evidence is the whole purpose of the feature and is useful without filters or the Web UI.

**Independent Test**: With a platform that has recorded events of several types (sign-ins, a role change, a Manager registration), a Club Admin lists events, receives them newest first with the stored fields, pages through all of them with the continuation position returned by the platform, and sees every event exactly once, including when further events are recorded between page reads.

**Acceptance Scenarios**:

1. **Given** recorded events and a signed-in Club Admin, **When** the Club Admin lists events, **Then** the platform returns the newest events first, one page at a time, each with only the minimized stored fields, and a continuation position when more events exist.
2. **Given** a continuation position, **When** the Club Admin requests the next page, **Then** the following older events are returned with no skipped or repeated event, even if newer events were recorded since the first page.
3. **Given** a continuation position that is malformed, undecodable, or bound to a different filter combination, **When** it is used, **Then** the platform refuses it with a specific problem and returns no events.
4. **Given** no events match, **When** the Club Admin lists events, **Then** an empty page is returned with no continuation position.

---

### User Story 2 - Narrow the evidence to a question (Priority: P1)

A Club Admin investigating something ("who changed this role?", "which Manager registration was revoked and by whom?", "what was denied for this team last night?") filters the events by time range, event type, outcome, actor account, resource type, resource identifier, and team, alone or combined, and pages through the matching events newest first.

**Why this priority**: Without filters an unbounded stream of events cannot answer a question; filtering is what makes the evidence usable.

**Independent Test**: Against seeded events varying in time, type, outcome, actor, resource, and team, each filter alone and in combination returns exactly the matching events, an invalid filter value is refused with a specific problem, and paging with a filter returns each matching event once.

**Acceptance Scenarios**:

1. **Given** events across several types, **When** the Club Admin filters by event type, **Then** only events of that type are returned.
2. **Given** events across several outcomes (`succeeded`, `unchanged`, `denied`, `failed`, `refused`), **When** the Club Admin filters by outcome, **Then** only events with that outcome are returned.
3. **Given** events in a time range, **When** the Club Admin sets a start, an end, or both, **Then** only events that occurred within the range (start inclusive, end exclusive) are returned; a range whose start is not before its end is refused.
4. **Given** events by several actors, **When** the Club Admin filters by actor account, **Then** only events of that account are returned.
5. **Given** events on several resources, **When** the Club Admin filters by resource type, or by resource type and resource identifier, **Then** only matching events are returned; a resource identifier without a resource type is refused.
6. **Given** events for several teams, **When** the Club Admin filters by team, **Then** only events that concern that team are returned.
7. **Given** several filters combined, **When** events are listed, **Then** only events matching all of them are returned.
8. **Given** a filter value the platform does not accept (for example an unknown outcome or a malformed identifier), **When** events are listed, **Then** the platform refuses the request with a specific problem identifying the filter.

---

### User Story 3 - Trust that the evidence view is itself accountable and safe (Priority: P1)

Every read of audit evidence is recorded as a security audit event, so a later reviewer can see who looked at the evidence, when, and with which filters, without seeing what the read returned. A member who is not a Club Admin is refused, the refusal is audited, and the refusal reveals nothing about the evidence (no counts, no hints that events exist). Events returned never contain credentials, tokens, links, or payload text.

**Why this priority**: The architecture requires access to audit evidence to be audited and the evidence to be minimized; without it the view would be a new leak path rather than a control.

**Independent Test**: A Club Admin lists events with a filter combination; the audit store then contains one new event of the audit-read type attributed to that Club Admin with the filter shape and no result content. A Coach, a member of another role, an unauthenticated caller, and a machine caller are refused; each refusal of an authenticated non-admin is recorded as a denied audit event, and the refusal is identical whatever the evidence holds. Seeded events carrying sensitive-looking stored details are returned without them.

**Acceptance Scenarios**:

1. **Given** a Club Admin lists events, **When** the platform answers a valid request, **Then** exactly one audit event of the audit-read type with outcome `succeeded` is recorded for that read, containing the acting account, the correlation identifier, and the shape of the applied filters (which filters were set, plus the values of the fixed-vocabulary filters: time range bounds, event type, outcome, and resource type), and not the events returned, their count, the continuation position, or the account, resource, and team identifiers supplied as filters.
2. **Given** a Club Admin lists events with filters the platform refuses, **When** the refusal is returned, **Then** an audit event with outcome `refused` is recorded the same way, with no result content.
3. **Given** a signed-in member who is not a Club Admin, **When** they request the list, **Then** the platform refuses with the same refusal as for any other protected Club Admin operation, records an audit event with outcome `denied` and the actor, and the response contains no event, no count, and no difference that depends on whether events exist or match.
4. **Given** an unauthenticated caller, or a caller with no member session, **When** they request the list, **Then** they are refused by the platform's ordinary authentication handling and no audit evidence is disclosed.
5. **Given** an event whose stored details hold fields beyond the minimized set, **When** it is listed, **Then** only the minimized fields are returned: identifiers, event type, action, outcome, actor kind and account, session reference, resource type and identifier, team, data class, reason code, correlation identifier, occurrence time, and a bounded, allow-listed set of non-sensitive detail values; credentials, tokens, links, question or advice text, prompts, payloads, and unmasked email addresses are never returned.
6. **Given** the audit-read event itself, **When** it is later listed by a Club Admin, **Then** it appears like any other event (a read's own event is recorded after its page is read, inside the same transaction, so it never appears in its own page but appears in later reads), so reading the audit trail of the audit reads is possible.
7. **Given** any read, **When** the audit event of the read cannot be recorded, **Then** the platform returns no events and answers with a recoverable server problem (a read is never served unaudited).

---

### User Story 4 - Use the evidence from the Web UI (Priority: P2)

A Club Admin opens an Audit entry in the Web UI navigation (shown only to Club Admins, with the other administration entries), sees the newest events, chooses filters, pages through the results, refreshes manually, and opens an event to read all its returned fields. The area is online only, keeps everything in memory, never refreshes by itself, and shows explicit loading, empty, error, and refused states.

**Why this priority**: The platform operation delivers the control; the screen makes it usable for Club Admins who do not call the API by hand. It follows the existing Club Admin operations area pattern.

**Independent Test**: Browser tests in a new simulated project (against a fake platform) and a new platform project (against the real platform with seeded events and a scripted sign-in) open the area as Club Admin, filter, page, refresh, and open an event, and verify a Coach sees neither the entry nor the area, no audit data is stored locally or cached by the service worker, and nothing refreshes without the Club Admin's action.

**Acceptance Scenarios**:

1. **Given** a signed-in Club Admin, **When** the navigation is shown, **Then** it contains the Audit entry; **Given** any other member, **Then** it does not, and opening an audit address shows the not-permitted view without requesting audit data.
2. **Given** the area, **When** it opens, **Then** it shows the newest page of events with time, type, action, outcome, actor, resource, and team, and states plainly that the view is read-only development evidence and that reading it is itself audited.
3. **Given** the area, **When** the Club Admin sets filters and applies them, **Then** only matching events are shown, the filters are part of the address, and a filter value the Web UI does not know is ignored rather than sent.
4. **Given** more events than a page, **When** the Club Admin moves to the next page and back, **Then** the platform's page boundaries are kept and no event is skipped or repeated.
5. **Given** the list, **When** the Club Admin chooses refresh, **Then** the first page for the current filters is read again, the earlier page stays visible until the new one arrives, and nothing is read again without that action.
6. **Given** an event, **When** the Club Admin opens it, **Then** every returned field is shown with absent values shown as absent, and identifiers can be read and copied.
7. **Given** the platform refuses with lack of permission, an invalid filter, or an unusable continuation position, is rate limiting reads, or is unavailable, **When** the answer arrives, **Then** the Web UI shows the not-permitted view, the filter problem next to the filter, a restart at the first page with an explanation, a retry after the stated time, or a recoverable error with a retry, respectively.
8. **Given** the browser is offline, **When** the area is opened or refreshed, **Then** the connection-required state is shown, nothing is queued, and no audit data is kept locally.
9. **Given** any audit view, dialog, or state, **When** automated accessibility checks run, **Then** they report no violations and every control is operable by keyboard.

---

### Edge Cases

- Many events share the exact same occurrence time: the order is still total and stable, and paging neither skips nor repeats any of them.
- Events are recorded between two page reads: the continuation position keeps the second page consistent with the first; newer events appear only after a refresh.
- A time range in the future yields an empty page, and an end not after the start is refused with a specific problem, never an unspecific error.
- A filter combination that matches nothing returns an empty page, indistinguishable in shape from an empty store.
- An actor account or team identifier that does not exist is accepted as a filter value if well-formed and matches nothing; the response never reveals whether the account or team exists.
- Events of anonymous or system actors (no actor account) are listed; filtering by actor account never matches them.
- A Club Admin whose role is removed between two page reads is refused on the next read, and that refusal is audited.
- The Club Admin role is club-wide: a Club Admin sees events of all teams and events with no team; team scope grants no access in this feature.
- Platform-provided text (reason codes, identifiers, correlation identifiers) containing markup or very long text is shown as plain text within bounded space and never interpreted.
- A session that ends while the area is open follows the Web UI's existing signed-out handling.
- Large histories: a page request stays bounded by a maximum page size, and a request for a larger page is refused with a specific problem, never an unbounded read.
- Rapid repeated reads by one Club Admin are each audited; a rate limit protects the operation from abuse, and a limited request is refused with a specific problem and is itself audited as refused.

## Requirements *(mandatory)*

### Functional Requirements

#### Authorization and access

- **FR-001**: The platform MUST offer one member operation under `/api/v1` that lists security audit events, available only to active members with the Club Admin role; all other callers MUST be refused.
- **FR-002**: The operation MUST NOT be granted through team scope or any other role until a dedicated audit-reviewer or stamp-operator role exists, and this feature MUST NOT add a per-event-class access policy.
- **FR-003**: A refusal for lack of permission MUST contain no event, count, or detail that depends on the stored evidence, and MUST be identical whether events exist or not.
- **FR-004**: The operation MUST be read-only: it MUST NOT create, change, redact, delete, export, or hold any audit event, and the append-protected store MUST stay unchanged by it apart from the audit event recording the read itself.

#### Listing, order, and paging

- **FR-005**: The operation MUST return events newest first with a total, stable order that does not depend on the filters, such that paging never skips or repeats an event, including events with identical occurrence times.
- **FR-006**: The operation MUST page with an opaque continuation position that stays valid while new events are recorded, is bound to its filter combination, and is refused with a specific problem when malformed, undecodable, or bound to a different filter combination.
- **FR-007**: The page size MUST have a documented default and a documented maximum (development values), and requests above the maximum MUST be refused with a specific problem.
- **FR-008**: The operation MUST NOT return a total count of events or of matches.

#### Filters

- **FR-009**: The operation MUST support these filters, alone or combined, each narrowing the result: time range (start inclusive, end exclusive, either optional), event type, outcome, actor account, resource type, resource identifier, and team.
- **FR-010**: The outcome filter MUST accept only the outcomes the platform records (`succeeded`, `unchanged`, `denied`, `failed`, `refused`); the event type and resource type filters MUST accept only well-formed values; a resource identifier filter MUST require a resource type; account and team filters MUST accept only well-formed identifiers. Any other value MUST be refused with a specific problem naming the filter.
- **FR-011**: A time range whose start is not before its end MUST be refused with a specific problem.
- **FR-012**: Filtering MUST NOT reveal whether an account, team, or resource exists; a well-formed value that matches nothing MUST return an empty page.

#### Minimization

- **FR-013**: Each returned event MUST contain only minimized stored fields: event identifier, occurrence time, event type, action, outcome, actor kind, actor account identifier, session reference, resource type, resource identifier, team identifier, data class, reason code, correlation identifier, and an allow-listed, bounded set of non-sensitive detail values.
- **FR-014**: The operation MUST NOT return credentials, tokens, private keys, links (including presigned links), prompts, question, advice, or rationale text, payload content, fact payloads, or unmasked email addresses; where an event refers to an address it MUST be returned only in its stored masked form.
- **FR-015**: Detail values that are not on the allow-list MUST be omitted rather than returned; a stored value that would break the minimization rules MUST NOT be returned even if it is on the allow-list.

#### Self-audit

- **FR-016**: Every request to the operation by an authenticated member MUST be recorded as a security audit event of a dedicated audit-read event type, before any event content is returned, with the acting account, session reference, correlation identifier, outcome, and the shape of the applied filters.
- **FR-017**: The recorded filter shape MUST name which filters were set and MAY include the values of the fixed-vocabulary filters (time range bounds, event type, outcome, and resource type) but MUST NOT include the returned events, their number, the continuation position, or the account, resource, and team identifiers supplied as filters.
- **FR-018**: A successful read MUST be recorded with outcome `succeeded`, a read refused for lack of permission with outcome `denied` and a denial reason code, and a read refused for invalid input or rate limiting with outcome `refused` and a specific reason code. A refused read records the filter names plus only the valid fixed-vocabulary values; when the request could not be parsed (as for rate limiting), it records no filter shape beyond the names that could be read.
- **FR-019**: If the audit event of a read cannot be recorded, the platform MUST NOT return events and MUST answer with a recoverable server problem.
- **FR-020**: The audit-read events MUST themselves be listable and filterable by event type like any other audit event.

#### Platform contract and protection

- **FR-021**: The operation MUST be described in the platform's published API description with typed schemas for its parameters, events, pages, and problems, and the committed API snapshot used by the Web UI MUST include it.
- **FR-022**: The operation MUST apply the platform's existing member API rules (active member session, anti-forgery handling, problem responses, and request correlation) and MUST be protected by a per-member rate limit whose values are development values.
- **FR-023**: Audit data MUST NOT be cached by shared or private caches and MUST NOT be written to application logs or telemetry by the operation, other than identifiers and reason codes the platform already logs for any request.

#### Web UI

- **FR-024**: The Web UI MUST offer an Audit area with its own navigation entry shown only to Club Admins, placed with the other Club Admin administration entries; every view MUST be addressable, and opening it as another member MUST show the not-permitted view without requesting audit data.
- **FR-025**: The Web UI MUST list events newest first one page at a time with next and previous paging that keeps the platform's page boundaries, offer all supported filters, keep the applied filters in the address, and ignore unknown filter values from the address.
- **FR-026**: The Web UI MUST offer a manual refresh that re-reads the first page for the current filters, MUST keep earlier values visible while it reads, and MUST NOT refresh automatically.
- **FR-027**: The Web UI MUST offer an event detail view showing every returned field, absent values as absent, identifiers readable and copyable, and platform-provided text as plain text only.
- **FR-028**: The Web UI MUST show explicit loading, empty, error, refusal, invalid-filter, rate-limited retry, and connection-required states, and MUST state that the view is read-only and that reading it is audited.
- **FR-029**: The Web UI MUST be online only: it MUST keep audit data in memory only, MUST NOT store it in browser storage or service-worker caches, and MUST NOT queue reads for later.
- **FR-030**: The Web UI MUST use the existing navigation, translation, accessibility, and problem-handling rules, and MUST reach the platform only through application-owned adapters over the generated platform client.
- **FR-031**: The Web UI MUST NOT offer export, redaction, deletion, retention, integrity verification, hold, alerting, or automatic refresh controls.

#### Verification

- **FR-032**: Integration tests MUST prove authorization (Club Admin allowed; Coach, other roles, unauthenticated, and machine callers refused), filtering (each filter and combinations, and refusals of invalid values), paging (order, stability under concurrent recording, tie order, bad continuation positions), minimization (sensitive stored values never returned), and the self-audit (recorded for success, denial, and refusal, with no result content, and no serving when the audit write fails).
- **FR-033**: Browser tests MUST run as new specs in the existing `simulated` project against a fake platform and in a new `platform-audit` project against the real platform, covering access, filters, paging, refresh, detail, states, offline behavior, no local audit data, and accessibility.

### Key Entities *(include if feature involves data)*

- **Security Audit Event**: The platform's append-protected record of one security or lifecycle decision: when it occurred, event type, action, outcome, who acted, the affected resource, the team concerned, data class, reason code, correlation identifier, and stored details. Existing; this feature only reads it.
- **Audit Event View**: The minimized projection of a Security Audit Event that a Club Admin receives; only the allow-listed fields.
- **Audit Query**: The set of filters and the continuation position of one read: time range, event type, outcome, actor account, resource type and identifier, and team.
- **Audit Read Event**: The Security Audit Event recorded for every read of audit evidence, holding the actor, outcome, and the filter shape but no result content.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Club Admin finds out who changed a given role or revoked a given Manager, using the Web UI filters, in at most 3 navigation steps from the Web UI's home view and without any raw API call or database access.
- **SC-002**: In automated tests, paging through any seeded history of events returns every matching event exactly once in newest-first order in 100% of runs, including events with identical times and events recorded in occurrence order between page reads.
- **SC-003**: In automated tests, every non-Club Admin caller is refused in 100% of cases, and the refusal discloses nothing about the evidence whether the store is empty or full.
- **SC-004**: In automated tests, 100% of reads by authenticated members (successful, denied, and refused) leave exactly one `audit.evidence-read` event (other events, such as the `authorization.denied` event the shared authorizer records for a denial, are not counted) that holds no returned event, count, continuation position, or filter identifier values.
- **SC-005**: In automated tests, none of the planted sensitive values (credential, token, link, free text, unmasked address) appears in any response in 100% of covered scenarios.
- **SC-006**: In automated tests, the audit table is unchanged by reads apart from the self-audit events in 100% of cases.
- **SC-007**: Automated accessibility checks report zero violations for every audit view, dialog, and state, every control is reachable by keyboard, and no audit response is found in browser storage or service-worker caches after a session that used the area.
- **SC-008**: In automated tests on a development stamp with 100,000 recorded events, the first page of events is returned to a Club Admin within 2 seconds.

## Assumptions

- Paging stability is guaranteed for events committed in occurrence order; an event that commits late with an older timestamp can be missed by later pages until the Club Admin refreshes.
- The Club Admin role stays the only role allowed to read audit evidence until a dedicated audit-reviewer or stamp-operator role and an independent audit authority exist; this is not the independent audit authority required by `GOV-AUD-001` and does not resolve it.
- POL-009 (event-class retention and integrity-verification periods) stays Open and Blocking. All limits here (default and maximum page size, rate limit) are development values, and this feature makes no claim about production readiness.
- Out of scope: export, redaction, deletion, retention enforcement, integrity verification, holds, the production audit store, alerting, per-event-class access policy, dashboards, charts, event counts and statistics, and reading audit evidence of other stamps.
- The recorded events and their fields already exist; this feature adds no new event kinds other than the audit-read event type and changes no existing event. Events that predate the feature are listed with whatever minimized fields they hold.
- Which stored detail values are on the allow-list is decided at planning time from the existing event types, defaulting to omission when unsure.
- The audit-read event type uses the data class of the existing audit events and is recorded in the same store with the same append-protection.
- The Web UI follows the pattern of the Club Admin operations area ([Web UI Workflow Operations](../archive/20261017-215112-web-ui-workflow-operations/spec.md)): connected, online only, memory-only data, and manual refresh.
- Dependencies, all merged (archived under `specs/archive/`): [Club Identity Foundation](../archive/20261005-130701-club-identity-foundation/spec.md) (audit table and roles), [Web UI Foundation](../archive/20261012-150657-web-ui-foundation/spec.md), and [Web UI Workflow Operations](../archive/20261017-215112-web-ui-workflow-operations/spec.md).
- Implements the audit events section of [Security And Data Governance](../../docs/architecture/security-and-data-governance.md#audit-events), the append-protected audit table description of [Platform Implementation → Data Organization](../../docs/architecture/platform-implementation.md#data-organization), and the Club Admin administration part of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui).
