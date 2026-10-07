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
  It runs every statement through the persistence foundation's internal
  `IDbSession`, which is bound to the current `IUnitOfWorkScope` (opened by
  `IUnitOfWork.BeginAsync`). Application code reaches Identity only through
  Application abstractions (`IAccountCredentialService`). No `SignInManager`,
  no EF Core, and no Identity UI are used.
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

- **Decision**: Use a per-session synchronizer token derived from the session
  token: `antiforgeryToken = base64url(HMAC-SHA256(key = raw session token,
  message = "socalytics-csrf"))`. Nothing is stored for it.
  - Sign-in returns the derived value in the response body
    (`antiforgeryToken`), and `GET /api/v1/session` returns it again for page
    reloads.
  - The endpoint filter `SessionAntiforgeryFilter` runs on every
    session-authenticated `POST`, `PUT`, `PATCH`, and `DELETE` endpoint. It
    recomputes the value from the raw session token in the request cookie,
    which the authentication handler has already validated, and compares it
    with the `X-CSRF-Token` header in fixed time
    (`CryptographicOperations.FixedTimeEquals`).
  - A missing or wrong value is rejected with `403` and problem type
    `antiforgery-failed` before any handler runs.
  - The two anonymous state-changing endpoints (sign-in and credential
    redemption) accept only `application/json` (other media types get `415`),
    which a cross-site form cannot send without a CORS preflight. No CORS
    policy is enabled.
- **Rationale**:
  - This satisfies FR-025 and SC-006 for JSON APIs.
  - The token is bound to exactly one session. It cannot be computed without
    the `HttpOnly` session cookie, which scripts cannot read, so other sites
    cannot obtain it.
  - Recomputation works on every instance without stored state, Data
    Protection keys (R2), or a database read. It also removes any stored token
    material.
  - Domain separation through the fixed message keeps the value distinct from
    the session token and its stored SHA-256 hash.
  - Combined with `SameSite=Strict`, it is defense in depth.
- **Alternatives considered**:
  - An independent random token with its hash stored in the session row was
    rejected after the security design review. It needs an extra column and a
    read, and adds nothing: both values die with the session.
  - `IAntiforgery` was rejected because it depends on Data Protection keys and
    has the same multi-instance key ring problem as R2.
  - Relying on `SameSite=Strict` alone was rejected because the spec requires
    explicit anti-forgery proof.
  - A double-submit cookie was rejected because a session-bound derived token
    is simpler to validate and audit.

## R4. Session lifetime, invalidation, and security stamps

- **Decision**: Session options are `IdentityAccess:Session:IdleTimeout` and
  `IdentityAccess:Session:AbsoluteLifetime`. Each session stores
  `idle_expires_at` and `absolute_expires_at`. Validation slides
  `last_seen_at` and `idle_expires_at` at most once per 60 seconds, to avoid a
  write on every request. Idle expiry is checked against the stored value
  before sliding.
  - The security stamp rotates on password set, change, or reset (Identity
    behavior), on membership deactivation, when a Club Admin ends a member's
    sessions, and on break-glass recovery (R19).
  - Session rows are also marked ended with a reason: `sign-out`,
    `password-changed`, `password-reset`, `deactivated`, `ended-by-admin`,
    `replaced`, or `break-glass-recovery`.
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
    about the credential. The account row is locked `FOR UPDATE`, and the
    credential is consumed atomically with `UPDATE socalytics.one_time_credential
    c SET consumed_at = now() FROM socalytics.member_account a WHERE
    c.credential_hash = @Hash AND c.member_account_id = @AccountId AND
    a.id = c.member_account_id AND a.membership_status = 'active' AND
    c.purpose = @Purpose AND c.consumed_at IS NULL AND c.revoked_at IS NULL
    AND c.expires_at > now() RETURNING c.id` (FR-052).
  - **Revocation (FR-052)**: open credentials (unconsumed, unrevoked) get
    `revoked_at` and one `credential.revoked` audit event each
    (`details.purpose`, `reason_code`). This happens in the same unit of work
    as the change that triggers it:
    - When a member loses `club-admin`, through revocation or deactivation,
      every open credential they issued
      (`issued_by_account_id = @AccountId`, index
      `ix_one_time_credential_issuer_open`) is revoked with reason
      `issuer-lost-authority`.
    - When a member is deactivated, their own open credentials are also
      revoked, with reason `target-deactivated`.
    - Issuing a newer credential for the account revokes earlier ones, with
      reason `superseded`.
    - Break-glass recovery revokes the account's open credentials, with reason
      `break-glass-recovery`.

    The active-membership guard in the redemption `UPDATE` also covers a
    deactivation that commits concurrently, because the account row lock
    serializes the two.
  - The password is set through `UserManager`, which rotates the security
    stamp. All sessions end, and the redemption audit event is written in the
    same unit of work.
  - Every failure (unknown, expired, used, revoked, inactive target, or
    another account's credential) returns the same `400 credential-invalid`.
    On rollback the credential stays unused.
  - The raw credential appears only in the issuing response, which carries
    `Cache-Control: no-store`.
- **Rationale**: This meets single use and time limits (FR-016, FR-021), binding
  to one account (edge case), and secret-free storage. Credentials die with
  their issuer's authority or their target's membership (FR-052). It stays
  within the Identity token-provider model the architecture names ("recovery
  tokens"). Set-password and reset share one mechanism.
- **Alternatives considered**:
  - Checking the issuer's current authority at redemption time instead of
    revoking eagerly was rejected. The spec requires an audited revocation
    event, and eager revocation leaves an explicit trail.
  - `DataProtectorTokenProvider` was rejected. It needs Data Protection keys,
    and its tokens are not intrinsically single-use; they are only invalidated
    by a stamp change.
  - Shorter human-friendly codes were rejected. They need rate limiting to
    resist guessing, and the 43-character token is handed over out-of-band
    anyway.
  - Generating an initial password was rejected because FR-016 forbids it.

## R6. Uniform sign-in failure and lockout

- **Decision**: Sign-in normalizes the account name and loads the account.
  - **One hash verification on every path.** Before any refusal decision,
    every attempt runs exactly one `IPasswordHasher.VerifyHashedPassword`
    call. The call uses the stored hash when the account has one, and a fixed
    dummy hash otherwise: for an unknown account, or an account without a
    password yet. The dummy hash has the same PBKDF2 format and iteration
    count as real hashes and is generated once at start-up. Only then are the
    lockout, membership, and no-password checks evaluated. Locked and inactive
    accounts therefore cost the same as a wrong password (FR-018).
  - Lockout uses Identity options (`MaxFailedAccessAttempts`,
    `DefaultLockoutTimeSpan`, `AllowedForNewUsers = true`) from
    `IdentityAccess:Lockout`.
  - **Counter rules.** While an account is locked, a sign-in attempt is
    refused even when the password is correct. It does not increment
    `access_failed_count`, so the lockout cannot be extended.
  - Outside lockout, a wrong password increments the counter. Reaching the
    threshold sets `lockout_end` and records `account.locked-out`.
  - Accounts with an inactive membership or no password yet are refused
    without touching the counter.
  - Every failure returns the identical `401` problem body
    (`sign-in-failed`, fixed title and detail).
  - The failure path performs the same database work on every branch: one
    account lookup, one audit insert, and the commit. Only the conditional
    counter update differs; it is a single-row `UPDATE` with negligible cost
    next to PBKDF2.
  - The failure counter, lockout, and `session.sign-in` audit event (outcome
    `failed`, internal `reason_code`) commit in one unit of work.
  - The audit event names the account only when it exists. Unknown account
    names are not recorded, because users sometimes type passwords into the
    name field.
  - **Evidence.** A timing-distribution test samples at least 200 attempts
    per failure class against the in-process host. The classes are: unknown
    account, account without a password, wrong password, locked account, and
    inactive membership. The test compares each class with the wrong-password
    class using a two-sample Kolmogorov–Smirnov test at α = 0.01, after
    warm-up, with interleaved order. It fails on a statistically significant
    difference (SC-005).
- **Rationale**: This satisfies FR-018, FR-019, SC-005, and the US6 lockout
  scenarios with framework lockout semantics, while keeping audit evidence
  minimized (FR-046). PBKDF2 dominates request time, so equalizing hash work
  equalizes the response-time distributions.
- **Alternatives considered**:
  - Distinct error messages were rejected because FR-018 forbids them.
  - Refusing locked or inactive accounts before hashing was rejected after the
    security design review, because it created a measurable timing oracle.
  - A fixed artificial delay was rejected. It does not hide hashing variance
    and slows every sign-in.
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
  deployments) and is never logged. The section is not validated at start-up;
  each value is read only on the path that needs it.
  - The handler runs in one unit of work. It first takes
    `pg_advisory_xact_lock` on the constant key for `club-bootstrap`, then
    reads the club.
  - **No club**: validate `ClubDisplayName`, `AccountName`, and
    `InitialPassword` against the password policy, then:
    - create the member account, hashing the initial password through
      `UserManager`, with `password_change_required = true` (FR-051);
    - assign `club-admin`;
    - insert the singleton `club` row with `bootstrap_admin_account_id`;
    - record `club.bootstrapped`, and commit.

    The first sign-in therefore yields the restricted session of R20. The
    administrator must change the configured password before any other
    operation, so the configured value never remains a working credential.
  - **Club exists**: the handler never reads `InitialPassword`. Its absence is
    valid, so the operator removes the secret from the deployment
    configuration after the first Club Admin has signed in and changed the
    password. `AccountName` is optional at this point.
    - If it is absent, nothing is checked and nothing changes.
    - If it matches the recorded bootstrap administrator, nothing changes and
      nothing is recorded.
  - **Club exists and the configured account name differs** from the recorded
    bootstrap administrator: change nothing, log a diagnostic without secrets,
    record `club.bootstrap-refused` in an independent transaction, and report
    the readiness check `club-bootstrap` as unhealthy with reason
    `bootstrap-conflict`.
  - **No club and no configuration**: create nothing, and report readiness
    unhealthy with reason `club-not-established`. No default credential and no
    unauthenticated setup operation exist.
  - Database unavailability is retried with bounded exponential backoff. The
    process keeps running, and readiness stays unhealthy until bootstrap
    succeeds.
  - The singleton constraint on `club` and the unique normalized account name
    are backstops. A unique violation is treated as "another instance won":
    roll back, re-read, and continue.
  - In the same unit of work and under the same lock, the service then
    evaluates an optional break-glass recovery directive (R19). Its outcome
    never affects readiness.
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
    was rejected. It can expire before first use with no recovery path. The
    forced first-sign-in change (FR-051) already keeps the configured value
    from remaining a working credential.
  - Re-applying or re-validating `InitialPassword` on every start was
    rejected. It would keep a secret in configuration indefinitely and could
    reset an administrator's own password (FR-051).

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
  - The AppHost provides bootstrap parameters:
    - The password parameter is
      `AddParameter("first-club-admin-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true)`.
      It is generated once per developer and persisted to AppHost user
      secrets. The platform spike confirmed this on Aspire 13.4.6: the value
      was identical across three runs. Persistence works only when
      `SocAlytics.Platform.AppHost.csproj` has a `<UserSecretsId>`; without one,
      a new value is generated every run, silently. The persistence foundation
      adds that `<UserSecretsId>` for its own generated role passwords, and
      this feature reuses it without adding a second one.
    - The account name defaults to `club-admin`, and the club display name
      defaults to `Development Club`.
    - The break-glass recovery directive (R19) has no AppHost parameter. It is
      an operator action supplied only when needed.
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
    aggregate roots with `version bigint not null default 1`. Each is attached
    with `CALL socalytics.attach_version_trigger(...)`, which uses
    `socalytics.advance_version()`.
  - `club_role_assignment` and `team_role_assignment` are attached with
    `CALL socalytics.attach_aggregate_child_triggers(child, 'socalytics.member_account', 'member_account_id')`,
    which uses `socalytics.touch_aggregate_root()`.
  - Unversioned tables are classified in `PersistedTableClassifications.cs`
    (see [data-model.md](data-model.md#aggregates-and-version-ownership)).
  - Guarded writes go through the persistence foundation's versioned-write
    helpers. Their `VersionedWriteResult` (`Applied`, `NotFound`,
    `ConcurrencyConflict`) maps to `OperationFailure`.
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
  - **Deactivate** (`active` → `deactivated`), in one unit of work:
    - delete all club and team role assignments;
    - rotate the security stamp and end all sessions;
    - revoke the member's own open one-time credentials
      (`target-deactivated`);
    - if the member held `club-admin`, revoke every open credential they
      issued (`issuer-lost-authority`);
    - record `member.deactivated` and one `credential.revoked` per credential
      (FR-052).
  - **Revoke `club-admin`**: in the same unit of work, revoke every open
    credential the member issued (`issuer-lost-authority`), with
    `credential.revoked` events (FR-052). Revoking `registrar` revokes nothing
    else, because only Club Admins issue credentials.
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
  - The API applies one coarse policy, `ActiveMember`, which requires a valid,
    unrestricted `SocAlyticsSession` principal, to the `/api/v1` group. Four
    self-service operations use `SessionHolder` instead, which also admits a
    session restricted by a required password change (R20). Only sign-in and
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

## R19. Break-glass account recovery (FR-050)

- **Decision**: An operator recovers an account through an optional
  protected-configuration section `BreakGlassRecovery`. It has three keys:
  `AccountName`, `RecoveryId` (an operator-chosen single-use identifier of 8
  to 128 characters), and `TemporaryCredential` (an operator-supplied secret
  that must satisfy the password policy). The section comes from the
  deployment secret store, as environment variables or user secrets.
  - `ClubBootstrapHostedService` evaluates the directive at every start, after
    the bootstrap step (R7), in the same unit of work under
    `pg_advisory_xact_lock('club-bootstrap')`. The Application command
    `ApplyBreakGlassRecoveryCommand` (area `IdentityAccess`) runs these checks
    in order, and the first one that fails refuses the directive:
    1. incomplete section (`directive-incomplete`);
    2. recovery id already in `recovery_directive_use` (`recovery-id-used`);
    3. unknown account (`unknown-account`);
    4. deactivated membership, checked after locking the account
       `FOR UPDATE` (`account-inactive`);
    5. password policy violation (`password-policy`).
  - When every check passes, one unit of work applies the directive:
    - set the password hash through `UserManager`;
    - set `member_account.password_change_required = true`;
    - clear the lockout;
    - rotate the security stamp, which invalidates every session;
    - mark the sessions ended (`break-glass-recovery`);
    - revoke open one-time credentials;
    - insert the `recovery_directive_use` row;
    - record `break-glass-recovery.applied`.
  - A refusal changes no account state. It records
    `break-glass-recovery.refused` with the reason code and logs a diagnostic
    that names the reason and configuration key only.
  - Readiness stays healthy either way. The `club-bootstrap` check reflects
    bootstrap state only.
  - Recovery never assigns or removes roles. No API operation, endpoint, or
    unauthenticated path can trigger it.
  - The temporary credential is bound through an options type that is never
    logged. Validation messages name keys, never values. The credential is
    never written to audit `details`, problem details, or diagnostics
    (FR-046).
  - The runtime role holds only `INSERT` and `SELECT` on
    `recovery_directive_use`, so a used identifier cannot be deleted to make
    it reusable.
  - Leaving the directive configured after use is harmless. Each later start
    records a `recovery-id-used` refusal, which also signals the operator to
    remove it.
- **Rationale**:
  - The ledger's unique recovery id plus the bootstrap lock gives "at most
    once, also when several instances start together" (FR-050).
  - Refusals that do not consume the id let an operator fix a typo and retry
    with the same id.
  - Stamp rotation reuses the existing next-request invalidation (R4).
  - The forced change (R20) limits the temporary credential's exposure,
    because the operator knows it.
  - Recovery reuses the bootstrap lock and the start path, so it adds no
    operation and no new lock key.
  - It resolves the sole-administrator lockout risk without an unauthenticated
    recovery endpoint.
- **Alternatives considered**:
  - The platform could generate a temporary credential and emit it to logs or
    console output for the operator. This was rejected because FR-046 forbids
    credentials in diagnostic output, and logs are copied to telemetry stores
    with weaker protection (DAT-010, POL-010).
  - An unauthenticated recovery endpoint protected by a shared recovery secret
    was rejected. FR-050 forbids any unauthenticated recovery operation, and
    the endpoint would widen the attack surface permanently.
  - Applying recovery in the Migrator was rejected for the same reasons as
    bootstrap (R7): it would need the DDL role, application secrets, and
    domain logic.
  - Direct operator SQL against the database was rejected. It bypasses
    password hashing, the audit trail, and session invalidation, and needs
    privileged database access (CTL-012).
  - Making the directive idempotent by content, without a ledger, was
    rejected. Restarting with the same directive would reset the password
    again and undo the member's own password change, which violates "starting
    again with the same directive changes nothing" (US6 scenario 6).
  - Recording refused directives in the ledger was rejected because the
    operator could then not correct an account-name typo while keeping the
    same id.
  - Granting `club-admin` during recovery was rejected because FR-050 forbids
    role changes. Restoring roles stays a Club Admin action.

## R20. Password change required at next sign-in

- **Decision**: `member_account.password_change_required` marks an account
  whose current password was supplied by an operator. That covers the first
  Club Admin created by bootstrap (FR-051, R7) and an account recovered by
  break-glass (FR-050, R19).
  - Sign-in still succeeds with the uniform failure rules (R6), but the
    session is restricted. `SessionInfo` and `CurrentMember` carry
    `passwordChangeRequired: true`.
  - A second API policy, `AuthorizationPolicyNames.SessionHolder`, admits a
    valid restricted session for `getSession`, `getCurrentMember`,
    `changeOwnPassword`, and `signOut` only.
  - Every other `/api/v1` operation uses `ActiveMember`, which rejects a
    restricted session with `403` and problem code `password-change-required`
    before any handler or role check runs.
  - `changeOwnPassword` clears the flag in its unit of work. The rest of its
    behavior is unchanged: it rotates the stamp, keeps the current session,
    and ends other sessions.
- **Rationale**:
  - The member proves knowledge of the operator-supplied password through the
    normal sign-in path, including lockout and audit, and then uses the
    existing password-change operation, so no new credential-bearing endpoint
    is needed.
  - Restriction is evaluated per request from current account state, so it
    lifts immediately after the change (FR-039 semantics).
  - The extra fields and problem code are additive to the contract.
- **Alternatives considered**:
  - Refusing sign-in with a special status and adding an unauthenticated
    "change expired password" endpoint was rejected. It creates a second
    credential-accepting anonymous endpoint, and a distinct status on sign-in
    reveals that the account was recovered.
  - Silently allowing full access with a reminder was rejected because FR-050
    and FR-051 require the change.
