# 0001 Shared Persistence Boundary And Database Roles

## Status

Accepted for implementation of the OpenSpec change
`add-platform-persistence-foundation`.

That change is still active, so this record references it by its canonical
change identity rather than by a dated archive path. The archived change must
link back to this record before archive completes, and the current architecture
narratives are synchronized by a separate task in the same change.

## Context

The executable platform baseline is a .NET 10 modular-monolith host with six
peer capability projects that expose one public composition boundary each and do
not reference one another. The adopted architecture in
[Platform Implementation Profile](../platform-implementation.md) and
[Deployment and Technology](../tenancy-and-technology.md) already selects one
logical PostgreSQL database per single-club stamp, Npgsql and Dapper for data
access, DbUp for ordered module-grouped migrations, explicit transactions,
optimistic concurrency, module-owned schemas, and a shared
`socalytics_migrations.history` that holds no domain state.

Two properties of that adopted design had no owner in the executable baseline.

First, connection creation, explicit transaction execution, optimistic-
concurrency signalling, migration descriptors, checksum verification, and
migration orchestration are needed by every capability module, but the module
graph was deliberately flat and had no shared production peer to hold them.
Placing them in ServiceDefaults, duplicating them per module, or placing
orchestration in the API each assigns database policy to a component that does
not own it.

Second, module schema ownership was advisory. Architecture tests can detect a
cross-schema query in committed source, but nothing prevented a module session
from reading or writing a peer module's schema at runtime. Enforcement authority
for isolation was therefore undefined.

Both properties are structural: they change the component dependency graph and
they determine which component is authoritative for schema isolation. Preserving
the rationale is useful because the rejected alternatives recur whenever a new
module or a production profile is added, so this crosses the ADR threshold in
the [decision index](README.md). The remaining choices in the originating design
— embedded migration resources, checksum journal policy, transaction-executor
shape, and startup migration gating — implement these two decisions and are
recorded there rather than here.

## Decision

**One shared peer persistence project owns module-neutral database
infrastructure.** A shared production project under `src/platform` is a peer of
the capability projects and is the single owner of Npgsql data-source
configuration, Dapper integration support, explicit transaction execution,
optimistic-concurrency result and conflict semantics, migration descriptors,
checksum verification, the shared migration journal, and migration
orchestration. It owns the `socalytics_migrations` schema and owns no capability
schema, domain SQL, or domain record.

Dependency direction is one-way: each capability project references the shared
persistence project and never another capability project, and the shared project
references no capability project. A capability registers its internal migration
contributor and its internal module-scoped persistence services through its
existing public composition method and continues to export only that composition
type. The API host may call the shared persistence registration and startup
boundary but must not consume module persistence implementation types.

**PostgreSQL privileges, not convention, enforce module schema isolation.** For
local and test databases, shared bootstrap creates a stable `NOLOGIN` owner role
and a stable `NOLOGIN` runtime role for each adopted module. The owner role owns
only its module's schema and migration objects. The runtime role receives only
the schema usage and object privileges that normal module access requires;
public and peer-module access is revoked. Migration execution assumes the owning
module's owner role, and normal module sessions assume its runtime role.

The shared connection factory produces module-scoped sessions and applies the
role during connection or session setup. A module cannot request an arbitrary
schema through public API; its internal registration binds a fixed adopted
module identity. The bootstrap and administrative connection is reachable only
from migration orchestration and is not injectable into module application
services.

This decision establishes role and privilege semantics only. It selects no
production identity, login name, credential, or secret source.

The single-club stamp invariant is unchanged: the club remains the implicit
root, and no persistence artifact introduced under this decision carries a
`club_id` discriminator.

## Consequences

**Enabling consequences.**

- Connection, transaction, concurrency, checksum, and startup behavior have one
  implementation and cannot diverge per module.
- Cross-schema access fails in the database during normal module access, so
  isolation is verifiable at runtime rather than only in source review.
- Migration ordering, history, and checksum policy have a single owner, which
  makes deterministic repeat startup and conflict detection testable against a
  real engine.
- The capability reference graph and narrow public composition surfaces are
  preserved, so a module can still be extracted later without unwinding peer
  module coupling.

**Costs and constraints accepted.**

- The capability graph gains a shared dependency vertex. The shared project must
  keep a restricted public surface limited to module-neutral concerns, enforced
  by architecture tests, or it becomes a dumping ground and a coupling channel
  between modules.
- A bootstrap connection exists that is more privileged than any module runtime
  session. It must remain confined to migration orchestration, excluded from
  module dependency injection, and covered by explicit denial tests.
- Role-based isolation requires bootstrap to run before module access, which
  adds startup ordering that local composition and tests must honour.
- Isolation behavior can only be proven against a real PostgreSQL engine, so the
  supported test workflow requires a supported container runtime.
- A future production profile must map deployment identities and secrets onto
  these owner and runtime privileges. It may not restore isolation by granting a
  single login ownership of every schema, because that would make isolation
  advisory again.

**Deliberately unresolved.** Production host, port, database name, login
identities, credential delivery, secret source, role-to-identity mapping,
encryption, residency, retention, audit, backup and restore, capacity, recovery
objectives, and deployment migration sequencing remain out of scope and
unresolved. They are governed by
[Security and Data Governance](../security-and-data-governance.md) and
[Production Deployment and Operations](../production-operations.md) and remain
blocking for production promotion. Whether migration execution eventually moves
out of API startup into a separate deployment step is deferred with them. The
exact shared-project name and internal registration contracts may be refined
during implementation while the public surface and dependency direction above
continue to hold.

## References

Originating OpenSpec change `add-platform-persistence-foundation`:

- [Proposal](../../../openspec/changes/add-platform-persistence-foundation/proposal.md)
- [Design](../../../openspec/changes/add-platform-persistence-foundation/design.md),
  decisions 1 and 4, including the rejected alternatives
- [Platform persistence requirement delta](../../../openspec/changes/add-platform-persistence-foundation/specs/platform-persistence/spec.md)
- [Platform host requirement delta](../../../openspec/changes/add-platform-persistence-foundation/specs/platform-host/spec.md)

Affected behavioral specifications:

- [`platform-host`](../../../openspec/specs/platform-host/spec.md)
- `platform-persistence`, which becomes an accepted specification under
  [`openspec/specs/`](../../../openspec/specs/) when the originating change is
  archived

Current architecture narratives governed by this decision:

- [Platform Implementation Profile](../platform-implementation.md)
- [Deployment and Technology](../tenancy-and-technology.md)

The change links above point at the active change directory. Archiving moves
that directory, so archive work must repoint them at the archived location and
add the corresponding link from the archived change back to this record.

Return to the [decision index](README.md) or the
[architecture index](../README.md).
