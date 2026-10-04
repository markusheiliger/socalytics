# Add Platform Persistence Foundation Proposal

## Why

The executable platform host has no database composition or persistence primitives, so modules cannot yet prove the adopted PostgreSQL, Npgsql, Dapper, DbUp, transaction, concurrency, or schema-isolation architecture. This change establishes that shared foundation before domain models make persistence boundaries harder to introduce or verify.

## What Changes

- **BREAKING**: make Aspire-hosted PostgreSQL a required local-development resource for the platform API and its readiness path, replacing the dependency-free local host behavior.
- Add a shared platform persistence boundary for connection creation, explicit transaction execution, optimistic-concurrency primitives, and migration orchestration that owns no domain schema or domain records.
- Integrate Npgsql and Dapper for database access and DbUp for ordered, module-grouped PostgreSQL migrations.
- Add checksum-aware shared migration history in `socalytics_migrations.history`, including deterministic repeat-startup behavior and failure on conflicting checksums.
- Keep SQL, migration scripts, schema creation, and persistence implementations internal to the owning Club, Identity Access, Recordings, Registry, Analysis, or Agent Orchestration module.
- Add architecture and Testcontainers-backed PostgreSQL evidence for ordered migrations, checksum conflicts, repeat startup, rollback behavior, module schema ownership, public-surface isolation, cross-schema access prohibition, explicit transactions, optimistic concurrency, and the absence of `club_id` from the single-club stamp persistence model.
- Update platform development and architecture documentation to distinguish the implemented local persistence foundation from deferred domain behavior and production readiness.
- Do not add domain entities, domain tables beyond migration/schema test fixtures, NATS, outbox publication, S3-compatible storage, authentication or authorization, production deployment configuration, or unresolved production values.

## Capabilities

### New Capabilities

- `platform-persistence`: Defines observable local PostgreSQL composition, migration safety, transaction and concurrency behavior, module schema ownership, persistence encapsulation, and PostgreSQL integration evidence.

### Modified Capabilities

- `platform-host`: Changes local composition and readiness from dependency-free startup to an Aspire-managed PostgreSQL dependency while preserving the existing operational and OpenAPI surface.

## Impact

- Affected product areas: `src/platform` AppHost, API composition, six capability projects, the solution and central package catalog, platform architecture tests, a new PostgreSQL integration-test project, and a shared peer persistence project.
- New development dependencies: Aspire PostgreSQL hosting/client integration, Npgsql, Dapper, DbUp PostgreSQL support, and PostgreSQL Testcontainers, with exact package versions managed centrally during implementation.
- Affected documentation: root/platform development commands and the current architecture narratives in `docs/architecture/platform-implementation.md` and `docs/architecture/tenancy-and-technology.md`.
- No domain or external API endpoints are added. The API continues to expose only its existing health and OpenAPI behavior.
- Production host, port, database name, credentials, secret source, role-to-identity mapping, backup/restore, encryption, retention, capacity, recovery objectives, and deployment values remain unresolved and out of scope; this change must not select or imply them.
- The shared-project name and exact internal registration contracts may be refined during implementation if the resulting public surface and dependency direction continue to satisfy the approved capability requirements.
