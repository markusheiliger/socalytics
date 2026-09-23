# Add Club and Identity Foundation Proposal

## Why

The executable platform has no authenticated domain behavior, so it cannot yet enforce the adopted single-club hierarchy, team-scoped access, or secure human sessions. This change establishes the first authenticated Club and Identity Access slice on the planned persistence foundation and makes its security boundaries observable through API and test evidence.

## What Changes

- Implement the singleton Club root and required `Club > Season > Team > Match` hierarchy in the Club module without a `club_id` discriminator.
- Implement platform-managed local accounts with ASP.NET Core Identity backed by IdentityAccess-owned Dapper/PostgreSQL stores.
- Implement club membership, Club Admin and Registrar capabilities, team-scoped grants, revocation, and authorization that resolves every protected domain resource to its owning Team.
- Establish secure ASP.NET Core backend-for-frontend sessions using Secure, HttpOnly, SameSite cookies, CSRF protection for state-changing browser requests, and no browser bearer-token storage.
- Expose OpenAPI-visible domain operations for authentication, account recovery, Club hierarchy administration, membership, capability, and team-grant workflows, with authorization enforced by application handlers rather than documentation alone.
- Cover login, logout, password reset, lockout, MFA readiness, optimistic concurrency, and minimized authentication, authorization, grant, membership, and administrative audit records.
- Add focused host, authorization, disposable-PostgreSQL, and architecture tests for successful paths, denial paths, stale writes, revocation, cross-Team access, schema ownership, public boundaries, and the absence of `club_id` from internal records.
- Depend on the active `add-platform-persistence-foundation` change for shared database composition, migration orchestration, module-scoped Dapper access, explicit transactions, optimistic-concurrency signaling, and PostgreSQL test infrastructure. This change does not modify or duplicate that change's artifacts.
- Synchronize current platform, tenancy, and security architecture narratives with the implemented evidence while preserving unresolved production policy values.
- Keep optional external OpenID Connect, Analyst Manager pairing, client applications, and production ingress configuration out of scope.

## Capabilities

### New Capabilities

- `club-domain`: Defines the singleton Club root, Season-Team-Match hierarchy, OpenAPI-visible domain operations, optimistic concurrency, and Team-scope resolution.
- `identity-access`: Defines local account lifecycle, secure BFF sessions, club membership, administrative capabilities, team grants, authorization enforcement, and security audit behavior.

### Modified Capabilities

None.

## Impact

- Affected product areas: `SocAlytics.Platform.Club`, `SocAlytics.Platform.IdentityAccess`, the API composition surface, their module-owned PostgreSQL migrations and Dapper stores, and platform host, PostgreSQL, authorization, and architecture tests.
- New dependencies are limited to ASP.NET Core Identity support required by the existing .NET 10 shared framework; persistence packages and Testcontainers infrastructure are supplied by `add-platform-persistence-foundation` and must not be independently reintroduced.
- Affected API surface: versioned JSON operations for local authentication and recovery, Club hierarchy management, membership, capabilities, and team grants, all represented in the built-in OpenAPI document.
- Affected documentation: `docs/architecture/platform-implementation.md`, `docs/architecture/tenancy-and-technology.md`, `docs/architecture/security-and-data-governance.md`, repository/platform development guidance, and component-local executable contract guidance justified during apply.
- Security and governance impact: implements part of BND-001, CTL-002, CTL-009, DAT-001, DAT-009, POL-001, POL-009, and THR-003 as development evidence only. Unresolved production retention, audit authority/store, cookie lifetime, lockout thresholds, MFA factors and enrollment policy, recovery delivery channel, cryptographic key custody, and production ingress/TLS values remain open and must not be presented as production-approved defaults.
- The active persistence change overlaps in module schemas, migrations, concurrency, tests, and host readiness. Its foundation must be applied first or rebased into equivalent accepted evidence before this change is implemented; Club and IdentityAccess domain tables, stores, and SQL remain owned by this change.
