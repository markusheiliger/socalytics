# Identity Access Specification

## Purpose

Defines local account lifecycle, secure browser sessions, club membership, capabilities, Team grants, authorization enforcement, and security audit outcomes.

## ADDED Requirements

### Requirement: Platform-managed local accounts support secure lifecycle operations

The platform SHALL always support local accounts with password hashing, security-stamp invalidation, password reset, lockout, and multi-factor-ready account state. Authentication responses SHALL NOT disclose whether an account exists, is locked, or has a valid reset request.

#### Scenario: Valid local credentials are submitted

- **WHEN** an active club member submits valid local credentials and no additional factor is currently required
- **THEN** the platform establishes an authenticated BFF session
- **THEN** no password, recovery token, security stamp, or bearer token is returned

#### Scenario: Invalid credentials accumulate

- **WHEN** failed login attempts reach the configured lockout policy
- **THEN** subsequent login attempts are denied until the account is unlocked by policy or authorized administration
- **THEN** the public response does not reveal whether lockout or invalid credentials caused the denial

#### Scenario: Password reset is completed

- **WHEN** a valid, unexpired, single-use reset token and an acceptable new password are submitted
- **THEN** the password and security stamp are changed atomically
- **THEN** the reset token cannot be reused and existing sessions for that account cease to authorize protected requests

#### Scenario: MFA is not yet required

- **WHEN** an account is created before an MFA factor and enrollment policy are approved
- **THEN** the account retains the state needed for future factor enrollment and requirement enforcement
- **THEN** the platform does not claim that MFA is enabled or production-ready

### Requirement: Browser authentication uses a secure BFF cookie session

Successful login SHALL establish a server-validated session through a Secure, HttpOnly, SameSite cookie. Browser clients SHALL NOT receive or store identity-provider bearer tokens, and logout or security-stamp invalidation SHALL prevent the session from authorizing later protected requests.

#### Scenario: Login succeeds

- **WHEN** a member completes local login
- **THEN** the response issues a Secure, HttpOnly, SameSite session cookie
- **THEN** the authenticated principal is resolved server-side on later requests

#### Scenario: Logout succeeds

- **WHEN** an authenticated member submits a valid logout request
- **THEN** the server invalidates or rejects the current session and expires its cookie
- **THEN** reuse of the former cookie does not authorize a protected request

#### Scenario: An unauthenticated request reaches a protected operation

- **WHEN** a request without a valid session targets a protected operation
- **THEN** the API returns an unauthenticated outcome without redirecting the API caller to a login page

### Requirement: State-changing browser requests require CSRF validation

Every cookie-authenticated state-changing browser operation SHALL require a valid anti-forgery token bound to the session. Safe read operations SHALL NOT mutate state.

#### Scenario: Anti-forgery evidence is valid

- **WHEN** an authenticated member submits a state-changing request with valid anti-forgery evidence
- **THEN** the request proceeds to authorization and application validation

#### Scenario: Anti-forgery evidence is missing or invalid

- **WHEN** a cookie-authenticated state-changing request lacks valid anti-forgery evidence
- **THEN** the request is rejected before its state change executes
- **THEN** no domain, identity, grant, or audit-authority state is changed by the attempted operation

### Requirement: Club membership and capabilities govern Club-wide authority

An account SHALL require active club membership to use protected stamp resources. Club Admin and Registrar SHALL be explicit revocable capabilities; Club Admin SHALL include Registrar authority. Club Admin SHALL manage membership, capabilities, and Team grants, while Registrar SHALL manage Club hierarchy registration data but SHALL NOT manage membership or privilege assignments.

#### Scenario: Club Admin performs an administrative operation

- **WHEN** an active member with current Club Admin capability manages a membership, capability, Team grant, or Club hierarchy resource
- **THEN** the authorized operation is permitted subject to validation and concurrency

#### Scenario: Registrar manages hierarchy data

- **WHEN** an active member with Registrar but not Club Admin capability manages a Season, Team, or Match
- **THEN** the hierarchy operation is permitted subject to validation and concurrency

#### Scenario: Registrar attempts privilege administration

- **WHEN** a Registrar without Club Admin capability attempts to change membership, capabilities, or Team grants
- **THEN** the request is forbidden
- **THEN** no privilege state changes

#### Scenario: Membership is revoked

- **WHEN** an account's club membership is revoked
- **THEN** its existing sessions and grants no longer authorize protected requests

### Requirement: Team grants fail closed on revocation and scope mismatch

Non-admin protected access SHALL require an active grant for the resource's authoritative Team scope. Authorization SHALL evaluate current membership and current grant state for each protected request and SHALL deny revoked, missing, or different-Team grants.

#### Scenario: Current Team grant matches

- **WHEN** an active member with a current Team grant requests an operation allowed by that grant on a resource resolving to the same Team
- **THEN** the operation is authorized

#### Scenario: Team grant was revoked

- **WHEN** a member reuses an established session after the relevant Team grant is revoked
- **THEN** the next protected request is forbidden
- **THEN** cached session claims do not restore the revoked authority

#### Scenario: Grant belongs to another Team

- **WHEN** a member has a grant for Team A and requests a protected resource resolving to Team B
- **THEN** the request is forbidden
- **THEN** no Team B data is disclosed or changed

### Requirement: Identity and authorization writes use optimistic concurrency

State-changing membership, capability, and Team-grant operations SHALL require an expected version and SHALL reject stale writes without partial changes.

#### Scenario: Administrative write has the current version

- **WHEN** an authorized Club Admin changes membership, capability, or Team-grant state using the current version
- **THEN** the state change and version advance commit atomically

#### Scenario: Administrative write has a stale version

- **WHEN** an authorized Club Admin submits a stale expected version
- **THEN** the request is rejected as a concurrency conflict
- **THEN** the persisted authorization state remains unchanged

### Requirement: Security-relevant outcomes produce minimized audit records

The platform SHALL record minimized audit evidence for login success and failure, logout, reset request and completion, lockout, membership and capability changes, Team-grant changes, authorization denials, Club hierarchy administration, and bootstrap administration. Audit records SHALL identify the event, time, actor when known, affected resource, Team scope when applicable, action, outcome, correlation identifier, and reason or approval reference when applicable, without recording secrets or sensitive request payloads.

#### Scenario: Authorization is denied

- **WHEN** a protected request is denied because membership, capability, or Team authority is absent, revoked, or mismatched
- **THEN** a minimized denial audit record is persisted
- **THEN** the record contains no cookie, password, reset token, anti-forgery token, or bearer token

#### Scenario: Privilege state changes

- **WHEN** a Club Admin changes membership, a capability, or a Team grant
- **THEN** the state change and corresponding audit record commit atomically
- **THEN** the record identifies the actor, target, action, outcome, and relevant Team without storing secret values

### Requirement: Identity and access operations are represented in OpenAPI

The versioned OpenAPI document SHALL describe local login, logout, password-reset request and completion, anti-forgery acquisition, current-session, membership, capability, and Team-grant operations with their authentication, CSRF, and response requirements.

#### Scenario: Identity OpenAPI operations are inspected

- **WHEN** a contributor retrieves the versioned OpenAPI document
- **THEN** the supported identity and authorization operations and their principal response outcomes are present
- **THEN** no external OpenID Connect or Analyst Manager pairing operation is present
