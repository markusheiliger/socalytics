# 0001. Shared Platform Persistence Boundary And Module Database Roles

## Status

Accepted as the implementation target of the active
`add-platform-persistence-foundation` OpenSpec change. Implementation evidence
is pending; this record does not claim that the boundary, roles, or tests
exist yet.

## Context

The implemented platform host consists of peer projects under `src/platform`.
The six capability projects (Club, Identity Access, Recordings, Registry,
Analysis, and Agent Orchestration) expose one public composition boundary each
and reference no other project. The adopted architecture already selects one
PostgreSQL database per single-club stamp, Npgsql, Dapper, DbUp, explicit
transactions, optimistic concurrency, module-owned schemas, and shared
`socalytics_migrations.history`.

Implementing that persistence baseline changes the implemented architecture in
two durable ways that the current narratives do not yet explain:

- every capability project gains a dependency on shared persistence
  infrastructure, which changes the established project-reference graph; and
- module schema isolation becomes a database-enforced security boundary rather
  than a naming and code-review convention.

Both choices constrain all later domain work, so their rationale meets the
[architecture decision threshold](README.md) for a consequential change to an
implemented architecture.

## Decision

- Add one peer production project, `SocAlytics.Platform.Persistence`, that owns
  only module-neutral infrastructure: Npgsql connection creation, Dapper
  support, explicit transaction execution, affected-row optimistic-concurrency
  signaling, migration descriptors, SHA-256 checksum verification, the shared
  `socalytics_migrations` journal, and migration orchestration. It owns no
  capability schema, domain SQL, or domain record.
- Each capability project may reference the persistence project and still
  references no other capability. SQL, embedded migrations, schema creation,
  and persistence implementations remain internal to the owning capability,
  which registers them through its existing public composition method. The API
  may invoke the shared registration and startup boundary but must not consume
  module persistence implementations.
- Each adopted module is bound to a fixed module identity and schema. For local
  and test databases, shared bootstrap creates a NOLOGIN owner role and a
  NOLOGIN runtime role per module. Migrations run as the owning module's owner
  role; normal module sessions run as its runtime role, which has access only
  to its own schema. Public and peer-module privileges are revoked, so
  PostgreSQL rejects cross-schema reads and writes.
- The bootstrap connection is restricted to migration orchestration and is not
  available to module application services.

This decision establishes privilege semantics only. Production login
identities, role-to-identity mapping, credential delivery, secret sources,
hosts, ports, and database names remain unresolved and are governed by
[Production Deployment and Operations](../production-operations.md) and
[Security and Data Governance](../security-and-data-governance.md).

## Consequences

- Connection, transaction, concurrency, checksum, and migration behavior is
  implemented once, so modules cannot diverge on these safety properties.
- The capability reference rule changes from "no project references" to "no
  capability-to-capability references"; architecture tests must enforce the
  narrower shared-persistence public surface to keep the project from becoming
  a general-purpose dumping ground.
- Accidental cross-schema access fails at runtime in local and test databases,
  and PostgreSQL integration tests can prove denial rather than relying only on
  static inspection.
- Bootstrap access is more privileged than normal module access and must stay
  isolated to startup orchestration.
- A future production profile must map deployment identities and secrets to
  these privileges without changing the capability contract.

## References

- OpenSpec change:
  [`add-platform-persistence-foundation`](../../../openspec/changes/add-platform-persistence-foundation/design.md)
  (design decisions 1 and 4; relink to the dated archive path when the change
  is archived)
- Affected behavioral specifications:
  [`platform-host`](../../../openspec/specs/platform-host/spec.md) and the
  proposed
  [`platform-persistence` delta](../../../openspec/changes/add-platform-persistence-foundation/specs/platform-persistence/spec.md)
  (relink to `openspec/specs/platform-persistence/spec.md` after
  synchronization)
- Current architecture narratives:
  [Platform Implementation](../platform-implementation.md) and
  [Tenancy and Technology](../tenancy-and-technology.md)

Return to the [architecture decisions](README.md).
