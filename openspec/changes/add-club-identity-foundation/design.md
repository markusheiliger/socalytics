# Club and Identity Foundation Design

## Context

See `proposal.md` for motivation and the delta specs for required behavior. The current executable host composes empty Club and IdentityAccess module boundaries and exposes only operational endpoints and OpenAPI. The active `add-platform-persistence-foundation` change plans the PostgreSQL resource, shared migrations, module-scoped Dapper sessions, explicit transactions, optimistic-concurrency primitive, schema isolation, and disposable-PostgreSQL test fixture but has no completed implementation tasks.

The adopted architecture fixes one implicit Club per stamp, `Club > Season > Team > Match`, module-owned `club` and `identity_access` schemas, ASP.NET Core Identity with Dapper stores, secure BFF cookies, CSRF defense, and server-side Team authorization. This design supplies domain behavior only after that foundation exists and does not modify the active change.

Impact categories:

- **UX:** No client is added. HTTP behavior supports future browser clients, including non-redirecting API authentication outcomes, anti-forgery acquisition, and generic recovery responses.
- **Architecture:** Club owns hierarchy state and Team-scope resolution; IdentityAccess owns accounts, membership, capabilities, Team grants, sessions, and authorization decisions. Neither capability references the other directly.
- **Security and governance:** Implements development evidence for BND-001 and portions of CTL-002/CTL-009 while leaving production policy values and independent audit authority unresolved.
- **Implementation:** Adds module-owned migrations, internal handlers and Dapper stores, narrow public endpoint-mapping/composition boundaries, and focused host, authorization, PostgreSQL, and architecture tests.
- **Documentation:** Apply must synchronize `docs/architecture/platform-implementation.md`, `docs/architecture/tenancy-and-technology.md`, `docs/architecture/security-and-data-governance.md`, root/platform development guidance, and any component-local executable contracts.

## Goals / Non-Goals

**Goals:**

- Preserve capability isolation while making Team-scope resolution available to host-level authorization composition through narrow contracts.
- Use ASP.NET Core Identity behavior without introducing Entity Framework Core or allowing Identity persistence types into the API host.
- Re-evaluate current authorization state on protected requests so revocation takes effect without waiting for cookie expiry.
- Keep domain, authorization, audit, and concurrency changes transactional and testable against PostgreSQL.
- Publish a truthful OpenAPI surface and executable denial-path evidence.

**Non-Goals:**

- External OpenID Connect, Analyst Manager pairing or credentials, clients, email/SMS delivery, and production ingress/TLS configuration.
- Selecting production cookie lifetime, lockout thresholds, password policy values, MFA factors, recovery channel, audit retention, audit authority/store, encryption custody, or lifecycle policy.
- Cross-club tenancy, `club_id`, generic role frameworks, direct cross-module SQL, or a shared domain model.

## Decisions

### 1. Build on the active persistence foundation

Implementation begins only after `add-platform-persistence-foundation` supplies equivalent accepted and tested connection, migration, transaction, concurrency, role-isolation, readiness, and PostgreSQL-test behavior. Club and IdentityAccess add migrations to their existing module contributors and use the shared contracts; they do not add another persistence project, migration journal, connection factory, or container fixture.

If both changes are applied concurrently, persistence tasks that create schema-only first migrations must land before these domain migrations, and the domain migration sequences must be additive. This dependency is implementation ordering, not permission to edit the other change's planning artifacts.

Alternative considered: duplicate temporary persistence infrastructure here. Rejected because it would create competing contracts and invalidate the active foundation's schema and role design.

### 2. Keep hierarchy and authorization ownership separate

Club owns the singleton Club, Season, Team, Match, their SQL, validation, concurrency, and an internal Team-scope resolver. IdentityAccess owns local Identity records, club membership, capabilities, Team grants, sessions, and audit records for its actions. The API composes narrow public registration and endpoint-mapping boundaries from both modules; capability projects remain peers and never reference one another.

Authorization uses host-owned policy composition over narrow interfaces registered by the modules. The resource loader asks Club to resolve an opaque domain resource reference to Team scope, then asks IdentityAccess to evaluate current membership/capability/grant state. Application handlers repeat authorization at the state-change boundary to avoid endpoint-only enforcement and time-of-check/time-of-use gaps.

Alternative considered: IdentityAccess queries Club tables or Club queries grant tables. Rejected because direct cross-schema reads violate module ownership and make extraction and testing harder.

This cross-module policy composition is a durable refinement but follows the existing modular-monolith and public composition architecture; it is an **ADR candidate** only if apply reveals a new public application-contract assembly or changes the established capability dependency rule.

### 3. Model the single Club and hierarchy relationally

The `club` schema uses one singleton Club row protected by a database uniqueness/check invariant, with Season referencing the implicit root conceptually but carrying no `club_id`; Team requires a Season key, and Match requires a Team key. Stable opaque identifiers and `BIGINT` versions support URLs and optimistic concurrency. Foreign keys prevent orphans, and destructive parent behavior is restrictive until a separately approved lifecycle change defines deletion semantics.

Club initialization is an authenticated Club Admin operation after identity bootstrap. All hierarchy writes require expected versions and explicit transactions. Registrar and Club Admin can create/update hierarchy data; only Club Admin controls privileges.

Alternative considered: encode the hierarchy in authorization claims. Rejected because claims become stale, do not enforce relational ownership, and cannot provide authoritative Team resolution.

### 4. Adapt ASP.NET Core Identity to Dapper stores

IdentityAccess implements internal Dapper stores for users, passwords, normalized identifiers, security stamps, lockout state, reset tokens, MFA-ready flags and tokens, club membership, capabilities, and Team grants in `identity_access`. Identity managers remain the account-policy boundary; handlers do not reproduce password hashing or token semantics.

Account identifiers are normalized and unique according to configured Identity behavior. Public login and reset responses are generic to resist account enumeration. Reset completion rotates the security stamp. MFA-ready means the stores and session flow preserve ASP.NET Core Identity's factor/enrollment state and can return an additional-factor-required outcome later; this change neither selects a factor nor claims MFA enforcement.

Alternative considered: custom password and recovery implementation. Rejected because the adopted architecture explicitly selects ASP.NET Core Identity and its reviewed primitives.

### 5. Bootstrap the first administrator outside the public API

The host accepts a protected, deployment-supplied bootstrap descriptor through configuration/secrets, creates or reconciles exactly one initial local account, active membership, and Club Admin capability transactionally, and records a minimized bootstrap audit event. Bootstrap contains no default credential, never logs its secret, is idempotent for the same identity, fails closed on conflicting identity, and is ignored or rejected after an administrator exists according to the executable contract.

Development and tests may provide ephemeral values. Production secret source, values, rotation, and operator workflow remain production-profile decisions; absence of approved values blocks production rather than enabling an anonymous setup endpoint.

Alternatives considered: an unauthenticated first-user endpoint, rejected because exposure timing and race behavior create privilege escalation; committed default credentials, rejected because they are inherently unsafe.

### 6. Use server-validated cookies and current authorization state

Cookie authentication uses Secure, HttpOnly, SameSite cookies and API-style 401/403 responses. Security-stamp validation and a server-side session record allow logout, reset, membership revocation, and administrative session revocation to stop authorization. The cookie contains identity/session correlation only, not durable Team grants or Club capabilities; current membership and authorization state are read for every protected request or through a bounded cache invalidated transactionally on change.

Cookie-authenticated unsafe HTTP methods require ASP.NET Core anti-forgery validation. A protected anti-forgery acquisition operation returns only the request token while the framework manages its paired cookie. Login and reset completion receive explicit origin/anti-forgery treatment in the executable contract and host tests; no operation relies on SameSite alone.

Alternative considered: place Team grants in long-lived cookie claims. Rejected because revoked and cross-Team grants must fail immediately and stale claims cannot be authoritative.

### 7. Represent capabilities and Team grants explicitly

IdentityAccess stores explicit Club Admin and Registrar capability assignments plus versioned Team grants. Club Admin implies Registrar during policy evaluation without duplicating a second assignment. Club Admin manages membership, capabilities, and grants; Registrar manages Season, Team, and Match registration data but cannot change privileges. Team grants carry a bounded operation set selected by the endpoint policy rather than unrestricted role strings.

Every authorization decision starts with active membership, then permits Club Admin for Club-wide operations or checks the required capability/grant against the resource's authoritative Team. Unknown resources, missing scope, stale versions, revoked membership, revoked grants, and Team mismatch fail closed.

Alternative considered: generic string roles attached only to users. Rejected because they cannot distinguish Club-wide capabilities from resource-scoped authority or make Team mismatch explicit.

### 8. Commit minimized audit evidence with security state

IdentityAccess owns initial audit tables for authentication, session, membership, capability, Team-grant, and authorization events. Club records hierarchy administration through the same narrow audit contract without direct table access. State changes and their audit records share an explicit PostgreSQL transaction when they are in one module; cross-module requests use correlation identifiers and each owner records its own outcome without a distributed transaction.

Audit payloads use typed columns and bounded reason metadata, never free-form request serialization. Passwords, cookies, reset and anti-forgery tokens, security stamps, and bearer tokens are prohibited. This is application audit evidence, not resolution of the independent production audit authority, retention, integrity store, export, hold, backup, or redaction decisions in GOV-AUD/POL-009.

Alternative considered: ordinary structured logs as the audit store. Rejected because logs do not provide the required transactional, access-controlled security record.

### 9. Map versioned endpoint groups and OpenAPI metadata

Club and IdentityAccess expose narrow endpoint-mapping boundaries called by the API host after module registration. Routes are grouped under the existing versioned API description and return problem details for validation, unauthenticated, forbidden, not found, conflict, and concurrency outcomes. Endpoint metadata declares cookie authentication, anti-forgery requirements, and response schemas; handlers remain the enforcement boundary.

The API host orchestrates contracts but does not consume internal entities, stores, SQL, or migration types. Architecture tests update the allowed public surfaces deliberately rather than weakening the existing export checks.

Alternative considered: define all endpoints directly in `Program.cs`. Rejected because it would expose module implementation concepts and concentrate domain behavior in the host.

### 10. Separate host, authorization, PostgreSQL, and architecture evidence

Host tests start the complete Aspire composition, bootstrap an administrator with ephemeral configuration, validate cookie attributes, anti-forgery behavior, login/logout/reset/lockout outcomes, and inspect OpenAPI operations and security metadata. Authorization tests exercise policy and handler boundaries for Admin, Registrar, matching Team grant, revoked membership/grant, Team mismatch, unknown resource, and current-state re-evaluation.

PostgreSQL tests use the persistence foundation's disposable compatible engine to verify migrations, Dapper Identity stores, singleton Club enforcement, hierarchy foreign keys, atomic writes/audits, optimistic concurrency, session invalidation, and catalog-level absence of `club_id`. Architecture tests enforce capability isolation, schema/SQL ownership, API access through approved composition and endpoint boundaries, no public persistence types, no EF Core dependency, and no `club_id` token in internal records.

Security tests use synthetic credentials and assert diagnostics/audits contain no secrets. Numeric policy values remain test configuration, not production defaults.

## Risks / Trade-offs

- **[The active persistence change is not implemented]** -> Gate apply on its accepted equivalent behavior and sequence domain migrations after its schema migrations.
- **[Per-request authorization reads add database work]** -> Start with authoritative reads; introduce only measured, bounded, transactionally invalidated caching that preserves immediate revocation semantics.
- **[Cookie and anti-forgery settings depend on ingress behavior]** -> Test framework behavior locally, keep production ingress/TLS configuration out of scope, and retain production promotion as blocked.
- **[Dapper Identity stores can miss framework semantics]** -> Build store-contract and end-to-end tests for normalization, stamps, lockout, reset tokens, and MFA-ready state against PostgreSQL.
- **[Bootstrap configuration can become persistent privileged material]** -> Require protected configuration, redact diagnostics, make provisioning idempotent, and document that production secret lifecycle remains unresolved.
- **[Application audit records do not satisfy independent production audit governance]** -> Label them development evidence and preserve GOV-AUD/POL-009 blockers in synchronized architecture.
- **[Registrar permissions may broaden later]** -> Keep its first authority limited to hierarchy registration data and require a future spec change for additional operations.

## Migration Plan

1. Confirm `add-platform-persistence-foundation` behavior is implemented or available on the apply branch; do not recreate it.
2. Add additive Club and IdentityAccess migrations and PostgreSQL tests, then internal Dapper stores and transactional handlers.
3. Add bootstrap, Identity managers, session/CSRF composition, current-state authorization, and focused authorization tests.
4. Add endpoint mappings and OpenAPI metadata, then extend full AppHost tests for authentication and denial paths.
5. Extend architecture tests, synchronize current architecture and development guidance, resolve the conditional ADR candidate, and run complete restore/build/test/OpenSpec validation plus independent verification and security audit.

Application rollback removes endpoint/session/bootstrap wiring and returns the host to its prior surface. Applied additive tables are not automatically dropped; a correction uses forward migrations, and developer/test databases may be explicitly reset. Rollback must not restore a revoked session or privilege, and production deployment remains blocked until its separate policy and operational decisions are approved.
