# Research: Club and Identity Foundation

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md)

This document resolves every open technical question for the feature. Each
entry records the decision, its rationale, and the alternatives considered.
Shared conventions for all platform foundation plans (layers, persistence
abstractions, error and concurrency mapping) are taken as given and are not
re-decided here.

## R1. ASP.NET Core Identity integration without EF Core

- **Decision**: Use `Microsoft.Extensions.Identity.Core` 10.0.12 (`UserManager<TUser>`,
  `IPasswordHasher<TUser>`, password validators, lockout, security stamps,
  `IdentityOptions`) inside the Infrastructure layer. An internal user type
  `IdentityMemberAccount` maps the `member_account` table. An internal Dapper
  store implements `IUserStore`, `IUserPasswordStore`,
  `IUserSecurityStampStore`, `IUserLockoutStore`, and `IUserTwoFactorStore`.
  It runs every statement on the connection and transaction of the current
  `IUnitOfWork`. Application code reaches Identity only through Application
  abstractions (`IAccountCredentialService`). No `SignInManager`, no EF Core,
  and no Identity UI are used.
- **Rationale**: The architecture assigns password hashing, security stamps,
  recovery tokens, lockout, and MFA readiness to ASP.NET Core Identity with
  Dapper and PostgreSQL stores ([platform-implementation.md](../../docs/architecture/platform-implementation.md#api-and-identity)).
  `Microsoft.Extensions.Identity.Core` contains exactly these services without
  HTTP or cookie dependencies. This keeps the Identity user type internal to
  Infrastructure and satisfies the layer rule that Infrastructure exposes only
  `AddInfrastructure(...)`. Running the store on the unit-of-work transaction
  makes account, role, and audit changes atomic (FR-047).
- **Alternatives considered**:
  - `Microsoft.AspNetCore.Identity.EntityFrameworkCore` was rejected because EF
    Core is excluded by the architecture.
  - `SignInManager<TUser>` was rejected. It requires `HttpContext` and the
    built-in cookie handler (see R2), and it would leak the user type into the
    API.
  - A hand-written password hasher and lockout were rejected because they
    duplicate audited framework code and contradict the architecture.

## R2. Server-validated BFF session

- **Decision**: Implement the session as an ASP.NET Core authentication scheme
  `SocAlyticsSession` with a small custom `AuthenticationHandler` in the API.
  Sign-in creates a `member_session` row and returns a random 256-bit opaque
  session token in the cookie `__Host-socalytics-session` (`Secure`,
  `HttpOnly`, `SameSite=Strict`, `Path=/`, no `Domain`, no `Expires`). The
  database stores only the SHA-256 hash of the token. On every request the
  handler hashes the cookie value and calls the Application query
  `ValidateSessionQuery`. The session is accepted only when all of these hold:
  - the session row exists and is not ended;
  - it is neither idle-expired nor past its absolute lifetime;
  - the account's membership is `active`;
  - the account's security stamp equals the stamp recorded in the session.
  The principal carries only the account id and session id. Roles are never
  read from the cookie. Challenges return `401` problem details and never
  redirect.
- **Rationale**:
  - Server-side state makes sign-out, end-all-sessions, deactivation, and
    password changes take effect on the next request (FR-027, FR-028, FR-031,
    FR-039).
  - Hash-only storage means no session secret exists at rest (DAT-002, POL-002).
  - The design works across several API instances without a shared key ring.
  - The cookie flags meet FR-024 and BND-001.
- **Alternatives considered**:
  - The built-in cookie handler with an `ITicketStore` and a Data Protection
    key ring persisted in PostgreSQL was rejected. Several API instances need a
    shared key ring, which puts reusable secret key material into the stamp
    database and its backups while POL-002 backup exclusion is unresolved. It
    still needs a server-side store for end-all-sessions, so it adds moving
    parts without removing any.
  - Stateless signed cookies or JWTs were rejected because revocation cannot
    take effect on the next request without a server lookup.
  - A browser-held bearer token was rejected because the architecture forbids
    it (BND-001).

## R3. Anti-forgery for state-changing requests

- **Decision**: Use a per-session synchronizer token. Sign-in generates a
  random 256-bit anti-forgery token, stores its SHA-256 hash in the session
  row, and returns the raw value in the response body (`antiforgeryToken`).
  `GET /api/v1/session` returns it again for page reloads. An endpoint filter
  `SessionAntiforgeryFilter` on every session-authenticated `POST`, `PUT`,
  `PATCH`, and `DELETE` endpoint requires the header `X-CSRF-Token` and
  compares its hash with the stored hash in fixed time. A missing or wrong
  value is rejected with `403` and problem type `antiforgery-failed` before any
  handler runs. The two anonymous state-changing endpoints (sign-in and
  credential redemption) accept only `application/json` (other media types get
  `415`), which a cross-site form cannot send without a CORS preflight. No CORS
  policy is enabled.
- **Rationale**: This satisfies FR-025 and SC-006 for JSON APIs. The token is
  bound to one session, survives multi-instance routing because it lives in
  the database, and needs no Data Protection keys (see R2). Combined with
  `SameSite=Strict`, it is defense in depth.
- **Alternatives considered**:
  - `IAntiforgery` was rejected because it depends on Data Protection keys and
    has the same multi-instance key ring problem as R2.
  - Relying on `SameSite=Strict` alone was rejected because the spec requires
    explicit anti-forgery proof.
  - A double-submit cookie was rejected because a server-stored synchronizer
    token is simpler to validate and audit.

## R4. Session lifetime, invalidation, and security stamps

- **Decision**: Session options are `IdentityAccess:Session:IdleTimeout` and
  `IdentityAccess:Session:AbsoluteLifetime`. Each session stores
  `idle_expires_at` and `absolute_expires_at`. Validation slides
  `last_seen_at` and `idle_expires_at` at most once per 60 seconds, to avoid a
  write on every request. Idle expiry is checked against the stored value
  before sliding.
  - The security stamp rotates on password set, change, or reset (Identity
    behavior), on membership deactivation, and when a Club Admin ends a
    member's sessions.
  - Session rows are also marked ended with a reason: `sign-out`,
    `password-changed`, `password-reset`, `deactivated`, `ended-by-admin`, or
    `replaced`.
  - A password change keeps the current session but records the new stamp on
    it, and ends every other session of the account.
  - A sign-in that presents an existing session cookie ends that session
    (`replaced`) before issuing a new one, which prevents session fixation.
- **Rationale**: Both mechanisms are checked on every request. The stamp covers
  every session at once; the session row covers per-session sign-out and
  expiry (FR-026, FR-027, FR-020, FR-021, FR-031). Throttled sliding keeps the
  idle error below one minute.
- **Alternatives considered**:
  - Security stamp only was rejected because it cannot end a single session on
    sign-out.
  - Session rows only were rejected because they lose the Identity-native
    invalidation the architecture names.
  - Updating `last_seen_at` on every request was rejected as an unnecessary
    write load.

## R5. One-time set-password and reset credentials

- **Decision**: Register an internal Identity token provider
  `OneTimeCredentialTokenProvider` (an `IUserTwoFactorTokenProvider<TUser>`)
  for the purposes `set-password` and `password-reset`. It is backed by the
  `one_time_credential` table.
  - **Issuance**: generate 256 random bits, encoded as base64url (43
    characters), and store only the SHA-256 hash with the account, purpose,
    issuer, and expiry (`IdentityAccess:OneTimeCredential:Lifetime`). Issuance
    revokes every earlier unused credential of the account.
  - **Redemption**: `POST /api/v1/credentials/redeem` takes the account name,
    the credential, and the new password. The new password is validated
    against the password policy first, so a policy failure reveals nothing
    about the credential. The credential is then consumed atomically with
    `UPDATE ... SET consumed_at = now() WHERE credential_hash = @Hash AND
    member_account_id = @AccountId AND purpose = @Purpose AND consumed_at IS
    NULL AND revoked_at IS NULL AND expires_at > now() RETURNING id`.
  - The password is set through `UserManager`, which rotates the security
    stamp. All sessions end, and the redemption audit event is written in the
    same unit of work.
  - Every failure (unknown, expired, used, revoked, or another account's
    credential) returns the same `400 credential-invalid`. On rollback the
    credential stays unused.
  - The raw credential appears only in the issuing response, which carries
    `Cache-Control: no-store`.
- **Rationale**: This meets single use and time limits (FR-016, FR-021), binding
  to one account (edge case), and secret-free storage. It stays within the
  Identity token-provider model the architecture names ("recovery tokens").
  Set-password and reset share one mechanism.
- **Alternatives considered**:
  - `DataProtectorTokenProvider` was rejected. It needs Data Protection keys,
    and its tokens are not intrinsically single-use; they are only invalidated
    by a stamp change.
  - Shorter human-friendly codes were rejected. They need rate limiting to
    resist guessing, and the 43-character token is handed over out-of-band
    anyway.
  - Generating an initial password was rejected because FR-016 forbids it.

## R6. Uniform sign-in failure and lockout

- **Decision**: Sign-in normalizes the account name and loads the account.
  - Unknown accounts run one password-hash verification against a fixed dummy
    hash, so timing is comparable.
  - Lockout uses Identity options (`MaxFailedAccessAttempts`,
    `DefaultLockoutTimeSpan`, `AllowedForNewUsers = true`) from
    `IdentityAccess:Lockout`.
  - Locked accounts are refused before the password is checked.
  - Wrong passwords increment the failure counter. Reaching the threshold sets
    `lockout_end` and records `account.locked-out`.
  - Accounts with an inactive membership or no password yet are refused.
  - Every failure returns the identical `401` problem body
    (`sign-in-failed`, fixed title and detail).
  - The failure counter, lockout, and `session.sign-in` audit event (outcome
    `failed`, internal `reason_code`) commit in one unit of work.
  - The audit event names the account only when it exists. Unknown account
    names are not recorded, because users sometimes type passwords into the
    name field.
- **Rationale**: This satisfies FR-018, FR-019, SC-005, and the US6 lockout
  scenarios with framework lockout semantics, while keeping audit evidence
  minimized (FR-046).
- **Alternatives considered**:
  - Distinct error messages were rejected because FR-018 forbids them.
  - Recording attempted unknown names was rejected for minimization.
  - IP-based rate limiting was deferred. It is not required, and production
    ingress is out of scope.

## R7. Club and first Club Admin bootstrap

- **Decision**: The API hosted service `ClubBootstrapHostedService`, a
  `BackgroundService` in the API, runs the Application command
  `BootstrapClubCommand` once per process start. Configuration section
  `ClubBootstrap` provides `ClubDisplayName`,
  `FirstClubAdmin:AccountName`, and `FirstClubAdmin:InitialPassword`. The
  password is protected configuration (user secrets locally, a secret store in
  deployments) and is never logged.
  - The handler runs in one unit of work. It first takes
    `pg_advisory_xact_lock` on the constant key for `club-bootstrap`, then
    reads the club.
  - **No club**: validate the configuration, create the member account (hash
    the initial password through `UserManager`), assign `club-admin`, insert
    the singleton `club` row with `bootstrap_admin_account_id`, record
    `club.bootstrapped`, and commit.
  - **Club exists and the configured account name matches the recorded
    bootstrap administrator**: change nothing and record nothing. The initial
    password is not re-applied.
  - **Club exists and the names differ**: change nothing, log a diagnostic
    without secrets, record `club.bootstrap-refused` in an independent
    transaction, and report the readiness check `club-bootstrap` as unhealthy
    with reason `bootstrap-conflict`.
  - **No club and no configuration**: create nothing, and report readiness
    unhealthy with reason `club-not-established`. No default credential and no
    unauthenticated setup operation exist.
  - Database unavailability is retried with bounded exponential backoff. The
    process keeps running, and readiness stays unhealthy until bootstrap
    succeeds.
  - The singleton constraint on `club` and the unique normalized account name
    are backstops. A unique violation is treated as "another instance won":
    roll back, re-read, and continue.
- **Rationale**:
  - The transaction-scoped advisory lock serializes concurrent starts of
    several API instances. Exactly one creates the club, administrator, and
    audit event, and the others see the committed club and change nothing
    (FR-002, US1 scenario 3).
  - Running in the API keeps password hashing, Identity, and `IAuditTrail` on
    the runtime role.
  - Readiness gating keeps every instance starting normally while still
    signaling a misconfigured stamp.
- **Alternatives considered**:
  - The Migrator was rejected. It holds the DDL migration role, runs
    schema-only forward migrations without application secrets, and must not
    depend on Identity or domain handlers. A bootstrap would also add domain
    records to the migration path.
  - An unauthenticated setup endpoint was rejected because FR-003 forbids it.
  - Relying only on unique constraints without the lock was rejected. It works,
    but it lets losing instances hash passwords and attempt audit inserts that
    must then roll back, which makes "one audit event" harder to reason about.
  - Failing the process on conflicting configuration was rejected. It would
    crash-loop replicas, and readiness already blocks traffic.
  - A configured one-time bootstrap credential instead of an initial password
    was rejected. It can expire before first use with no recovery path, and
    SC-001 expects direct sign-in.

## R8. Configuration values and no production defaults

- **Decision**: Bind `IdentityAccess` options (`Session`, `Lockout`,
  `Password`, `OneTimeCredential`) with data-annotation validation and
  `ValidateOnStart()`. The values exist only in
  `appsettings.Development.json` and in test configuration. A start without
  them fails with a diagnostic that names the missing key.
  - The development and test values are: idle timeout 30 minutes, absolute
    lifetime 8 hours, 5 failed attempts, 15 minutes lockout, minimum password
    length 12 with no composition rules, and one-time credential lifetime 24
    hours.
  - The AppHost provides bootstrap parameters. The password is a secret
    parameter generated per developer and persisted to AppHost user secrets
    (`AddParameter` with a generated default). The account name defaults to
    `club-admin`, and the club display name defaults to `Development Club`.
- **Rationale**: The spec and POL-001 and POL-002 leave production values
  unresolved. Requiring explicit configuration prevents development values from
  becoming implicit production defaults. A per-developer generated password is
  not a shipped default credential (FR-003).
- **Alternatives considered**:
  - Code defaults were rejected because they would silently become production
    defaults.
  - Committing a fixed development password was rejected because it is a shipped
    default credential.

## R9. Hierarchy persistence and concurrency

- **Decision**:
  - `club`, `season`, `team`, `match`, and `member_account` are versioned
    aggregate roots with `version bigint not null default 1` and the shared
    `socalytics.advance_version()` trigger. `club_role_assignment` and
    `team_role_assignment` are declared children of `member_account` and use
    the persistence foundation's child-touch trigger.
  - Edits use `WHERE id = @Id AND version = @ExpectedVersion`, and zero rows is
    reported as a concurrency conflict.
  - Season activation and archive use
    `WHERE id = @Id AND state = @ExpectedState`.
  - "At most one active season" is a partial unique index on `season` where
    `state = 'active'`. A unique violation on activation maps to
    `409 season-already-active`.
  - Writes under a season take `SELECT ... FOR SHARE` on the season row (team
    edits and creation, match creation and edits through the team's season) and
    reject `archived` with `409 season-archived`. Archive's row-exclusive
    `UPDATE` then serializes against them, so no change can commit into a
    season that was archived concurrently (FR-008).
  - Parents are required foreign keys (FR-012), and no delete paths exist
    (FR-013).
  - The `season_id` of a team, and the `team_id` and opponent of a match, are
    never part of an `UPDATE` statement. Update contracts reject them with
    `400 validation-failed` (field code `immutable`).
- **Rationale**: This follows the adopted version-trigger and edit or
  lifecycle conventions
  ([platform-implementation.md](../../docs/architecture/platform-implementation.md#persistence-and-cqrs))
  and closes the write-skew gap between archive and child edits under
  PostgreSQL `READ COMMITTED`.
- **Alternatives considered**:
  - `SERIALIZABLE` isolation for every command was rejected because retry
    handling adds complexity without need.
  - Database triggers enforcing column immutability were rejected because no
    write path touches those columns. Contract validation and integration tests
    give the evidence.
  - Auto-archiving the previous active season was rejected because the spec's
    default rejects it.

## R10. Last Club Admin invariant under concurrency

- **Decision**: Every command that can remove Club Admin authority first takes
  `pg_advisory_xact_lock` on the constant key for `club-admin-invariant`. These
  are revoking `club-admin` and deactivating a member who holds `club-admin`.
  The command then locks the target `member_account` row `FOR UPDATE`, counts
  active members holding `club-admin` other than the target, and rejects with
  `409 last-club-admin` when the count is zero.
- **Rationale**: Statements after the lock see every previously committed
  change under `READ COMMITTED`, so two Club Admins revoking each other
  serialize, and the second sees one remaining admin (FR-034, edge case). The
  lock is held only by these rare commands.
- **Alternatives considered**:
  - A deferred constraint trigger counting admins was rejected because it does
    not prevent write skew without `SERIALIZABLE`.
  - Locking the `club` row was rejected because it would also block unrelated
    club setting edits.
  - `SERIALIZABLE` with retry was rejected because the explicit lock is simpler
    and deterministic.

## R11. Membership and role lifecycle actions

- **Decision**: Every membership or role action first locks the target account
  with `SELECT ... FROM member_account WHERE id = @Id FOR UPDATE`, then checks
  the current state.
  - **Deactivate** (`active` → `deactivated`): delete all club and team role
    assignments, rotate the security stamp, end all sessions, and record
    `member.deactivated`.
  - **Reactivate** (`deactivated` → `active`): no roles are restored and no
    sessions are revived.
  - An action the current state does not allow is rejected with
    `409 invalid-state-transition` or `409 membership-inactive`.
  - Role assignment and revocation are idempotent set operations (`PUT` and
    `DELETE` on the role sub-resource). Assigning a held role or revoking an
    absent one returns `200` with no change and an audit outcome of
    `unchanged`.
  - Assigning a team role upserts on `(member_account_id, team_id)` and
    replaces a different role (FR-035).
  - Team roles can be assigned on teams of any season state, because roles
    govern access and are not team data.
  - None of these actions requires `If-Match`. An `If-Match` that is sent is
    ignored.
- **Rationale**: Locking the root row serializes concurrent deactivation and
  role assignment, so every order ends deactivated with no roles (US2 scenario
  7, FR-049). This follows the lifecycle rule in
  [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md#representation-conventions).
- **Alternatives considered**:
  - Version-guarded role changes were rejected because FR-049 forbids requiring
    versions.
  - Rejecting an already-held role with `409` was rejected. Idempotent set
    semantics are simpler for clients and need no retry keys.
  - Deactivating without revoking roles was rejected because it contradicts
    FR-031.

## R12. Authorization model and team scope

- **Decision**: Authorization is decided in the Application layer.
  - The API applies one coarse policy, `ActiveMember`, which requires a valid
    `SocAlyticsSession` principal, to the `/api/v1` group. Only sign-in and
    credential redemption are marked anonymous.
  - Application handlers call `IAccessAuthorizer`:
    - `AuthorizeClubAsync(ClubPermission)`: `Administer` requires
      `club-admin`; `Register` requires `registrar` or `club-admin`.
    - `AuthorizeTeamResourceAsync(TeamOwnedResource, TeamPermission)`: `Read`
      requires a team role or `club-admin`; `Write` requires `coach` or
      `club-admin`.
    - `GetVisibleTeamsAsync()`: returns all teams for Club Admins and the
      teams with a current role for others. It is used for list filtering
      (FR-041).
  - Team resources resolve through `ITeamScopeResolver`, which dispatches to
    `ITeamScopeSource` implementations registered per resource kind. This
    feature registers `team` and `match`; later features register their own
    kinds. An unknown kind or an unresolvable resource is denied (FR-040).
  - Membership and roles are read from the database on every authorization
    decision (FR-039).
  - Outcomes:
    - An unknown resource, or a team-owned resource that the member cannot
      read, returns `404 not-found`, so existence is not disclosed (FR-040).
    - A readable resource with insufficient permission, or a club operation
      without the role, returns `403 forbidden`.
    - Every denial records `authorization.denied` through
      `IAuditTrail.RecordIndependentAsync`, because the command's unit of work
      rolls back.
- **Rationale**: The architecture places authorization in Application
  handlers. A pluggable scope resolver gives later features (recordings,
  analysis, agents) the same team boundary without editing this feature's code.
- **Alternatives considered**:
  - ASP.NET Core resource-based authorization handlers in the API were rejected
    because they put authorization outside the Application layer and need the
    resource loaded twice.
  - Role claims in the cookie were rejected because revocation would not take
    effect on the next request.
  - Returning `403` for cross-team access was rejected because it discloses
    existence.

## R13. Security audit trail

- **Decision**: Use the table `socalytics.security_audit_event` with these
  columns: id (UUIDv7), `occurred_at`, `event_type`, `action`, `outcome`,
  `actor_kind`, `actor_account_id`, `session_id`, `resource_type`,
  `resource_id`, `team_id`, `data_class`, `reason_code`, `details` (jsonb),
  and `correlation_id`. It has no foreign keys and no version.
  - `IAuditTrail.RecordAsync(AuditEvent)` inserts on the current unit-of-work
    transaction and throws when none is active (FR-047).
    `IAuditTrail.RecordIndependentAsync(AuditEvent)` uses its own short
    transaction for denials, sign-in failures, and refused bootstraps.
  - Actor, session, and correlation id (the W3C trace id of the current
    activity) are filled from `IRequestContext`.
  - `details` accepts only allow-listed keys per event type: role, purpose,
    previous role, end reason, and state names. It never accepts free text.
  - The migration revokes `UPDATE`, `DELETE`, and `TRUNCATE` on the table from
    `socalytics_app`. It also attaches `BEFORE UPDATE OR DELETE` row and
    `BEFORE TRUNCATE` statement triggers that raise an exception (FR-048).
  - Logging rules: request and response bodies of sign-in, password, and
    credential endpoints are never logged, and exception messages never include
    credential values. A test scans captured logs and audit rows for known
    secret values (SC-007).
- **Rationale**: This meets FR-044 to FR-048 and the audit field list in
  [security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md#audit-events).
  The stamp identity is implicit in the stamp database and is added when
  evidence leaves the stamp. The production audit store, integrity
  verification, and retention stay unresolved (POL-009, GOV-AUD).
- **Alternatives considered**:
  - Writing audit events to application logs was rejected because FR-048
    requires separation.
  - A separate schema or role for audit was rejected because the architecture
    has one application schema and one runtime role. Table privileges plus a
    trigger achieve append protection with an architecture note.
  - A hash chain for integrity was deferred. It serializes inserts, and the
    integrity policy is unresolved.

## R14. HTTP representation, concurrency, and errors

- **Decision**:
  - Single aggregates (club, season, team, match, member) return
    `ETag: "<version>"` (strong).
  - Edits (`PUT /api/v1/club`, `PUT /api/v1/teams/{teamId}`,
    `PUT /api/v1/matches/{matchId}`) require `If-Match`:
    - a missing value, or `*`, returns `428 version-required`;
    - a stale or unparsable value returns `412 version-mismatch`.
  - Lifecycle actions never require `If-Match` and return the updated
    representation with its new `ETag`.
  - Errors are RFC 9457 `application/problem+json` with
    `type = urn:socalytics:problem:<code>` and the extension members `code`,
    `correlationId`, and `errors` (field violations).
  - Order of checks: authentication, then anti-forgery, then request
    validation, then authorization and visibility, then the read-only season
    check, then the version check.
  - Lists use `items` plus an opaque `continuationToken` (a base64url keyset
    cursor), with page size 50 by default and 200 at most.
- **Rationale**: This follows
  [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md#representation-conventions)
  and the shared status mapping. URN problem types are stable without implying
  a hosted documentation site.
- **Alternatives considered**:
  - `PATCH` with JSON merge patch was rejected because it is more complex for
    three small editable field sets.
  - Version fields in request bodies were rejected because HTTP validators are
    the adopted convention.
  - Treating `If-Match: *` as satisfied was rejected because the spec requires
    the last-seen version.

## R15. OpenAPI publication and contract evidence

- **Decision**: Map endpoints with minimal APIs that carry an explicit
  `operationId` (`WithName`), tags, documented response types, and status
  codes.
  - The built-in document `/openapi/v1.json` (OpenAPI 3.1) adds security
    schemes through a document transformer: `sessionCookie` (apiKey in cookie
    `__Host-socalytics-session`) and `antiforgeryHeader` (apiKey in header
    `X-CSRF-Token`).
  - [contracts/openapi.yaml](contracts/openapi.yaml) is the design contract.
  - A contract test asserts that the generated document contains every
    `operationId`, path, method, and documented status of the design contract.
    Its expected set is maintained as test data.
- **Rationale**: This meets FR-043 and keeps the generated document as the
  runtime publication, while repository-root canonical contracts remain future
  work.
- **Alternatives considered**:
  - A hand-maintained runtime document was rejected because it duplicates
    generation.
  - NSwag or Swashbuckle was rejected because the built-in generator is already
    adopted.

## R16. Test strategy and evidence

- **Decision**:
  - Behavior tests live in `src/platform/Tests/SocAlytics.Platform.Integration.Tests`
    under `Club/` and `IdentityAccess/`. They use
    `Microsoft.AspNetCore.Mvc.Testing` 10.0.12 (`WebApplicationFactory` with an
    `https://localhost` base address so `Secure` cookies flow) and the
    persistence foundation's Testcontainers PostgreSQL fixture, with
    migrations applied through the Migrator code path.
  - Pure rule tests (account-name normalization, season transitions) run in the
    same project without containers.
  - Concurrency tests use parallel connections with barriers.
  - The authorization matrix (SC-003) is data-driven over role × operation ×
    same, other, and unknown team.
  - The secret-leak test (SC-007) scans audit rows and captured logs.
  - Architecture tests gain rules:
    - Domain and Application reference no `Microsoft.AspNetCore.*`,
      `Microsoft.Extensions.Identity.*`, `Dapper`, or `Npgsql`;
    - Identity store types stay internal;
    - every `/api/v1` endpoint requires authorization except an explicit
      anonymous allow list;
    - every authenticated unsafe-method endpoint carries
      `SessionAntiforgeryFilter`.
  - Host tests keep the Aspire smoke test and assert that the new operations
    appear in `/openapi/v1.json`.
- **Rationale**: This covers FR-001 to FR-049 and SC-001 to SC-009 with
  executable evidence on real PostgreSQL (constitution IV) without adding test
  projects beyond the shared conventions.
- **Alternatives considered**:
  - A separate unit-test project was rejected because the shared conventions
    fix the platform test projects, and the logic is mostly SQL and HTTP.
  - In-memory databases were rejected because they cannot prove triggers,
    locks, or privileges.

## R17. Local composition and HTTPS

- **Decision**: The AppHost keeps the API's HTTP endpoint for health checks and
  adds an HTTPS endpoint (`WithHttpsEndpoint()`, ASP.NET Core development
  certificate) for interactive sign-in. It passes the `ClubBootstrap__*`
  environment variables from AppHost parameters (see R8). Readiness includes
  the persistence `database` check and the new `club-bootstrap` check.
- **Rationale**: `Secure` and `__Host-` cookies require a secure context. The
  HTTP health endpoint keeps the existing host smoke test independent of
  certificates on CI runners.
- **Alternatives considered**:
  - An HTTPS-only API was rejected because CI runners may lack a trusted
    development certificate.
  - Relaxing `Secure` in development was rejected because tests should exercise
    production cookie flags.

## R18. Account names, match details, and identifiers

- **Decision**:
  - Account names are 3 to 64 characters from letters, digits, `.`, `_`,
    `-`, and `@`. They are stored as entered, unique by their upper-invariant
    normalized form, and immutable in this feature.
  - Display names for the club, seasons, and teams, and opponent names, are 1
    to 100 characters after trimming.
  - Match details are `kickoffAt` (RFC 3339 UTC, required), `homeAway`
    (`home`, `away`, or `neutral`, required), and `competition` (optional, up
    to 100 characters).
  - The opponent snapshot is `opponent.name`, captured at creation.
  - Identifiers are UUIDv7 values generated by the application and exposed as
    opaque strings.
- **Rationale**: These are minimal fields that make matches useful to the
  recording upload feature. UUIDv7 gives index-friendly ordering.
- **Alternatives considered**:
  - An opponent registry was rejected because it is not in scope; the snapshot
    keeps history stable.
  - Member display names and contact data were rejected because the spec does
    not require them and they add personal data.
