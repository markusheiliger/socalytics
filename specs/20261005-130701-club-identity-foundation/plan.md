# Implementation Plan: Club and Identity Foundation

**Branch**: `20261005-130701-club-identity-foundation` | **Date**: 2026-10-07 |
**Spec**: [spec.md](spec.md)

**Input**: Feature specification from
`specs/20261005-130701-club-identity-foundation/spec.md`

**Depends on**: `specs/20261005-130700-platform-persistence-foundation`. That
feature provides:

- `IUnitOfWork.BeginAsync` → `IUnitOfWorkScope`, `VersionedWriteResult`, and
  the internal `IDbSession`;
- `socalytics.attach_version_trigger` and
  `socalytics.attach_aggregate_child_triggers`;
- `PersistedTableClassifications.cs`;
- migrations, the Migrator, and the runtime and migration roles;
- the Integration.Tests PostgreSQL fixture;
- the AppHost `<UserSecretsId>`.

## Summary

The feature establishes who is acting and what they may touch. It covers:

- the singleton club root and the `Club > Season > Team > Match` hierarchy;
- platform-managed local accounts with secure human sessions;
- club membership with Club Admin and Registrar roles;
- per-team Coach and Viewer roles;
- team-scoped authorization with security audit evidence.

The technical approach follows the adopted architecture.

**Identity.** ASP.NET Core Identity core services (`UserManager`, password
hashing, password policy, lockout, security stamps, MFA readiness) run in the
Infrastructure layer over internal Dapper stores on PostgreSQL. There is no EF
Core.

**Sessions.** The API hosts a server-validated BFF session:

- an opaque 256-bit token in a `__Host-` cookie (`Secure`, `HttpOnly`,
  `SameSite=Strict`), stored only as a hash;
- an anti-forgery token in `X-CSRF-Token`, derived per request as
  `HMAC-SHA256(key = session token, "socalytics-csrf")` and never stored;
- uniform sign-in failures: exactly one password-hash verification on every
  path, and no counter increments while an account is locked;
- `401` problem details instead of redirects;
- membership, role, and stamp checks on every request, so revocations take
  effect on the next request.

**Credentials.** Single-use, time-limited set-password and reset credentials
come from a custom Identity token provider over a hashed credential table.
Credentials are revoked, with an audit event, when their issuer loses Club
Admin or is deactivated, or when their target is deactivated. Redemption also
requires an active target membership (FR-052).

**Bootstrap.** A background service in the API creates the club and the first
Club Admin from protected configuration. It runs inside one unit of work under
a PostgreSQL transaction advisory lock, backed by uniqueness constraints, so
concurrent API starts create exactly one club, one admin, and one audit event.
The first Club Admin must change the configured password at first sign-in.
Once the club exists, bootstrap no longer reads the initial password (FR-051).

**Break-glass recovery (FR-050).** The same service then evaluates an optional
recovery directive from protected configuration, in the same unit of work and
under the same lock. The directive names an account, a single-use recovery id,
and an operator-supplied temporary credential. A `recovery_directive_use`
ledger enforces single use.

- **Applying** the directive sets the password, clears the lockout, and
  rotates the security stamp, which ends all sessions. It also forces a
  password change at the next sign-in through a restricted session that
  allows only self-service operations. It records
  `break-glass-recovery.applied`.
- **Refusals** record `break-glass-recovery.refused` without any change. They
  leave readiness healthy.
- Recovery never grants roles, never logs secrets, and has no API path.

**Concurrency.** Hierarchy and membership tables follow the version-trigger
convention:

- edits (club settings, team, match) require `If-Match`, returning `428` when
  it is missing and `412` when it is stale;
- lifecycle actions are guarded by current state and row locks;
- the last-Club-Admin invariant is serialized through an advisory lock.

**Authorization.** It is decided in Application handlers through
`IAccessAuthorizer` and a pluggable `ITeamScopeResolver`.

**Audit.** `IAuditTrail` writes minimized audit events to an append-protected
table in the same unit of work as each change.

## Technical Context

**Language/Version**: C# on .NET 10, with the SDK pinned by
`src/platform/global.json`. Nullable is enabled and warnings are treated as
errors (`src/platform/Directory.Build.props`).

**Primary Dependencies**:

- ASP.NET Core 10 minimal APIs and the built-in OpenAPI generator
  (`Microsoft.AspNetCore.OpenApi` 10.0.0, already present).
- `Microsoft.Extensions.Identity.Core` 10.0.12. This is new, used in
  Infrastructure only, and verified on the package feed.
- From the persistence foundation:
  - Npgsql 10.0.3, Dapper 2.1.89, and dbup-postgresql 7.0.1;
  - `Aspire.Hosting.PostgreSQL` 13.4.6 in the AppHost.
- The platform spike confirmed the package graph: no `NU1605` conflicts. Any
  directly referenced `Microsoft.Extensions.*` package uses the central floor
  10.0.12, which matches `Microsoft.Extensions.Identity.Core` 10.0.12 and
  `Microsoft.AspNetCore.Mvc.Testing` 10.0.12.
- The confirmed Aspire 13.4.6 API
  `AddParameter(name, new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true)`
  provides the first-admin password. It persists through the AppHost
  `<UserSecretsId>`, which the persistence feature adds.
- No EF Core, no MediatR, no `SignInManager`, no Data Protection key
  persistence, and no `IAntiforgery` (see [research.md](research.md) R1 to R3).

**Storage**: The stamp PostgreSQL database, schema `socalytics`, holds these
new tables:

- `member_account`, `club`, `season`, `team`, and `match` (versioned roots);
- `club_role_assignment` and `team_role_assignment` (children of
  `member_account`);
- `member_session` and `one_time_credential` (unversioned operational
  records);
- `security_audit_event` (append-protected).

They are created by five forward-only migrations named by description (see
[data-model.md](data-model.md#migrations-infrastructure-persistencemigrations)).
No table has a `club_id`.

**Testing**:

- xUnit v3, Shouldly, and NSubstitute.
- `Microsoft.AspNetCore.Mvc.Testing` 10.0.12, added to Integration.Tests if
  the persistence feature has not already added it.
- `Testcontainers.PostgreSql` 4.15.0 through the persistence fixture.
- NetArchTest.Rules for architecture rules, and `Aspire.Hosting.Testing` for
  the host smoke test.
- Tests live in the existing `Architecture.Tests` and `Host.Tests` projects,
  and in `Integration.Tests` (from persistence) under `Club/` and
  `IdentityAccess/`.

**Target Platform**: A cloud-neutral Linux OCI container for the API, as
adopted. Local development runs through the Aspire AppHost on Windows, macOS,
or Linux, with an HTTPS development certificate for interactive sign-in.

**Project Type**: A web service: the platform control-plane API with a
backend-for-frontend session. There are no clients in scope.

**Performance Goals**:

- SC-009: the 95th percentile of sign-in, sign-out, and single-resource read
  and update requests stays under 1 second locally.
- SC-001: the first Club Admin can sign in within 2 minutes of readiness.
- SC-002: a season, team, member, and Coach role can be set up in under 5
  minutes.
- Password hashing uses the Identity PBKDF2 defaults.

**Constraints**:

- Revocation, deactivation, password change, and session termination take
  effect on the next request (FR-039, SC-004).
- No secret is at rest: tokens and credentials are stored as SHA-256 only.
- No secrets appear in audit events, logs, or problem details (FR-046).
- The runtime role has DML only, and `INSERT`/`SELECT` only on the audit table
  and the recovery ledger `recovery_directive_use`.
- No production defaults: identity options are required configuration, and
  development values live in development and test settings only
  (POL-001, POL-002).
- Single club per stamp, with no discriminator.
- Migrations are forward-only.
- The API never runs migrations; bootstrap and break-glass recovery are domain
  data applied by the API at start.

**Scale/Scope**:

- One club per stamp, with tens to low hundreds of members, a few seasons,
  tens of teams per season, and hundreds of matches per season.
- 34 API operations ([contracts/openapi.yaml](contracts/openapi.yaml)), 11
  tables, and 5 migrations.
- Clients, OIDC, MFA enforcement, self-service recovery, and permanent
  deletion are out of scope.

No `NEEDS CLARIFICATION` items remain. Every open choice is resolved in
[research.md](research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-design evaluation

| Principle or constraint | Status | Justification |
| --- | --- | --- |
| I. Architecture is the design authority | PASS | Uses the adopted stack: ASP.NET Core Identity with Dapper and PostgreSQL stores, a BFF session with a Secure/HttpOnly/SameSite cookie and CSRF protection, version triggers, edit-versus-lifecycle HTTP validators, and logical CQRS. Design refinements that the narratives do not yet state (server-side session store, bootstrap location, audit table privileges) are listed under [Required Architecture Updates](#required-architecture-updates) rather than diverging silently. Unresolved production values stay explicit (POL-001, POL-002, POL-009). |
| II. Respect source-area ownership | PASS | All code goes into the existing platform layer projects under `src/platform/`, in the `Club` and `IdentityAccess` folders and namespaces. No new `src/` child and no new production project. Only existing test projects and the persistence-introduced `Integration.Tests` are used. |
| III. API-first control plane | PASS | Every capability is a REST operation under `/api/v1` and is described in OpenAPI 3.1 ([contracts/openapi.yaml](contracts/openapi.yaml)) and the built-in `/openapi/v1.json`. There are no UIs or direct database access, and no video. Bootstrap and break-glass recovery are deployment-configuration actions applied by the API host at start. FR-003 and FR-050 explicitly forbid exposing them as API operations; their effects are observable through the API and the audit trail. |
| IV. Evidence over claims | PASS | Each FR and SC maps to automated tests on real PostgreSQL ([quickstart.md](quickstart.md#2-automated-scenario-checklist)). Host and architecture suites stay green with updated assertions. The readiness disclaimer is kept, and audit output is development evidence only. |
| V. Focused, minimal changes | PASS | One new package (`Microsoft.Extensions.Identity.Core`). No EF Core, Data Protection persistence, MediatR, or extra projects. Contract fields are limited to what the spec requires. The `ITeamScopeSource` extension point is required by later features under the shared conventions. |
| Deferred technologies are introduced only by an adopting feature | PASS | Authentication is listed as deferred, and this feature's spec and plan adopt it. PostgreSQL, Dapper, and DbUp come from the persistence dependency. |
| Environment features (constitution 1.1.0) | PASS | Tasks need only the .NET SDK from `global.json` and Docker for Testcontainers. `environment-setup` provides both (Docker is on the runner). All code is inside `src/platform/`, so `environment-verify` builds and tests it unchanged. CI needs no development certificate: Integration tests use the in-process test server with an `https://localhost` base address, and the Aspire host test uses the API's HTTP endpoint. No separate environment feature is needed, so none is named under Assumptions → Dependencies. |
| Workflow and quality gates | PASS | The supported restore, build, and test commands are unchanged. README, `src/platform/README.md`, and `AGENTS.md` "Current State" are updated at implementation to describe the implemented identity foundation and the bootstrap parameters. Markdown validation is required for documentation changes. Nothing is committed or pushed. |

### Post-design re-evaluation

| Principle or constraint | Status | Justification |
| --- | --- | --- |
| I. Architecture is the design authority | PASS | The data model keeps one schema, the runtime and migration roles, version and child triggers, and no `club_id`. HTTP follows the representation conventions (strong ETags; `If-Match` only on edits; `409` for disallowed transitions). Errors use one structured envelope (RFC 9457 plus `code`, `correlationId`, `errors`). The three refinements are written as exact architecture updates below. |
| II. Respect source-area ownership | PASS | [Project Structure](#project-structure) names only existing projects and folders under `src/platform/`. |
| III. API-first control plane | PASS | 34 operations with `operationId`s, security schemes, and every outcome are documented: authentication, anti-forgery, denial, conflict, version-required, version-mismatch, and password-change-required (FR-043). A contract test ties the runtime document to the design contract. |
| IV. Evidence over claims | PASS | The 57 scenarios in the quickstart cover every acceptance scenario, edge case, and SC. They include concurrency (bootstrap, break-glass recovery, last admin, deactivate versus assign, deactivate versus redeem, archive versus edit) and security evidence: anti-forgery derivation, uniform failures including a timing-distribution test, credential revocation, the forced first password change, secret scanning including temporary credentials, and runtime-role protection of the audit and recovery tables. |
| V. Focused, minimal changes | PASS | 11 tables and 5 migrations, each traceable to an FR. FR-050 reuses the bootstrap lock, the start path, and `changeOwnPassword`, and adds no endpoint. There are no speculative endpoints: no audit read APIs, no member display names, and no opponent registry. |
| Environment features | PASS | Unchanged after design. No new SDK, workload, or tool. |
| Workflow and quality gates | PASS | Unchanged after design. |

No violations, so Complexity Tracking is not required.

## Project Structure

### Documentation (this feature)

```text
specs/20261005-130701-club-identity-foundation/
├── spec.md              # Feature specification (input, unchanged)
├── plan.md              # This file
├── research.md          # Phase 0 decisions (R1–R20)
├── data-model.md        # Phase 1 tables, rules, transitions, audit catalog, cross-feature contracts
├── quickstart.md        # Phase 1 validation guide (automated checklist + manual walk-through)
├── contracts/
│   └── openapi.yaml     # Phase 1 OpenAPI 3.1 design contract (34 operations)
├── checklists/
│   └── requirements.md  # Spec quality checklist (existing)
└── tasks.md             # Phase 2 output (/speckit-tasks; not created here)
```

### Source Code (repository root)

```text
src/platform/
├── Directory.Packages.props                         # + Microsoft.Extensions.Identity.Core 10.0.12
│                                                    #   (+ Microsoft.AspNetCore.Mvc.Testing 10.0.12 if absent)
├── README.md                                        # bootstrap parameters, local sign-in, implemented scope
├── SocAlytics.Platform.Domain/
│   ├── Club/                                        # Club, Season, SeasonState, Team, Match, MatchOpponent,
│   │                                                # MatchDetails, HomeAway, DisplayName
│   └── IdentityAccess/                              # MemberAccount, MembershipStatus, AccountName, ClubRole, TeamRole
├── SocAlytics.Platform.Application/
│   ├── ApplicationServiceCollectionExtensions.cs    # registers handlers, authorizer, scope resolver
│   ├── Abstractions/                                # IRequestContext, IAuditTrail, AuditEvent, AuditOutcome,
│   │                                                # AuditResource, OperationResult<T>, OperationFailure
│   │                                                # (IUnitOfWork/IUnitOfWorkScope come from persistence)
│   ├── Club/                                        # BootstrapClub, UpdateClubSettings, Create/Activate/ArchiveSeason,
│   │                                                # Create/UpdateTeam, Create/UpdateMatch commands; Get/List queries;
│   │                                                # IClubHierarchyStore
│   └── IdentityAccess/                              # IAccessAuthorizer, AccessAuthorizer, ITeamScopeResolver,
│                                                    # TeamScopeResolver, ITeamScopeSource, TeamOwnedResource, TeamScope,
│                                                    # ClubPermission, TeamPermission, AccessDecision;
│                                                    # SignIn, SignOut, ChangeOwnPassword, RedeemCredential, CreateMember,
│                                                    # Deactivate/ReactivateMember, Assign/RevokeClubRole,
│                                                    # Assign/RevokeTeamRole, UnlockMember, IssueCredential,
│                                                    # EndMemberSessions, ApplyBreakGlassRecovery commands;
│                                                    # ValidateSession, GetSession,
│                                                    # GetCurrentMember, GetMember, ListMembers queries;
│                                                    # IAccountCredentialService, ISessionStore, IMemberAccountStore,
│                                                    # IdentityAccessOptions
├── SocAlytics.Platform.Infrastructure/
│   ├── InfrastructureServiceCollectionExtensions.cs # + AddIdentityCore<IdentityMemberAccount>, stores, token provider
│   ├── Persistence/
│   │   ├── Migrations/                              # NNNN_identityaccess_member_accounts.sql
│   │   │                                            # NNNN_club_hierarchy.sql
│   │   │                                            # NNNN_identityaccess_role_assignments.sql
│   │   │                                            # NNNN_identityaccess_sessions_and_credentials.sql
│   │   │                                            # NNNN_identityaccess_security_audit.sql
│   │   ├── AdvisoryLockKeys.cs                      # club-bootstrap, club-admin-invariant
│   │   └── PostgresAuditTrail.cs                    # IAuditTrail implementation
│   ├── Club/                                        # ClubHierarchyStore (Dapper SQL), TeamScopeSource, MatchScopeSource
│   └── IdentityAccess/                              # IdentityMemberAccount, DapperMemberAccountStore (Identity stores),
│                                                    # AccountCredentialService, OneTimeCredentialTokenProvider,
│                                                    # SessionStore, MemberAccountStore, SecretHashing
├── SocAlytics.Platform.Api/
│   ├── Program.cs                                   # authentication scheme, ActiveMember and SessionHolder policies,
│   │                                                # endpoint groups, problem details, bootstrap service,
│   │                                                # readiness check
│   ├── appsettings.Development.json                 # IdentityAccess development values (not production defaults)
│   ├── Security/                                    # SessionAuthenticationHandler, SessionCookie,
│   │                                                # SessionAntiforgeryFilter, AuthorizationPolicyNames,
│   │                                                # HttpRequestContext, MemberApiRouteGroup (MapMemberApi)
│   ├── Http/                                        # IfMatchHeader, ProblemResults, OpenApiSecuritySchemesTransformer
│   ├── Bootstrap/                                   # ClubBootstrapHostedService (bootstrap + break-glass step),
│   │                                                # ClubBootstrapHealthCheck, ClubBootstrapOptions,
│   │                                                # BreakGlassRecoveryOptions (secret; never logged)
│   └── Endpoints/
│       ├── Club/                                    # ClubEndpoints, SeasonEndpoints, TeamEndpoints, MatchEndpoints
│       └── IdentityAccess/                          # SessionEndpoints, SelfEndpoints, CredentialEndpoints, MemberEndpoints
├── SocAlytics.Platform.AppHost/
│   └── Program.cs                                   # + API HTTPS endpoint; bootstrap parameters (generated persisted
│                                                    #   secret password via GenerateParameterDefault, account name,
│                                                    #   club display name) → ClubBootstrap__*; relies on the
│                                                    #   <UserSecretsId> added by the persistence feature
└── Tests/
    ├── SocAlytics.Platform.Architecture.Tests/      # + Identity/persistence visibility rules, anonymous allow list,
    │                                                #   anti-forgery coverage of unsafe endpoints
    ├── SocAlytics.Platform.Host.Tests/              # + readiness incl. club-bootstrap; contract operations in
    │                                                #   /openapi/v1.json (replaces the "zero paths" assertion)
    └── SocAlytics.Platform.Integration.Tests/
        ├── Structure/PersistedTableClassifications.cs # + classifications of this feature's unversioned tables
        ├── Club/                                    # hierarchy, lifecycle, archived read-only, immutability,
        │                                            # edit/lifecycle concurrency, visibility
        └── IdentityAccess/                          # bootstrap (concurrent), break-glass recovery (concurrent,
                                                     # refusals, restricted session), sessions, anti-forgery,
                                                     # lockout, credentials, membership/roles, last Club Admin,
                                                     # authorization matrix, audit evidence and secret scan,
                                                     # runtime-role audit and ledger protection
```

**Structure Decision**: The feature uses the existing layered platform
solution `src/platform/SocAlytics.Platform.slnx`. Functional areas `Club` and
`IdentityAccess` are folders and namespaces inside Domain, Application,
Infrastructure, and Api. They are not projects. Shared plumbing lives in
`Application/Abstractions` and `Infrastructure/Persistence`. The Identity user
type, Dapper stores, and SQL stay internal to Infrastructure behind the single
`AddInfrastructure(...)` composition method. The session handler, anti-forgery
filter, endpoint groups, and bootstrap hosting live in the API, which is the
composition root and BFF. No production or test project is added beyond those
fixed by the shared conventions.

### Interfaces Other Features Consume

| Name | Location | Use by later features |
| --- | --- | --- |
| `IRequestContext` | `SocAlytics.Platform.Application.Abstractions` | Current member account id, session id, and correlation id in handlers |
| `IAuditTrail` (`RecordAsync`, `RecordIndependentAsync`), `AuditEvent`, `AuditOutcome`, `AuditResource` | `SocAlytics.Platform.Application.Abstractions` | Audit events committed with each change, and independent denials |
| `ITeamScopeResolver`, `ITeamScopeSource`, `TeamOwnedResource`, `TeamScope` | `SocAlytics.Platform.Application.IdentityAccess` | Resolve recordings, runs, and agent evidence to their owning team by registering one `ITeamScopeSource` per resource kind |
| `IAccessAuthorizer`, `ClubPermission` (`Administer`, `Register`), `TeamPermission` (`Read`, `Write`), `AccessDecision` | `SocAlytics.Platform.Application.IdentityAccess` | Team-scoped and club-role authorization with built-in denial audit; `ClubPermission.Register` serves Analyst Manager registration |
| `OperationResult<T>`, `OperationFailure` | `SocAlytics.Platform.Application.Abstractions` | Uniform handler outcomes mapped to problem details |
| `SocAlyticsSession` scheme, `AuthorizationPolicyNames.ActiveMember`, `AuthorizationPolicyNames.SessionHolder`, `SessionAntiforgeryFilter`, `MapMemberApi`, `IfMatchHeader`, `ProblemResults` | `SocAlytics.Platform.Api` (internal) | Mapping new `/api/v1` endpoints with the same session, anti-forgery, concurrency, and error behavior; new endpoints use `ActiveMember` |
| Cookie `__Host-socalytics-session`, header `X-CSRF-Token`, problem types `urn:socalytics:problem:<code>` | HTTP contract | Shared transport conventions; this feature is their canonical owner |
| Tables `team`, `match`, `member_account` | `socalytics` schema | Foreign-key targets for recordings (`match_id`) and audit actor references |

This feature is the canonical owner of every name in this table, as recorded in
the risk-resolution brief. Other features reuse these names and do not
redefine them.

## Required Architecture Updates

The coordinator applies these updates. Updates 1 and 2 record design decisions
and are applied with this plan. Update 3 records implementation evidence and is
applied only when the implementation is merged.

### 1. Server-side sessions, credentials, and bootstrap

**File**: `docs/architecture/platform-implementation.md`

**Section**: `## API And Identity`

**Placement**: Insert a new paragraph after the paragraph that begins "Local
and external login paths establish the same ASP.NET Core backend-for-frontend
session."

> The BFF session is server-validated. The cookie carries only an opaque random
> session token, and the database stores its hash together with idle and
> absolute expiry and the account's security stamp at issue. A per-session
> anti-forgery token, which clients send in a request header on every
> state-changing request, is derived from the session token with a keyed hash
> and is never stored. Every request revalidates the session, the
> account's active membership, and its security stamp, and reads current
> roles, so sign-out, session termination, password changes, deactivation, and
> role revocation take effect on the affected member's next request. No session
> or anti-forgery secret is stored in recoverable form, and the API needs no
> shared key ring across instances. Sign-in failures are indistinguishable in
> content and timing. Single-use, time-limited set-password and
> reset credentials are issued by a Club Admin through an ASP.NET Core Identity
> token provider and stored only as hashes. They are revoked, with an audit
> event, when the issuer loses Club Admin authority or the target is
> deactivated. The club and its first Club Admin
> are established from protected deployment configuration by the API, not the
> Migrator. The first Club Admin must change the configured password at first
> sign-in, and bootstrap no longer needs it once the club exists. The API
> serializes this one-time bootstrap with a database
> transaction lock and uniqueness constraints, so concurrently starting API
> instances create exactly one club and administrator, and an instance reports
> not ready while no club is established or the configuration conflicts with
> it. A club whose only Club Admin can no longer sign in recovers through a
> break-glass directive in protected deployment configuration. The directive
> names an existing active account, a single-use recovery identifier, and an
> operator-supplied temporary credential. The API applies it at start under
> the same lock, at most once per identifier. Applying it sets the credential,
> ends the account's sessions, and requires a password change at the next
> sign-in. It grants no roles and is audited. The platform never writes the
> credential to logs or diagnostics, and no API operation performs recovery.

### 2. Append-protected security audit table and recovery ledger

**File**: `docs/architecture/platform-implementation.md`

**Section**: `### Planned Data Organization`

**Placement**: Append to the end of the first paragraph, which ends "...but
contains no domain state."

> The exceptions to uniform runtime access are the security audit table and
> the break-glass recovery ledger: the runtime role may only insert and read
> them. For the audit table, a trigger also rejects updates, deletes, and
> truncation, so audit evidence is append-protected and kept apart from
> application logs, and a used recovery identifier can never be made reusable.
> The audit table is development audit evidence; the production audit store,
> integrity verification, and retention remain governed by POL-009 in Security
> and Data Governance.

In the architecture file, "Security and Data Governance" links to
`security-and-data-governance.md#audit-events`.

### 3. Current-evidence statements (apply at implementation merge)

**File**: `docs/architecture/platform-implementation.md`

**Section**: `## API And Identity`, first paragraph

**Change**: Replace the paragraph that begins "The current dependency-free API
implements only the operational and OpenAPI surface..." with:

> The current API implements local accounts, the server-validated BFF session,
> anti-forgery protection, membership, club and team roles, team-scoped
> authorization, security audit events, and the `Club > Season > Team > Match`
> hierarchy, as specified in
> `specs/20261005-130701-club-identity-foundation`. It implements no external
> OpenID Connect sign-in, MFA enforcement, generated client, or Analyst Manager
> identity. Exposure and access policy for operational endpoints in a
> production ingress remain unresolved.

**Section**: `## Control Plane`, first paragraph

**Change**: Replace "it has no domain paths" with:

> its domain paths are limited to the club hierarchy, accounts, sessions,
> membership, and roles under `/api/v1`

## Risk Register

| ID | Risk | Disposition | Evidence / Owner | Revisit trigger |
| --- | --- | --- | --- | --- |
| CI-R1 | Persistence-name drift: this plan depends on names the parallel persistence plan defines (`IUnitOfWork`, triggers, structural classifications, default privileges, test fixture) | Mitigated | Canonical names from the risk-resolution brief are used throughout: `IUnitOfWork.BeginAsync` → `IUnitOfWorkScope` (`CommitAsync`, `RollbackAsync`), internal `IDbSession`, `VersionedWriteResult` (`Applied`, `NotFound`, `ConcurrencyConflict`), `socalytics.advance_version()`, `socalytics.touch_aggregate_root()`, `socalytics.attach_version_trigger(regclass)`, `socalytics.attach_aggregate_child_triggers(regclass, regclass, name, name)`, `Structure/PersistedTableClassifications.cs`. See [data-model.md](data-model.md#migrations-infrastructure-persistencemigrations). Owner: club-identity plan | The persistence plan or contracts rename any of these items |
| CI-R2 | Shared types redefined by parallel plans (`OperationResult<T>`, `OperationFailure`, `IRequestContext`, `IAuditTrail`, `ITeamScopeResolver`, `IAccessAuthorizer`, cookie and header names, problem URN scheme) | Mitigated | This feature is the canonical owner, recorded in the brief's canonical-names list and in [Interfaces Other Features Consume](#interfaces-other-features-consume). Owner: club-identity plan | Another plan's `/speckit-analyze` reports a parallel definition |
| CI-R3 | Custom security code: the session authentication handler and anti-forgery filter (about 200 lines) could contain defects | Mitigated by design review | The coordinator's security design review (2026-10-07) raised three medium findings and one low finding, all fixed in CI-R8 to CI-R11: credential revocation, the forced bootstrap password change, timing equalization, and a stateless HMAC-derived anti-forgery token. Code-level evidence comes from the architecture tests (every unsafe endpoint carries the filter, and every endpoint is authenticated unless allow-listed), the authorization matrix (A38), and the anti-forgery tests (A8, A57). Owner: security design review (coordinator) | The session or anti-forgery design changes, or before production promotion (an independent code review is still required) |
| CI-R4 | Aspire generated persisted secret for the first-admin password does not persist and is silently regenerated each run | Mitigated | Platform spike A1(b): `AddParameter(name, new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true)` returned the same value across three runs once the AppHost csproj had `<UserSecretsId>`, which the persistence feature adds. Without it, a new value is generated with no warning. Owner: persistence feature (`UserSecretsId`) and club-identity (parameter) | The Aspire version changes, or `<UserSecretsId>` is removed from the AppHost |
| CI-R5 | Sole Club Admin lockout: the only Club Admin loses their password or is locked out, and no admin can issue a reset | Mitigated | FR-050 break-glass recovery directive applied at API start ([research.md](research.md) R19 and R20; [data-model.md](data-model.md#recovery_directive_use); scenarios A41 to A48). Owner: club-identity plan | The production recovery policy (POL-002 / GOV-CRED-009) adds requirements such as dual control |
| CI-R6 | Production values unresolved: session lifetimes, lockout, password policy, credential lifetimes, retention of sessions, credentials, recovery ledger, and audit events, audit integrity verification, and encryption at rest | Deferred | Owners: POL-001, POL-002, POL-009 approvers (with GOV-CRED-001, GOV-CRED-009, GOV-AUD). Identity options are required configuration with development values only (R8) | Any production-promotion activity, or approval of the named policies |
| CI-R7 | Readiness coupling: without bootstrap configuration, or with conflicting configuration, the stamp never becomes ready | Accepted | Intended fail-visible behavior (R7). Recovery refusals never affect readiness. Operator documentation must state it. Owner: club-identity plan (documentation in `src/platform/README.md`) | Production operations define a different readiness contract |
| CI-R8 | Security review finding 1 (medium): unused one-time credentials survive revocation of their issuer or deactivation of their target | Mitigated | FR-052. Club Admin revocation or deactivation revokes all open credentials with `issued_by_account_id` = that member (`ix_one_time_credential_issuer_open`). Deactivation also revokes the member's own credentials. Redemption requires `membership_status = 'active'` under the account row lock. Each revocation records `credential.revoked`. See [research.md](research.md) R5 and R11, [data-model.md](data-model.md#one_time_credential), and scenarios A51 to A54. Owner: club-identity plan | A new path grants credential-issuing authority |
| CI-R9 | Security review finding 2 (medium): the bootstrap initial password stays a working credential, and bootstrap keeps requiring it in configuration | Mitigated | FR-051. The bootstrap administrator is created with `password_change_required = true`, which yields the restricted session of R20. Bootstrap never reads `InitialPassword` once the club exists, and the operator removes it after the first sign-in. See R7, and scenarios A1, A49, and A50. Owner: club-identity plan | The bootstrap flow changes |
| CI-R10 | Security review finding 3 (medium): sign-in response time reveals locked, inactive, or password-less accounts | Mitigated | FR-018 and SC-005. Exactly one hash verification runs on every path, using the stored hash or a dummy hash, before any refusal decision. The counter is not incremented while locked. See R6, and scenarios A9 (hash-call count), A55 (Kolmogorov–Smirnov timing distribution), and A56. Owner: club-identity plan | The password hasher, its iteration count, or the sign-in flow changes |
| CI-R11 | Security review finding 4 (low): the stored anti-forgery hash contradicted the stateless design and added stored token material | Mitigated | `HMAC-SHA256(key = raw session token, "socalytics-csrf")` is recomputed per request and compared in fixed time; there is no column (R3, [data-model.md](data-model.md#member_session), scenario A57). Owner: club-identity plan | The session token format changes |
