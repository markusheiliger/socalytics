# Feature Specification: Web UI Analyst Manager Fleet and Registration Approval

**Feature Branch**: `20261013-122132-web-ui-manager-fleet`

**Created**: 2026-10-13

**Status**: Draft

**Input**: User description: "Web UI Analyst Manager Fleet and Registration Approval: a further Web UI slice in src/clients/web/, building on Web UI Foundation and the fleet read API. (1) A pairing page at the stamp's verification path /analyst-managers/pair, the URL the Manager shows: a signed-in Registrar or Club Admin enters the pairing code and the device fingerprint the Manager displays, checks the host label, and submits; a wrong code or fingerprint gets one generic refusal; signed-out members are sent to sign-in and back; members without the Registrar capability see not-permitted. (2) Registrars see the registration requests they submitted with state and expiry times. (3) Club Admins see the fleet: every registration with host label, state, claimed key protection, connectivity, operating state (running, paused with resume time, runtime unavailable with reason, draining), active and maximum Analysts, Manager version, and last report time; filter by state; refresh while open; a detail view with fingerprint, operating system, pairing time and network origin; approve or reject pending requests (rejection with a reason) with the warning to approve only Managers they can physically identify and a warning for software-backed keys; revoke active registrations with a reason and confirmation. Uses the existing registration operations and the fleet read API through the generated client. Local development: the stamp's public base URI, which builds the verification URL, points to the Web UI origin, set by the AppHost. Out of scope: host-local actions (pause, resume, exit, unregister, concurrency, updates), queue depth and utilization, status history, key attestation, external OIDC sign-in, Manager-side changes, and the claimed application profile (tray or headless), which no component records yet and which follows once the headless profile exists. Depends on Web UI Foundation, Analyst Manager Status Report and Fleet Read API, and Analyst Manager Registration. Implements docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui and #browser-pairing-and-approval and docs/architecture/client-applications.md#web-ui (the fleet view)."

## Clarifications

### Session 2026-10-13

- Q: How does a Club Admin confirm that they identified the Manager before approving? → A: They re-enter the last 6 characters of the fingerprint shown on the Manager's screen (ignoring separators and letter case); approval is enabled only when they match the request's fingerprint.
- Q: Which registrations does the fleet show when it opens? → A: The current ones (pairing, pending approval, approved, active, unregistering), pending requests first; filters show one state, the ended ones (rejected, expired, revoked, unregistered), or all.
- Q: How do Club Admins notice that a registration request waits for their decision? → A: The fleet's navigation entry shows the number of pending requests, refreshed periodically while the Club Admin is signed in. Email notification of Club Admins is wanted but out of scope until the architecture adopts an email delivery channel; it is a follow-up.
- Q: How often do an open fleet view and the navigation's pending count refresh? → A: Every 30 seconds while the fleet or a registration is visible; every 60 seconds for the pending count while the Club Admin is signed in.
- Q: What does a Club Admin see and do to confirm revoking an active Manager? → A: The confirmation shows the host label and active Analysts, states that running work is stopped and retried elsewhere where attempts remain and that the Manager must be registered again, and requires typing the host label (the last 6 fingerprint characters when there is no host label).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Pair a new Analyst Manager in the browser (Priority: P1)

A host operator starts registration on an Analyst Manager. The Manager shows a verification address, a short pairing code, a device fingerprint, and the host label the operator entered. A Registrar (or a Club Admin) opens the address in a browser, signs in if needed, and enters the pairing code and the fingerprint read from the Manager. The Web UI submits them, then shows the host label and claimed key protection of the paired Manager and confirms that the request now waits for a Club Admin's decision.

**Why this priority**: Without the pairing page, the verification address every Manager shows leads nowhere, and registration can only be completed through the API. This story alone makes Manager registration usable for Registrars.

**Independent Test**: Start a pairing from a test Manager, open the verification address, sign in as a Registrar, submit the code and fingerprint, and observe the pending request with its host label; repeat with a wrong fingerprint and with an expired code and observe the same generic refusal.

**Acceptance Scenarios**:

1. **Given** a signed-in Registrar or Club Admin and a valid pairing code with its matching fingerprint, **When** they submit both on the pairing page, **Then** the Web UI shows that the request is pending approval, with the host label, claimed key protection, operating system, and fingerprint the platform returned, and how long the request remains open for approval.
2. **Given** a wrong, expired, or already used pairing code, or a fingerprint that does not match, **When** the member submits, **Then** the Web UI shows one generic refusal that does not reveal which part was wrong or whether a request exists, and lets the member try again.
3. **Given** a signed-out visitor, **When** they open the verification address, **Then** the Web UI asks them to sign in and returns them to the pairing page afterwards.
4. **Given** a signed-in member with neither the Registrar nor the Club Admin role, **When** they open the pairing page, **Then** the Web UI shows the not-permitted view and submits nothing.
5. **Given** the platform's rate limit for code submissions is reached, **When** the member submits again, **Then** the Web UI shows that too many attempts were made and when to try again.
6. **Given** the host label shown on the Manager, **When** the Web UI shows the submitted request, **Then** it shows the same host label and fingerprint so the member can compare them with the Manager's screen before leaving.

---

### User Story 2 - See my registration requests (Priority: P2)

A Registrar who paired Managers wants to know whether a Club Admin approved them. The Web UI lists the requests the Registrar submitted, with host label, fingerprint, state, submission and decision times, the rejection reason, and the times until which the request can still be approved or activated.

**Why this priority**: Registrars otherwise cannot follow up on their requests. It needs submitted requests (Story 1) and shows only what the fleet read API exposes to Registrars.

**Independent Test**: As a Registrar who submitted two requests, open the request list and see exactly those two with their states; after a Club Admin rejects one, see the rejection and its reason; as another Registrar, see none of them.

**Acceptance Scenarios**:

1. **Given** a Registrar without the Club Admin role, **When** they open their registration requests, **Then** the Web UI lists exactly the requests they submitted with host label, fingerprint, state, submission time, decision time, rejection reason, and approval or activation deadline, and no operating state or connectivity.
2. **Given** a request of another member, **When** the Registrar opens its address directly, **Then** the Web UI shows that it was not found, as the platform answers.
3. **Given** a request that changes state while the list is open, **When** the list refreshes, **Then** the new state is shown.

---

### User Story 3 - Club Admins approve or reject pending requests (Priority: P3)

A Club Admin sees the requests that wait for approval, opens one, and compares the fingerprint, host label, claimed key protection, operating system, and the time and network origin of the pairing request with what they know about the machine. The Web UI warns them to approve only Managers they can physically identify, and adds a stronger warning when the Manager claims a software-backed key. They approve the request, or reject it with a reason.

**Why this priority**: Approval is the second authorization step without which no Manager can activate. It needs pending requests (Story 1).

**Independent Test**: As a Club Admin, open a pending request, see the warnings and the pairing origin, approve it, and observe the approved state; reject another request with a reason and observe the rejected state and reason; let an approval window expire and observe that approval is refused.

**Acceptance Scenarios**:

1. **Given** a pending request, **When** a Club Admin opens it, **Then** the Web UI shows the fingerprint, host label, claimed key protection, operating system and version, Manager version, the time and network origin of the pairing request, the submitting member, the approval deadline, and the platform's approval notice, with the warning to approve only Managers they can physically identify.
2. **Given** a pending request whose Manager claims a software-backed key, **When** a Club Admin opens it, **Then** the Web UI shows an additional warning that the key is not hardware-protected and that the platform cannot verify any claimed protection.
3. **Given** a pending request, **When** the Club Admin re-enters the last 6 characters of the fingerprint shown on the Manager's screen and they match the request's fingerprint, **Then** approval is enabled, and after approving the request is shown as approved, waiting for the Manager to activate; **and When** the characters do not match, **Then** approval stays disabled and the Web UI says that the fingerprint does not match this request.
4. **Given** a pending request, **When** the Club Admin rejects it with a reason, **Then** the request is shown as rejected with that reason; rejecting without a reason is not possible.
5. **Given** a request that was decided, expired, or changed by someone else since it was loaded, **When** the Club Admin approves or rejects it, **Then** the Web UI shows the platform's refusal, changes nothing, and shows the current state.
6. **Given** a Club Admin who initiated the request themselves, **When** they approve it, **Then** the approval succeeds as a separate action, as the platform allows.
7. **Given** a Club Admin on any view, **When** registration requests are pending approval, **Then** the fleet's navigation entry shows their number, which updates periodically and disappears when none is pending.

---

### User Story 4 - Club Admins watch the fleet and revoke Managers (Priority: P4)

A Club Admin opens the fleet and sees every Analyst Manager of the club: its host label, registration state, claimed key protection, whether it is online or offline with the time of its last report, its operating state (running, paused with the resume time, runtime unavailable with the reason, draining), its active and maximum Analysts, and its version. They filter by state, for example to see only active or pending Managers. The view refreshes while open. When a machine is lost or no longer trusted, the Club Admin revokes its registration with a reason after confirming.

**Why this priority**: Fleet visibility tells Club Admins whether analysis capacity is available and which machines need attention; revocation is the only fleet action. It needs the fleet read API and is useful once Managers are registered (Stories 1 and 3).

**Independent Test**: With an active online Manager, an active Manager that stopped reporting, a paused Manager, and a pending request, open the fleet as a Club Admin and compare each row with the reported state; revoke one Manager with a reason and observe the revoked state; sign in as a Registrar and observe that the fleet is not available.

**Acceptance Scenarios**:

1. **Given** registrations in several states, **When** a Club Admin opens the fleet, **Then** the Web UI lists the current registrations (pairing, pending approval, approved, active, unregistering), pending requests first, each with host label, state, claimed key protection, connectivity (online, offline, or not applicable) with the time of the last report, operating state with resume time or reason, active and maximum Analysts, and Manager version, in a stable order with paging; ended registrations (rejected, expired, revoked, unregistered) are not listed by default.
2. **Given** the fleet, **When** the Club Admin chooses a filter (one registration state, "ended", or "all"), **Then** exactly the matching registrations are listed.
3. **Given** the fleet is open, **When** a Manager reports a new operating state or stops reporting, **Then** the change is shown at the next refresh without a page reload.
4. **Given** an active registration, **When** the Club Admin revokes it with a reason after reading the consequences (running work stopped and retried where attempts remain, re-registration required) and typing its host label (or the last 6 fingerprint characters when it has no host label), **Then** it is shown as revoked and its operating state and connectivity are no longer shown; revoking without a reason or with a mistyped confirmation is not possible.
5. **Given** a Registrar without the Club Admin role or a member with neither role, **When** they open the fleet's address, **Then** the Web UI shows the not-permitted view; Registrars are pointed to their own requests.
6. **Given** a Manager whose status report contains a resume time, **When** it is shown, **Then** the time appears in the member's local time with its date.

### Edge Cases

- **Pairing code expires while the page is open**: Submitting an expired code gives the generic refusal; the page explains that the Manager can start a new pairing.
- **Same code submitted twice**: The second submission gets the generic refusal; the first request is unaffected.
- **Approval window expires while the Club Admin decides**: The decision is refused and the request is shown as expired.
- **Request activated or revoked by someone else meanwhile**: Actions are refused as conflicts and the current state is shown.
- **Manager that never reported**: Connectivity is shown as offline or not applicable as the platform reports, with "no report yet" instead of a time.
- **Unknown values from a newer platform**: An operating state or key protection the Web UI does not know is shown as "unknown" rather than failing the view.
- **Long host labels**: Labels up to the platform's maximum length are shown in full in the detail view and shortened with a tooltip in lists.
- **Club Admin also Registrar**: Sees the full fleet; their own submissions are marked as such.
- **Platform unreachable**: Approve, reject, and revoke are disabled; refreshing resumes when the platform is reachable, as in the foundation.

## Requirements *(mandatory)*

### Functional Requirements

#### Pairing

- **FR-001**: The Web UI MUST serve a pairing page at the stamp's verification path (`/analyst-managers/pair`), so the verification address every Manager shows opens it.
- **FR-002**: The pairing page MUST be available to signed-in members with the Registrar or Club Admin role; signed-out visitors MUST be sent to sign-in and returned to the pairing page; other members MUST see the not-permitted view.
- **FR-003**: Members MUST be able to submit the pairing code and the device fingerprint read from the Manager; on success the Web UI MUST show the resulting request's state, host label, claimed key protection, operating system, fingerprint, and approval deadline so the member can compare them with the Manager's screen.
- **FR-004**: Every refused submission (unknown, expired, or used code; mismatching fingerprint) MUST be shown with one generic message that reveals neither which input was wrong nor whether a request exists; a rate-limit refusal MUST be shown as such with the time to wait when the platform provides it.
- **FR-005**: The pairing code and fingerprint MUST be entered by the member, MUST never be placed in an address, and MUST be cleared from the form after submission.

#### Registrar view

- **FR-006**: Registrars MUST be able to list the requests they submitted with host label, fingerprint, state, submission time, decision time, rejection reason, and approval or activation deadline, and open each one; the Web UI MUST show no operating state or connectivity to them.

#### Approval

- **FR-007**: Club Admins MUST be able to open any registration and see its fingerprint, host label, claimed key protection, operating system and version, architecture, Manager version, pairing request time and network origin, submitting member, decision details, deadlines, and the platform's approval notice.
- **FR-008**: The decision view MUST warn the Club Admin to approve only Managers they can physically identify and MUST show an additional warning for a claimed software-backed key; it MUST state that key protection is claimed by the Manager and not verified.
- **FR-009**: Club Admins MUST be able to approve a pending request only after re-entering the last 6 characters of the fingerprint shown on the Manager's screen (separators and letter case ignored) and only when they match the request's fingerprint, and MUST be able to reject a pending request with a required reason; the Web UI MUST show the platform's refusal for a decided, expired, or concurrently changed request and then show the current state.

#### Fleet

- **FR-010**: Club Admins MUST be able to list the club's registrations with host label, registration state, claimed key protection, connectivity with the time of the last report, operating state with its resume time or reason, active and maximum Analysts, and Manager version, using the platform's paging. The fleet MUST open on the current registrations (pairing, pending approval, approved, active, unregistering) with pending requests first, and MUST offer filters for one registration state, for the ended registrations (rejected, expired, revoked, unregistered), and for all.
- **FR-011**: The fleet list and an open registration MUST refresh every 30 seconds while visible, and the pending count in the navigation every 60 seconds while the Club Admin is signed in, so connectivity, operating state, and pending requests follow the platform without a page reload.
- **FR-012**: Club Admins MUST be able to revoke an active registration with a required reason. The confirmation MUST show the host label and the active Analysts, state that revocation cannot be undone, that the Manager's running work is stopped and, where attempts remain, retried by another Manager, and that the Manager must be registered again, and MUST require typing the host label (or, for a Manager without a host label, the last 6 characters of its fingerprint) before revoking. The Web UI MUST show the platform's refusal when the registration is no longer active.
- **FR-013**: The fleet MUST offer no other actions on Managers; host-local actions (pause, timed pause, resume, safe exit, unregister, autostart, updates, local concurrency) MUST NOT appear.
- **FR-014**: Values the Web UI does not recognize (operating states, key protections, registration states) MUST be shown as unknown without failing the view.

#### Navigation, platform access, and quality

- **FR-015**: Navigation MUST offer the fleet to Club Admins and "My registration requests" to Registrars without the Club Admin role, following the foundation's role-based navigation. For Club Admins, the fleet's navigation entry MUST show the number of pending requests, refreshed periodically while they are signed in, and no number when there is none.
- **FR-016**: The Web UI MUST access the platform only through the generated transport client behind application-owned adapters, as the foundation established, and MUST show the platform's refusals rather than enforcing its own registration rules.
- **FR-017**: In local development through the Aspire AppHost, the verification address the platform gives to Managers MUST open the Web UI's pairing page.
- **FR-018**: All new user-facing text MUST come from translation keys with English as the only shipped language, and all new views MUST be operable by keyboard with visible focus and pass automated WCAG 2.2 AA rule checks, as the foundation requires.

### Key Entities

- **Registration (client view)**: A Manager's registration as the platform exposes it: identity, state (pairing, pending approval, approved, active, unregistering, unregistered, rejected, expired, revoked), fingerprint, device metadata (operating system and version, architecture, Manager version, claimed key protection, host label), pairing origin (network address and time), submitter, decision and revocation details, deadlines, and version.
- **Status report (client view)**: The latest operating state of an active Manager (running, paused with resume time, runtime unavailable with reason, draining), its active and maximum Analysts, its version, and the time of the report; shown only to Club Admins.
- **Connectivity**: Online, offline, or not applicable, as the platform derives it from missed reports.
- **Pairing submission**: The pairing code and device fingerprint a member enters; never stored by the Web UI.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Registrar can go from opening the verification address shown by a Manager to a submitted pending request in under 2 minutes, including sign-in.
- **SC-002**: A Club Admin can find a pending request and approve or reject it in under 1 minute from opening the fleet.
- **SC-003**: 100% of the acceptance scenarios of this specification are covered by automated unit or browser tests that run in the Web UI verification environment, and an end-to-end set against the real platform (pair a test Manager through the pairing page, approve it, see it active in the fleet with its reported operating state, revoke it) passes on every merge.
- **SC-004**: In 100% of browser tests, refused pairing submissions show the identical message regardless of the cause.
- **SC-005**: In 100% of browser tests for each role, Club Admins reach the fleet and decision views, Registrars reach only their own requests and the pairing page, and other members reach neither.
- **SC-006**: A change in a Manager's reported operating state or connectivity is visible in an open fleet view within 30 seconds plus the platform's reporting interval.
- **SC-007**: 0 pairing codes or fingerprints appear in addresses, the browser history, or browser storage in the browser tests.
- **SC-008**: 0 Content Security Policy violations are reported in the browser tests, and automated WCAG 2.2 AA rule checks report 0 violations on every new view.

## Assumptions

- **Actors**: Registrars (club role) initiate pairing; Club Admins inherit the Registrar capability and decide and revoke, as the architecture states. Coaches and Viewers without these roles have no access to this feature.
- **API coverage**: The registration operations (submit pairing code, list, read, approve, reject, revoke) exist from Analyst Manager Registration, and the status and connectivity fields come from Analyst Manager Status Report and Fleet Read API. This feature changes no behavior of these operations. It needs two minimal platform changes: the operations declare typed request and response schemas in the platform's API description (the same JSON as today), so the generated client can use them; and an optional setting for the base address of the verification address (see below).
- **Verification address in local development**: The platform builds the verification address from the stamp's public base address and the verification path. The stamp's public base address must stay the API's secure address, because Managers use it for all their calls and refuse insecure addresses, while the Web UI is served on its own origin in local development. An optional verification base address therefore lets only the verification address point to the Web UI; the AppHost sets it to the Web UI origin. A stamp served from one origin leaves it unset. Production addresses remain deployment values and out of scope.
- **Refresh**: The fleet and an open registration refresh every 30 seconds while visible and the navigation's pending count every 60 seconds (clarification); there is no push channel in this slice.
- **Claimed application profile**: The architecture's fleet and approval views also show the Manager's claimed application profile (tray or headless). No component records it yet, and only the tray profile exists; it is shown once the headless profile is implemented and the Manager reports it.
- **Browser tests and the platform**: Most browser tests run against a simulated API. The end-to-end set runs against the real platform through the AppHost and drives pairing and activation with the platform's existing test Manager helper or an equivalent scripted Manager, so no Analyst Manager installation is needed; the plan decides how.
- **Browsers and devices**: Automated tests use Chromium; the supported-browser matrix starts with Chromium-based browsers on Windows. Pairing on a phone (for example from a QR code) is usable but not optimized.
- **Language and accessibility**: English only through translation keys, keyboard operation, and WCAG 2.2 AA rule checks, as in the foundation.
- **Out of scope**: host-local Manager actions (pairing on the Manager itself, pause, timed pause, resume, safe exit, unregister, autostart, updates, local concurrency); queue depth and host utilization; status history; key attestation; the claimed application profile; email or other notifications outside the Web UI (wanted for Club Admins, but pending an architecture decision on an email delivery channel; a follow-up); a verification address that carries the pairing code (for example from a QR code), which no Manager offers yet; external OpenID Connect sign-in; any Manager-side change; production addresses and deployment; browsers other than Chromium in automated tests.
- **Evidence**: Passing validation is development evidence only and does not establish deployment support or production readiness.
- **Dependencies**: `specs/20261012-150657-web-ui-foundation` (Web UI Foundation) provides the Web UI project, session, navigation, generated client, security baseline, and browser-test setup; it depends on `specs/archive/20261012-145505-web-ui-environment`. `specs/archive/20261012-153308-manager-fleet-status` (Analyst Manager Status Report and Fleet Read API) provides the status and connectivity fields and the Registrar-scoped read. `specs/archive/20261005-130704-analyst-manager-registration` provides pairing, approval, rejection, revocation, and the test Manager helper.
- **Architecture References**: [docs/architecture/analyst-manager.md](../../docs/architecture/analyst-manager.md) (Registration And Stamp Binding; Browser Pairing And Approval; Fleet Visibility In The Web UI), [docs/architecture/client-applications.md](../../docs/architecture/client-applications.md) (Web UI), [docs/architecture/security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md) (credential class entries, Web UI security baseline).
