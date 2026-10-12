# Feature Specification: Web UI Foundation

**Feature Branch**: `20261012-150657-web-ui-foundation`

**Created**: 2026-10-12

**Status**: Draft

**Input**: User description: "Web UI Foundation: the first slice of the one Web UI, an npm project at src/clients/web/ (React and TypeScript with Vite, React Router, TanStack Query, MUI under a SocAlytics theme) that provides the scripts the Web UI verification environment requires (build, test, test:e2e, @playwright/test 1.63.0) and talks to the platform only through a Kiota-generated TypeScript client, generated from the platform's OpenAPI document into ignored output and kept behind application-owned adapters and repositories, with explicit connectivity state for the online-only first release. Members sign in with a local account through the existing BFF session (cookie plus X-CSRF-Token on state-changing requests), sign out, are returned to sign-in when the session expires or is ended, change the initial password when required, set a password from a single-use credential, and change their own password; navigation and actions follow their club and team roles. Club Admins manage the club details, seasons (create, activate, archive), teams (create, edit), and members (create and issue set-password credentials, deactivate and reactivate, unlock, end their sessions, assign and remove club and team roles). The Web UI is served from one origin with the API in local development through the Aspire AppHost and follows the client security baseline (strict Content Security Policy with Trusted Types, no third-party scripts, Subresource Integrity). Out of scope: the service worker, PWA install, and offline mode; matches, recording upload, and analysis review; agent conversations; the Analyst Manager fleet view and registration approval; external OpenID Connect sign-in and MFA; the production container image and deployment; browsers other than Chromium in automated tests."

## Clarifications

### Session 2026-10-12

- Q: How should a Club Admin hand a new member their single-use set-password credential? → A: The Web UI shows the credential once as a code; the member opens the Web UI's set-password page and enters their sign-in name and the code. The credential never appears in an address.
- Q: What should the Web UI browser tests run against? → A: Most browser tests run against a simulated API in the browser; an end-to-end set runs against the real platform API with a disposable database and covers sign-in, the required password change, session end, role-based navigation, and one create flow per entity.
- Q: Which language(s) should the Web UI text be in for this first slice? → A: English only, but all user-facing text goes through translation keys so further languages need no view changes.
- Q: Which accessibility target should the automated checks in this slice measure? → A: Keyboard operation and visible focus, plus automated WCAG 2.2 AA rule checks on every view in the browser tests with 0 violations; no certification or manual audit.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Sign in, stay signed in safely, and sign out (Priority: P1)

A club member opens the Web UI, signs in with the local account a Club Admin created, and works until they sign out or the session ends. When their initial password must be changed, the Web UI asks for a new password before anything else. A member who received a single-use set-password credential sets their password with it. Any member can change their own password. When the session expires, is ended by a Club Admin, or the account is deactivated, the next action returns the member to sign-in without losing the information that they were signed out.

**Why this priority**: Nothing else in the Web UI works without a session. Sign-in is also the first thing the first Club Admin does after a stamp is bootstrapped, because the initial password must be changed.

**Independent Test**: Against a platform with a bootstrapped club, sign in as the first Club Admin, change the required initial password, sign out, sign in again with the new password, and observe that an ended session or a deactivated account sends the browser back to sign-in on the next action.

**Acceptance Scenarios**:

1. **Given** an active member with a valid password, **When** they sign in, **Then** the Web UI shows the home view for their roles, and no password, session secret, or token is readable by page scripts or stored in browser storage.
2. **Given** wrong credentials, a locked account, or a deactivated account, **When** someone signs in, **Then** the Web UI shows one generic refusal message that does not reveal which of these applies.
3. **Given** a member whose password change is required, **When** they sign in, **Then** the Web UI shows only the password-change view until the change succeeds, and every other view is unreachable.
4. **Given** a single-use set-password credential code, **When** the member opens the set-password page, enters their sign-in name and the code, and chooses a password that meets the platform's password policy, **Then** the password is set and the member can sign in; an expired, used, or revoked code shows a clear message and asks the member to contact a Club Admin.
5. **Given** a signed-in member, **When** they change their own password with their current password, **Then** the change succeeds and the platform's rules for other sessions apply.
6. **Given** a signed-in member, **When** they sign out, **Then** the session ends on the platform and the browser returns to sign-in; using the browser's back button shows no member data.
7. **Given** a signed-in member whose session expired, was ended by a Club Admin, or whose account was deactivated, **When** they take their next action, **Then** the Web UI returns them to sign-in with a message that the session ended.
8. **Given** a signed-in member, **When** the Web UI changes data on the platform, **Then** every state-changing request carries the session's anti-forgery token, and a refused anti-forgery check is shown as a session problem rather than a silent failure.

---

### User Story 2 - Find my way by my roles (Priority: P2)

After sign-in, members see an application shell with navigation that shows only what their club and team roles allow: Club Admins see club administration, coaches and viewers see the teams they belong to, and everyone sees their own account. The shell shows whether the platform is reachable, so members know when an action cannot be carried out.

**Why this priority**: Role-aware navigation keeps members from running into refusals and is the frame every later feature plugs into. It needs a session (Story 1).

**Independent Test**: Sign in as a Club Admin, a team Coach, and a team Viewer, and compare the navigation and the reachable views with what each role allows; stop the platform and observe the connectivity state.

**Acceptance Scenarios**:

1. **Given** a Club Admin, **When** they sign in, **Then** the navigation offers club, seasons, teams, members, and their own account.
2. **Given** a member who is a Coach or Viewer on some teams and has no club role, **When** they sign in, **Then** the navigation offers only their teams (read-only) and their own account, and no club administration.
3. **Given** any member, **When** they open the address of a view their roles do not allow, **Then** the Web UI shows a not-permitted view without data from that view, and the platform's own refusal remains authoritative.
4. **Given** a signed-in member, **When** the platform becomes unreachable, **Then** the shell shows that the platform is not reachable, state-changing actions are disabled, and the Web UI recovers automatically when the platform is reachable again.
5. **Given** a member whose roles change while signed in, **When** they take their next action, **Then** navigation and reachable views follow the new roles.

---

### User Story 3 - Club Admins manage the club, seasons, and teams (Priority: P3)

A Club Admin edits the club's details, creates seasons, activates one, archives finished ones, and creates and edits the teams of a season. When two admins edit the same item, the second save is refused instead of silently overwriting the first, and the Web UI offers to reload the current version.

**Why this priority**: Seasons and teams are the structure every later workflow (matches, uploads, analysis) hangs on, and they can only be managed through the API today.

**Independent Test**: As a Club Admin, change the club name, create a season, activate it, create two teams in it, edit one, archive an older season, and provoke a concurrent edit to see the conflict handling.

**Acceptance Scenarios**:

1. **Given** a Club Admin, **When** they edit the club's details, **Then** the change is saved and shown.
2. **Given** a Club Admin, **When** they create a season, **Then** it appears in the season list in the draft state; **and When** they activate it, **Then** it becomes the active season as the platform's rules allow; **and When** they archive a season, **Then** it is shown as archived and its teams become read-only as the platform defines.
3. **Given** a season, **When** a Club Admin creates a team in it or edits a team's details, **Then** the team is saved and shown in the season.
4. **Given** an item that another admin changed since it was loaded, **When** a Club Admin saves their edit, **Then** the save is refused, nothing is overwritten, and the Web UI offers to reload the current version.
5. **Given** an input the platform refuses (for example a missing required name), **When** the Club Admin saves, **Then** the Web UI shows the platform's reason next to the affected field.

---

### User Story 4 - Club Admins manage members and roles (Priority: P4)

A Club Admin creates members, issues each a single-use set-password credential to hand over, assigns and removes club roles (Club Admin, Registrar) and team roles (Coach, Viewer), deactivates and reactivates members, unlocks locked accounts, and ends a member's sessions.

**Why this priority**: Without member administration, every new coach or viewer has to be created through the API. It builds on Stories 1–3.

**Independent Test**: As a Club Admin, create a member, issue a credential, let the member set a password and sign in, give them the Coach role on a team, check their navigation, then deactivate them and observe that their next action returns them to sign-in.

**Acceptance Scenarios**:

1. **Given** a Club Admin, **When** they create a member, **Then** the member appears in the member list without a usable password.
2. **Given** a member, **When** a Club Admin issues a set-password credential, **Then** the Web UI shows the credential once as a code to hand over, never shows it again, and never puts it into an address.
3. **Given** a member, **When** a Club Admin assigns or removes a club role or a team role, **Then** the change is saved and shown, and the platform's rules (for example that the last Club Admin cannot remove their own role) are shown as clear refusals.
4. **Given** an active member, **When** a Club Admin deactivates them, **Then** the member is shown as deactivated and their sessions end; **and When** the Club Admin reactivates them, **Then** they can sign in again.
5. **Given** a locked member, **When** a Club Admin unlocks them, **Then** the member can sign in again.
6. **Given** a member with active sessions, **When** a Club Admin ends their sessions, **Then** the member's next action returns them to sign-in.

### Edge Cases

- The platform is reachable but returns an unexpected error: the Web UI shows a generic error with a correlation reference and does not show internal details.
- The set-password page is opened while another member is signed in: the Web UI asks to sign out first, so the credential is never applied to the wrong account.
- A member's browser holds an outdated build of the Web UI after a platform release: the Web UI detects an incompatible API response and asks to reload instead of failing silently.
- The OpenAPI document changes incompatibly: the Web UI build fails at client generation or type checking, so the break is caught before release.
- A very long list of members or teams: lists stay usable through paging or filtering offered by the platform.
- A Club Admin removes their own Club Admin role while other Club Admins exist: the navigation updates at the next action and club administration disappears.
- The browser blocks cookies: sign-in fails with a message that cookies are required.

## Requirements *(mandatory)*

### Functional Requirements

#### Project and platform access

- **FR-001**: The Web UI MUST be one project in the clients source area (`src/clients/web/`) that provides the build, unit-test, and browser-test entry points and the pinned browser-test tool version the Web UI verification environment requires.
- **FR-002**: The Web UI MUST access the platform only through a transport client generated from the platform's published OpenAPI document; generated code MUST stay in ignored output and MUST be used only behind application-owned adapters, so generated types never define the Web UI's own models.
- **FR-003**: The Web UI MUST hold no business rules the platform enforces; platform refusals (authorization, validation, conflict, version required) MUST be shown, never second-guessed.

#### Session and account

- **FR-004**: The Web UI MUST sign members in with a local account through the platform's backend-for-frontend session and MUST NOT store passwords, session secrets, or tokens in browser storage or expose them to page scripts.
- **FR-005**: Every state-changing request MUST carry the session's anti-forgery token as the platform requires.
- **FR-006**: Sign-in refusals MUST be shown with one generic message regardless of reason.
- **FR-007**: When the platform reports that a password change is required, the Web UI MUST show only the password-change view until the change succeeds.
- **FR-008**: The Web UI MUST let a member set a password on a set-password page by entering their sign-in name and a single-use credential code, and change their own password, showing the platform's password-policy refusals clearly. Credentials MUST never appear in an address.
- **FR-009**: When a request is refused because the session expired, was ended, or the account was deactivated, the Web UI MUST return to sign-in with a session-ended message and MUST discard cached member data.
- **FR-010**: Sign-out MUST end the session on the platform and discard cached member data, so navigating back shows no member data.

#### Shell, roles, and connectivity

- **FR-011**: Navigation and reachable views MUST follow the member's current club roles (Club Admin, Registrar) and team roles (Coach, Viewer) as reported by the platform, and MUST update at the next action after a role change.
- **FR-012**: Views the member's roles do not allow MUST show a not-permitted view without data.
- **FR-013**: The shell MUST show whether the platform is reachable, MUST disable state-changing actions while it is not, and MUST recover without a page reload when it is reachable again.

#### Club administration

- **FR-014**: Club Admins MUST be able to view and edit the club's details.
- **FR-015**: Club Admins MUST be able to list, create, activate, and archive seasons, following the platform's season states (draft, active, archived).
- **FR-016**: Club Admins MUST be able to list, create, and edit the teams of a season; members with team roles MUST be able to view their teams read-only.
- **FR-017**: Edits MUST send the version the item was loaded with; a refused stale save MUST NOT overwrite anything and MUST offer to reload the current version.
- **FR-018**: Club Admins MUST be able to list and create members, issue a single-use set-password credential (shown once as a code), deactivate and reactivate members, unlock them, end their sessions, and assign and remove club and team roles.

#### Delivery and security

- **FR-019**: In local development, the Web UI MUST be served together with the platform API from one origin through the Aspire AppHost, so the session cookie and anti-forgery protection work as in production.
- **FR-020**: The Web UI MUST follow the client security baseline: a strict Content Security Policy with Trusted Types, no third-party scripts, and Subresource Integrity for its own assets.
- **FR-021**: User-facing text MUST be clear for non-technical club members and MUST come from translation keys with English as the only shipped language, and the Web UI MUST be operable by keyboard with visible focus and pass automated WCAG 2.2 AA rule checks on every view, using the theme's accessible controls.

### Key Entities

- **Session (client view)**: Whether a member is signed in, whether a password change is required, the member's identity, club roles, and team roles; never the session secret.
- **Club, Season, Team, Member, Role assignment**: The platform's entities as exposed by its API; the Web UI holds only cached copies with their versions.
- **Set-password credential**: A single-use secret the platform issues for a member, shown once to the Club Admin and entered by the member.
- **Connectivity state**: Whether the platform is currently reachable, as observed by the Web UI.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A first Club Admin can go from the bootstrapped initial password to a working club structure (one active season with two teams and one invited coach) in under 10 minutes without using any tool other than the Web UI.
- **SC-002**: 100% of the acceptance scenarios of this specification are covered by automated unit or browser tests that run in the Web UI verification environment, and the end-to-end set against the real platform passes on every merge.
- **SC-003**: 0 session secrets, passwords, or set-password credentials appear in browser storage, page-readable cookies, or Web UI logs in the browser tests.
- **SC-004**: In 100% of browser tests that end a session (expiry, sign-out, deactivation, session termination), the next action lands on sign-in and shows no member data.
- **SC-005**: In 100% of browser tests for each role, the navigation offers exactly the views that role allows.
- **SC-006**: 0 violations of the Content Security Policy are reported in the browser tests.
- **SC-007**: Every view in scope can be operated by keyboard alone, and automated WCAG 2.2 AA rule checks report 0 violations on every view in the browser tests.

## Assumptions

- **Actors**: Club Admins administer the club; Registrars have no additional views in this slice (Analyst Manager registration screens come later); Coaches and Viewers see their teams read-only; every member manages their own password.
- **API coverage**: Every operation used here already exists in the platform API from the club and identity foundation; this feature adds no platform behavior. If a needed read operation (for example listing a member's roles for navigation) proves missing, the plan names it and the platform change stays minimal and within that feature's design.
- **Generated client source**: How the Web UI build obtains the platform's OpenAPI document for client generation (for example a build-time document produced by the platform) is decided by the plan, within the generated-client boundary of the architecture.
- **Browser tests and the platform**: Most browser tests run against a simulated API in the browser that follows the platform's OpenAPI document. An end-to-end set runs against a real platform API with a disposable database, which the verification environment supports with the .NET SDK and the container runtime; it covers sign-in, the required password change, session end, role-based navigation, and one create flow each for seasons, teams, and members. The plan decides how the end-to-end set starts the platform.
- **Browsers and devices**: Automated tests use Chromium; the supported-browser matrix starts with Chromium-based browsers on Windows, as the architecture states. Phone layouts are usable but not optimized in this slice.
- **Language**: The user interface ships in English only; all user-facing text comes from translation keys, so adding languages later needs no view changes.
- **Out of scope**: the service worker, PWA install, and offline mode; matches, recording upload, and analysis review; agent conversations; the Analyst Manager fleet view and registration approval; external OpenID Connect sign-in and MFA, which the API does not provide yet; the production container image and deployment; browsers other than Chromium in automated tests; accessibility certification.
- **Evidence**: Passing validation is development evidence only and does not establish deployment support or production readiness.
- **Dependencies**: `specs/20261012-145505-web-ui-environment` (Web UI verification environment) is reviewed and merged before this feature starts, as the constitution's environment-feature rule requires. `specs/archive/20261005-130701-club-identity-foundation` provides the session, anti-forgery, membership, roles, and the club hierarchy API this feature uses.
- **Architecture References**: [docs/architecture/client-applications.md](../../docs/architecture/client-applications.md) (Source Ownership And Delivery; Web UI; Delivery And Workspace), [docs/architecture/platform-implementation.md](../../docs/architecture/platform-implementation.md) (Client Technology; API And Identity), [docs/architecture/contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md) (Generated Client Boundary), [docs/architecture/security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md) (Web UI security baseline).
