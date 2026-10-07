# Feature Specification: Club and Identity Foundation

**Feature Branch**: `20261005-130701-club-identity-foundation`

**Created**: 2026-10-05

**Status**: Draft

**Input**: User description: "Club and Identity Foundation: establish the single-club organizational hierarchy (Club > Season > Team > Match), platform-managed local accounts with secure human sessions, club membership with Club Admin and Registrar roles, per-team Coach and Viewer roles, and team-scoped authorization with security audit evidence, building on the shared durable storage foundation in `specs/20261005-130700-platform-persistence-foundation`."

## Clarifications

### Session 2026-10-07

- Q: Which club-identity changes must be based on the version the requester last saw, and which are lifecycle actions protected by their state rules instead? → A: Edits that send back changed fields (club settings, team details, match details) require the last-seen version; lifecycle actions (season activate and archive, membership deactivate and reactivate, role assign and revoke, account unlock, credential issuance, ending sessions) require no version and are checked against the current state and rules.
- Q: What should happen when someone sends an edit of club settings, team details or match details without saying which version they last saw? → A: Refuse it without change, with an outcome distinct from the outdated-version conflict.
- Q: When several platform instances start at the same time against a fresh database with the same first-Club-Admin configuration, what must the result be? → A: Exactly one club and one first Club Admin with one bootstrap audit event; every other instance changes nothing and starts normally.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - First Club Admin signs in to the club (Priority: P1)

When a club's deployment is first set up, the operator supplies the identity of the first Club Admin through protected deployment configuration. That person can then sign in with their local account, see who they are and which roles they hold, and sign out. Their session is protected against theft and misuse, and anyone who is not signed in cannot use any protected capability.

**Why this priority**: Every other capability in the platform depends on knowing who is acting. Without a trustworthy first administrator and a secure session, no club data can be managed or protected.

**Independent Test**: Start a fresh club deployment with a first Club Admin configured. Confirm that this person can sign in, see their identity and Club Admin role, and sign out. Confirm that unauthenticated requests and requests that reuse a signed-out session are refused.

**Acceptance Scenarios**:

1. **Given** a new club deployment whose protected configuration names a first Club Admin, **When** the deployment starts, **Then** exactly one club exists, exactly one active member holds the Club Admin role, and a bootstrap audit event is recorded without any secret value.
2. **Given** the first Club Admin already exists, **When** the deployment restarts with the same bootstrap configuration, **Then** no additional account, member, or club is created and the existing administrator is unchanged.
3. **Given** a fresh deployment whose protected configuration names a first Club Admin, **When** several platform instances start at the same time, **Then** exactly one club, one first Club Admin, and one bootstrap audit event exist, and every instance starts normally.
4. **Given** a member with valid local credentials, **When** they sign in, **Then** a session is established, their identity and current roles are available to them, and no password, recovery credential, or identity token is returned to the client.
5. **Given** a signed-in member, **When** they sign out and the former session is presented again, **Then** the request is refused as unauthenticated.
6. **Given** no valid session, **When** any protected operation is requested, **Then** the request is refused as unauthenticated without disclosing protected data.
7. **Given** a signed-in member, **When** a state-changing request arrives without the anti-forgery proof issued for that session, **Then** the request is rejected before any change takes effect.
8. **Given** an unknown account name or a wrong password, **When** sign-in is attempted, **Then** both cases receive the same generic failure response.

---

### User Story 2 - Club Admin manages club members and club-level roles (Priority: P2)

A Club Admin adds people to the club by creating local accounts. Each new member gets a one-time credential, handed over outside the platform, to set their own password. The Club Admin can assign or remove the Club Admin and Registrar roles, deactivate and reactivate memberships, unlock locked accounts, and end all of a member's active sessions. The club can never be left without an active Club Admin.

**Why this priority**: A club cannot onboard coaches or staff, or remove departed people, without membership administration. Least-privilege access starts here.

**Independent Test**: As the first Club Admin, create a member, set that member's password with the one-time credential, assign and remove the Registrar role, and deactivate the member. Each change must take effect immediately, and a non-admin must be unable to perform any of these operations.

**Acceptance Scenarios**:

1. **Given** a signed-in Club Admin, **When** they create a member account, **Then** an active member with no roles exists and a single-use, time-limited set-password credential is issued only to that Club Admin.
2. **Given** a new member holding an unused set-password credential, **When** they set an acceptable password, **Then** they can sign in, and the credential cannot be used again.
3. **Given** a signed-in Club Admin, **When** they assign the Registrar role to a member, **Then** the member holds Registrar on their next request, and an audit event records the actor, target, role, and outcome.
4. **Given** a member who has live sessions, **When** a Club Admin deactivates that membership, **Then** the member's next request on any existing session is refused, all of the member's roles are revoked, and new sign-in is refused.
5. **Given** exactly one active Club Admin, **When** anyone tries to remove that person's Club Admin role or deactivate their membership, including the person themselves, **Then** the request is rejected and no change is made.
6. **Given** a signed-in member who is not a Club Admin, including a Registrar, **When** they try to create a member, change a role, deactivate a membership, unlock an account, or end another member's sessions, **Then** the request is refused, nothing changes, and a denial audit event is recorded.
7. **Given** two Club Admins acting on the same membership at the same time, **When** one deactivates it and the other assigns it a role, **Then** each action is decided against the membership's current state without requiring a previously seen version: if the deactivation commits first, the role assignment is rejected because the membership is inactive, and in every order the membership ends deactivated with no roles.

---

### User Story 3 - Club Admin organizes seasons and teams (Priority: P3)

A Club Admin creates seasons and moves each one through its life: draft, then active, then archived. The Club Admin creates teams within a season. Only one season can be active at a time, and archived seasons, together with their teams and matches, become read-only.

**Why this priority**: Teams are the boundary for all non-admin access, and every team belongs to a season. The hierarchy has to exist before team roles or matches mean anything.

**Independent Test**: As a Club Admin, create a draft season, add teams, activate the season, and archive it. Confirm that a second active season cannot coexist, that archived content cannot be changed, and that non-admins cannot change seasons or teams.

**Acceptance Scenarios**:

1. **Given** a signed-in Club Admin, **When** they create a season, **Then** it is created in the draft state under the club.
2. **Given** a draft or active season, **When** a Club Admin creates a team in it, **Then** the team belongs to exactly that season and cannot be moved to another season.
3. **Given** an active season already exists, **When** a Club Admin tries to activate another season, **Then** the request is rejected and both seasons keep their current states.
4. **Given** an active season, **When** a Club Admin archives it, **Then** the season and its teams and matches can no longer be changed, but authorized members can still read them.
5. **Given** a signed-in Coach, Viewer, or Registrar who is not a Club Admin, **When** they try to create, change, activate, or archive a season or team, **Then** the request is refused and nothing changes.
6. **Given** a request that names a season that does not exist, **When** a team is created in it, **Then** the request is rejected and no team is created.

---

### User Story 4 - Coaches and Viewers access only their own teams (Priority: P4)

A Club Admin gives a member the Coach or Viewer role on a specific team, independently for each team. Members see and act on only those teams and their matches. Club Admins can access every team. When a team role is revoked, the change takes effect on the member's very next request.

**Why this priority**: Team-scoped authorization is the core data-protection promise of the club hierarchy. Recordings, analytics, and agent evidence in later features all inherit this boundary.

**Independent Test**: Give one member the Coach role on Team A and the Viewer role on Team B. Confirm that the member can reach Team A and Team B with the matching permissions and cannot reach Team C. Then revoke the Team A role and confirm that the very next request to Team A is refused, even on the same session.

**Acceptance Scenarios**:

1. **Given** a signed-in Club Admin, **When** they give a member the Coach role on a team, **Then** that member can access the team on their next request, and the assignment is audited.
2. **Given** a member who holds a role on Team A only, **When** they request any data that belongs to Team B, **Then** the request is refused, no Team B data is disclosed, and a denial audit event is recorded.
3. **Given** a member with a live session whose Team A role has just been revoked, **When** they send their next Team A request on that same session, **Then** the request is refused.
4. **Given** a member who is not a Club Admin, **When** they list teams, **Then** only the teams on which they currently hold a role are returned.
5. **Given** a Club Admin with no team roles, **When** they request any team or match, **Then** access is granted because Club Admin authority covers every team.
6. **Given** a member who already holds a role on a team, **When** a Club Admin assigns them a different role on that team, **Then** the new role replaces the previous one, so the member holds only one role per team.

---

### User Story 5 - Coaches manage their team's matches (Priority: P5)

A team's Coach records matches for that team, including match details and the opponent the team faced. The opponent is captured once, when the match is created, and cannot be changed later. Viewers of the team can read matches but cannot change them. Club Admins can manage matches for any team.

**Why this priority**: Matches are the containers that the recording upload feature (`specs/20261005-130702-recording-lineage-upload`) attaches to. They also confirm that team-scoped access applies to resources owned by a team.

**Independent Test**: As the Coach of a team, create and update a match. As a Viewer of the same team, read the match and confirm it cannot be changed. As a Coach of another team, confirm the match cannot be reached at all.

**Acceptance Scenarios**:

1. **Given** a Coach of a team in a non-archived season, **When** they create a match for that team, **Then** the match belongs to that team, records the opponent snapshot, and returns its current version.
2. **Given** an existing match, **When** anyone attempts to change its opponent snapshot or its owning team, **Then** the change is rejected.
3. **Given** a Viewer of the match's team, **When** they read the match, **Then** it is returned, and **When** they try to change it, **Then** the request is refused.
4. **Given** a Coach of a different team, **When** they request or change the match, **Then** the request is refused and no data is disclosed.
5. **Given** a match whose season is archived, **When** a Coach tries to change it, **Then** the request is rejected and the match is unchanged.
6. **Given** two members authorized to edit the same match, **When** the second saves changed match details based on an outdated version, **Then** that change is rejected as a conflict and the first change is preserved.

---

### User Story 6 - Accounts are protected against guessing and can be recovered (Priority: P6)

Repeated failed sign-in attempts lock an account for a period, without revealing to the caller that a lockout occurred. Members can change their own password, which ends their other sessions. A member who forgets their password can regain access through a single-use reset credential that a Club Admin issues and hands over outside the platform.

**Why this priority**: Abuse resistance and recovery are needed before real users depend on the platform. They build on the sign-in and membership capabilities from the earlier stories.

**Independent Test**: Exceed the configured failed-attempt threshold for an account. Confirm that even correct credentials are refused until the lockout ends or a Club Admin unlocks the account. Then complete an admin-issued reset and confirm that previously established sessions no longer work.

**Acceptance Scenarios**:

1. **Given** an account that has reached the configured failed-attempt threshold, **When** correct credentials are submitted during the lockout period, **Then** sign-in is refused with the same generic failure response, and the lockout is audited.
2. **Given** a locked account, **When** a Club Admin unlocks it, **Then** the member can sign in again with valid credentials.
3. **Given** a signed-in member, **When** they change their password after proving their current password, **Then** their other existing sessions stop authorizing requests.
4. **Given** a reset credential issued by a Club Admin, **When** the member uses it within its validity period to set an acceptable password, **Then** the password changes, all of the member's prior sessions stop authorizing requests, and the credential cannot be used again.
5. **Given** an expired or already-used reset credential, **When** it is submitted, **Then** the request is rejected and the password is unchanged.

### Edge Cases

- The last active Club Admin tries to remove their own Club Admin role, deactivate their own membership, or be deactivated by someone else. The request is rejected so the club always keeps at least one active Club Admin.
- Two Club Admins remove each other's Club Admin role at the same time. At most one change succeeds, and at least one active Club Admin remains.
- A member whose membership was deactivated keeps sending requests on a live session. The very next request is refused.
- A member's role on a team is revoked while that member has a request in progress. Any change the member requests after the revocation is refused and makes no change.
- A member with roles on Team A crafts a request that names a Team B match by its identifier. The request is refused, Team B data is not disclosed, and the attempt is audited.
- A non-admin asks for a season-wide view that would include teams they hold no role on. Only their own teams' data is returned.
- A request names a team, season, or match that does not exist. The request is rejected without creating or changing anything.
- An edit of club settings, team details, or match details names no version. It is refused without change, and the outcome tells the client that a version is required rather than reporting a conflict.
- Someone tries to create a second club in the same deployment, or the bootstrap configuration names a different first administrator after one already exists. Both are refused without change.
- The deployment starts with no bootstrap configuration and no existing Club Admin. No administrator is created, no default credential exists, and no unauthenticated setup operation is offered.
- Several platform instances start at the same time on a fresh deployment with the same bootstrap configuration. Exactly one club and one first Club Admin are created, one bootstrap audit event is recorded, and no instance fails to start because another instance bootstrapped first.
- A Club Admin tries to activate a second season while one is active, or to change anything in an archived season. Both are rejected.
- A one-time set-password or reset credential is presented twice, after it expired, or for a different account. It is rejected each time.
- A member is reactivated after deactivation. They start with no club or team roles.
- A Registrar who holds no team role tries to read team data or change hierarchy data. The request is refused.
- A sign-in attempt names an account that does not exist. The response cannot be distinguished from a wrong password.

## Requirements *(mandatory)*

### Functional Requirements

#### Club root and bootstrap

- **FR-001**: The system MUST represent exactly one club per deployment. That club is the implicit root of all club data, and the system MUST reject any attempt to create a second club.
- **FR-002**: The system MUST establish the club and its first Club Admin only from protected deployment-supplied configuration. Running the bootstrap again with the same configuration MUST change nothing, and bootstraps performed at the same time by several starting platform instances MUST together create exactly one club, one first Club Admin, and one bootstrap audit event without preventing any instance from starting.
- **FR-003**: The system MUST refuse bootstrap configuration that conflicts with an already established first administrator, MUST NOT ship or accept any default credential, and MUST NOT expose any unauthenticated operation that creates an administrator.
- **FR-004**: Club Admins MUST be able to view and update club settings, such as the club's display name.

#### Seasons, teams, and matches

- **FR-005**: Club Admins MUST be able to create seasons. Each new season starts in the draft state.
- **FR-006**: The system MUST allow only these season transitions, performed by a Club Admin: draft to active, and active to archived.
- **FR-007**: The system MUST allow at most one active season at any time. Activating a second season while one is active MUST be rejected.
- **FR-008**: The system MUST treat an archived season and all of its teams and matches as read-only.
- **FR-009**: Club Admins MUST be able to create and update teams. Every team MUST belong to exactly one existing season, and its season MUST NOT change after creation.
- **FR-010**: Club Admins, and Coaches of the owning team, MUST be able to create and update matches. Every match MUST belong to exactly one existing team, and its owning team MUST NOT change after creation.
- **FR-011**: Every match MUST capture an opponent snapshot when it is created, and that snapshot MUST NOT be changeable afterward.
- **FR-012**: The system MUST reject any creation or change that names a parent that does not exist or would leave a season, team, or match without its required parent.
- **FR-013**: In this feature, the system MUST NOT permanently delete clubs, seasons, teams, or matches.

#### Accounts and sign-in

- **FR-014**: The system MUST always offer platform-managed local accounts that are identified by a unique account name.
- **FR-015**: Only Club Admins MUST be able to create member accounts. Each new account MUST start as an active member with no roles.
- **FR-016**: Creating an account MUST issue a single-use, time-limited set-password credential to the issuing Club Admin only. The system MUST NOT generate or store a usable initial password.
- **FR-017**: The system MUST store passwords only in a form that cannot be reversed, and MUST reject passwords that do not meet the configured password policy.
- **FR-018**: Sign-in failures MUST return the same response whether the account does not exist, the password is wrong, the account is locked, or the membership is inactive.
- **FR-019**: The system MUST lock an account for a configured period once it reaches the configured number of consecutive failed sign-in attempts. Club Admins MUST be able to unlock accounts.
- **FR-020**: Signed-in members MUST be able to change their own password after proving their current password. Doing so MUST end all of the member's other sessions.
- **FR-021**: Club Admins MUST be able to issue a single-use, time-limited password reset credential for a member. Using that credential MUST change the password, end all of that member's existing sessions, and make the credential unusable.
- **FR-022**: Accounts MUST keep the ability to have a second sign-in factor added later. This feature MUST NOT require a second factor or claim that one is enforced.

#### Sessions

- **FR-023**: A successful sign-in MUST establish a server-validated session. Identity tokens and other authentication secrets MUST stay on the server and MUST never be exposed to the client.
- **FR-024**: The session credential held by the client MUST be unreadable by client-side scripts, MUST be sent only over encrypted connections, and MUST be withheld from requests initiated by other sites.
- **FR-025**: Every state-changing request made with a session MUST carry anti-forgery proof issued for that session. Requests without valid proof MUST be rejected before any change takes effect.
- **FR-026**: Sessions MUST expire after a configured idle period and a configured maximum lifetime, whichever comes first.
- **FR-027**: Signing out MUST end the current session so that it cannot authorize any later request.
- **FR-028**: Club Admins MUST be able to end all active sessions of a member.
- **FR-029**: Members MUST be able to view their own identity, membership status, club roles, and team roles.
- **FR-030**: Requests without a valid session MUST receive an unauthenticated outcome rather than being redirected.

#### Membership, roles, and authorization

- **FR-031**: Only Club Admins MUST be able to deactivate and reactivate memberships. Deactivation MUST revoke all of the member's club and team roles and MUST make every existing session of that member stop authorizing requests. Reactivation MUST restore membership with no roles.
- **FR-032**: Only Club Admins MUST be able to assign and revoke the club-level Club Admin and Registrar roles. The Club Admin role MUST include all Registrar authority.
- **FR-033**: In this feature, the Registrar role MUST grant no access to team data and no hierarchy, membership, or role administration.
- **FR-034**: The system MUST reject any change that would leave the club without at least one active member holding the Club Admin role, including when such changes happen concurrently.
- **FR-035**: Only Club Admins MUST be able to assign and revoke a Coach or Viewer role for a member on a specific team. A member MUST hold at most one role per team, and assigning a new role on a team MUST replace the previous one.
- **FR-036**: Viewers MUST be able to read their team and its matches. Coaches MUST additionally be able to create and update their team's matches. Neither role MUST authorize any action outside that team.
- **FR-037**: Club Admins MUST have access to every season, team, and match without holding team roles.
- **FR-038**: Before authorizing any operation on a team-owned resource, the system MUST resolve that resource to its owning team. Non-admin access MUST require an active membership and a current role on that team.
- **FR-039**: Authorization MUST use current membership and role state on every request. Revocations and deactivations MUST take effect no later than the affected member's next request, regardless of session age.
- **FR-040**: The system MUST deny any request whose required team scope is unknown, missing, or different from the requester's teams. Denied requests MUST disclose no data from other teams.
- **FR-041**: Team and match lists for non-admin members MUST include only teams on which the member currently holds a role.

#### Concurrency, interface, and audit

- **FR-042**: Every edit of club settings, team details, or match details MUST be based on the version the requester last saw. Edits based on an outdated version MUST be rejected as conflicts without any partial change. An edit that names no version MUST be refused without any change, with an outcome distinguishable from the outdated-version conflict.
- **FR-049**: Lifecycle actions (season activation and archiving, membership deactivation and reactivation, club and team role assignment and revocation, account unlock, set-password and reset credential issuance, and ending sessions) MUST NOT require a previously seen version. Each MUST be decided against the current state and rules at the time it commits, and an action that the current state does not allow (for example assigning a role to a deactivated membership or archiving a draft season) MUST be rejected as a conflict without any partial change.
- **FR-043**: Every capability in this feature MUST be available through the platform API and described in its published, versioned API description, including authentication, anti-forgery, denial, conflict, and version-required outcomes.
- **FR-044**: The system MUST record security audit events for at least these actions: bootstrap, sign-in success and failure, sign-out, lockout and unlock, password change, set-password and reset credential issuance and use, session termination, membership creation, deactivation, and reactivation, club and team role changes, authorization denials, and season, team, and match administration.
- **FR-045**: Each audit event MUST record an event identifier, event type, time, actor when known, affected resource, team scope when applicable, action, outcome, and correlation identifier.
- **FR-046**: Audit events and diagnostic output MUST NOT contain passwords, one-time credentials, session credentials, anti-forgery proofs, or identity tokens.
- **FR-047**: Every role, membership, and hierarchy change MUST commit together with its audit event. Neither the change nor the event MUST persist without the other.
- **FR-048**: Audit events MUST be protected against modification through ordinary application operations and MUST be stored separately from ordinary application logs.

### Key Entities

- **Club**: The single organizational root of a deployment. It has a display name, settings, and a version. It owns every season directly and every team and match indirectly.
- **Season**: A club-owned period in the draft, active, or archived state. It has a name and a version. It owns teams, and at most one season is active at a time.
- **Team**: A squad that belongs to exactly one season. It has a name and a version, owns matches, and is the authorization boundary for every member who is not a Club Admin.
- **Match**: A container for match metadata that belongs to exactly one team. It holds an immutable opponent snapshot and a version, and recordings and analysis attach to it in later features.
- **Member account**: A local identity for a person in the club. It has a unique account name, password-verification data, lockout state, readiness for a second sign-in factor, an active or deactivated membership status, and a version that every change to the account, its membership status, or its role assignments advances.
- **Club role assignment**: An assignment of the Club Admin or Registrar role to a member. Club Admin includes Registrar. Each assignment can be revoked; it carries no version of its own, and assigning or revoking it advances the member account's version.
- **Team role assignment**: An assignment of the Coach or Viewer role to one member on one team. A member has at most one per team, and each assignment can be revoked; it carries no version of its own, and assigning or revoking it advances the member account's version.
- **Session**: A server-validated period during which a signed-in member is authenticated. It has idle and maximum-lifetime expiry, and sign-out, password changes, or administrative action can end it.
- **One-time credential**: A single-use, time-limited set-password or reset credential bound to one account and issued by a Club Admin.
- **Security audit event**: A minimized, append-protected record of a security-relevant action and its outcome.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On a fresh deployment, the configured first Club Admin can sign in and see their Club Admin role within 2 minutes of the deployment becoming ready, with no manual data setup.
- **SC-002**: A Club Admin can create a season, create a team in it, create a member account, and give that member the Coach role on the team in under 5 minutes.
- **SC-003**: In an authorization test matrix that covers every role, every operation in this feature, and same-team, other-team, and unknown-team targets, 100% of cross-team, revoked-role, deactivated-member, and unauthenticated attempts are denied and disclose no protected data.
- **SC-004**: Revoking a team role, revoking a club role, deactivating a membership, or ending sessions takes effect on 100% of the affected member's next requests, with zero requests authorized after the change.
- **SC-005**: Sign-in responses for an unknown account, a wrong password, a locked account, and an inactive membership cannot be told apart by their content in 100% of tested cases.
- **SC-006**: 100% of the state-changing requests sent without valid anti-forgery proof are rejected with no resulting change.
- **SC-007**: Every security event type listed in this specification produces an audit event in tests, and zero audit events or diagnostic outputs contain a password, one-time credential, session credential, anti-forgery proof, or identity token.
- **SC-008**: In concurrent-edit tests, 100% of edits based on an outdated version are detected and rejected, and 100% of edits that name no version are refused with the version-required outcome; in concurrent lifecycle-action tests, 100% of outcomes match the current state rules with 0 actions refused merely because of an outdated version; and no test run leaves the club without an active Club Admin.
- **SC-009**: 95% of sign-in, sign-out, and single-resource read or update requests complete in under 1 second under local development conditions.

## Assumptions

- **Scope and dependencies**:
  - This feature depends on `specs/20261005-130700-platform-persistence-foundation` for durable storage, ordered schema evolution, transactional writes, version-based conflict detection, and disposable test storage. It does not redefine that foundation.
  - Later features build on this feature's authenticated sessions, Club > Season > Team > Match hierarchy, and roles. These include `specs/20261005-130702-recording-lineage-upload` for recordings attached to matches and `specs/20261005-130704-analyst-manager-registration`, in which Registrars initiate Analyst Manager registration.
  - The feature delivers platform API behavior only. The Web UI, Coach Client, and any other human-facing client applications are out of scope. Behavior is verified through the API and automated tests.
- **Explicitly out of scope or deferred**:
  - Deferred features include external identity provider sign-in (OpenID Connect), Analyst Manager pairing and machine credentials, and agent and MCP access.
  - Deferred features include recording uploads and recording-set finalization, production ingress and encrypted-transport configuration, and self-registration and invitations.
  - Self-service password recovery is deferred because the architecture has not adopted a delivery channel such as email or SMS. In the meantime, recovery uses credentials that a Club Admin issues and hands over outside the platform.
  - Second-factor sign-in is deferred because MFA factors and enrollment policy are not adopted. Accounts are only kept ready for it.
- **Unresolved production values**:
  - Session idle and maximum lifetimes, the failed-attempt threshold and lockout duration, password policy, and one-time credential validity are configurable.
  - Development and test values are not production defaults. The production values remain unresolved under the security and data-governance policy (POL-001, POL-002).
  - Retention, deletion, and holds for identity records and audit events are deferred (POL-001, POL-009). This includes permanent deletion of accounts, seasons, teams, and matches.
  - The accountable audit authority and production audit store remain unresolved. Audit evidence produced by this feature is development evidence, not production-approved evidence.
- **Defaults chosen**:
  - Coaches may create and update their own team's matches. The architecture gives Coaches team-level authority and gives Viewers read-only authority.
  - Deactivating a membership revokes all of its roles, consistent with least privilege, and reactivation starts with no roles.
  - Teams and matches cannot be moved to a different parent.
  - Activating a second season is rejected rather than automatically archiving the current one.
- **Readiness disclaimer**: Successful local execution and tests demonstrate behavior only. They do not demonstrate deployment support or production readiness.
- **Architecture References**:
  - `docs/architecture/terminology-and-principles.md`
  - `docs/architecture/security-and-data-governance.md`
  - `docs/architecture/tenancy-and-technology.md`
  - `docs/architecture/platform-implementation.md`
  - `docs/architecture/contracts-and-compatibility.md`
  - `docs/architecture/client-applications.md`
