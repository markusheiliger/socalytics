# 0001: Platform Persistence Boundary And PostgreSQL Role Isolation

## Status

Accepted as the implementation target for the active Platform Persistence
Foundation change. It does not establish production identities, credentials, or
deployment configuration.

## Context

The executable platform has six peer capability projects with public
composition boundaries, but no persistence implementation. The established
platform baseline selects one PostgreSQL database per single-club stamp,
module-owned schemas, Npgsql, Dapper, and DbUp. The persistence foundation
must add reusable migration, connection, transaction, and concurrency behavior
without transferring module SQL or domain ownership to a shared project.

Normal module access also requires database-enforced isolation. Naming
conventions and architecture tests alone cannot prevent a module from reading
or writing a peer schema at runtime.

## Decision

Add `SocAlytics.Platform.Persistence` as a peer, module-neutral platform
boundary. It owns PostgreSQL connection and transaction infrastructure,
checksum-aware migration orchestration, the `socalytics_migrations` schema,
and a narrow optimistic-concurrency primitive. It owns neither a capability
schema nor domain records, SQL, migrations, or repositories.

Each capability continues to own its adopted schema, SQL, and embedded
migrations. Capability registration binds a fixed module identity; the API
composes public module registrations and the shared boundary without consuming
module persistence implementations.

For local and test databases, bootstrap creates a stable NOLOGIN owner role
and runtime role for each adopted module. An owner role owns only its module
schema and migration objects. A runtime role has only the usage and object
privileges required for its module schema; public and peer-schema privileges
are revoked. Migration execution uses the owner role, while normal module
sessions use the runtime role. Bootstrap access remains restricted to migration
orchestration and is unavailable to module application services.

## Consequences

The implementation must keep capability schemas and migrations internal,
deterministically orchestrate their migrations, and prove cross-schema denial
against PostgreSQL. The shared boundary may not become a generic repository or
domain-model layer.

This decision defines local and test privilege semantics only. Production
identity mapping, login names, credentials, secret delivery, deployment
configuration, and operational policy remain unresolved.

## References

- Active OpenSpec change:
  [add-platform-persistence-foundation](../../../openspec/changes/add-platform-persistence-foundation/proposal.md)
- Affected accepted behavior:
  [platform host specification](../../../openspec/specs/platform-host/spec.md)
- Active persistence behavior delta:
  [platform persistence specification](../../../openspec/changes/add-platform-persistence-foundation/specs/platform-persistence/spec.md)
- Governing architecture:
  [Platform Implementation Profile](../platform-implementation.md) and
  [Tenancy and Technology](../tenancy-and-technology.md)
