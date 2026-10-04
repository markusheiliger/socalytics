# Platform Persistence Specification

## Purpose

Defines the shared platform persistence behavior and verification evidence that modules can rely on while retaining ownership and isolation of their PostgreSQL schemas, SQL, and migrations.

## Requirements

### Requirement: Shared migration orchestration is ordered and checksum-aware

The platform SHALL discover each module's versioned migrations, apply them in a deterministic module and sequence order, and record each successful migration's module, sequence, identity, and content checksum in `socalytics_migrations.history`. The shared migration schema SHALL contain no domain state.

#### Scenario: Clean database is migrated in order

- **WHEN** migration orchestration runs against an empty supported PostgreSQL database
- **THEN** every registered module migration is applied in the declared deterministic order
- **THEN** `socalytics_migrations.history` contains one successful record for each applied migration with its module, sequence, identity, and checksum

#### Scenario: Unchanged migrations are seen again

- **WHEN** migration orchestration runs after all registered migrations have already succeeded with matching checksums
- **THEN** no migration is reapplied
- **THEN** the database schema and migration-history records remain unchanged

#### Scenario: Applied migration content conflicts

- **WHEN** a registered migration has the same module and identity as an applied migration but a different checksum
- **THEN** startup migration orchestration fails before applying that migration or any later migration
- **THEN** the conflict identifies the owning module and migration identity without exposing database credentials

#### Scenario: A migration fails

- **WHEN** a migration statement fails while its migration is being applied
- **THEN** all effects of that migration are rolled back
- **THEN** no successful history record is written for that migration
- **THEN** migrations committed before the failing migration remain applied

### Requirement: Modules own isolated database schemas

Each platform module SHALL own exactly its adopted schema: Club owns `club`, Identity Access owns `identity_access`, Recordings owns `recordings`, Registry owns `registry`, Analysis owns `analysis`, and Agent Orchestration owns `agent_orchestration`. A module SHALL own its SQL and migrations and SHALL be unable to read or write another module's schema through its normal persistence access.

#### Scenario: Module migrations create owned schemas

- **WHEN** all registered migrations run against an empty database
- **THEN** each adopted module schema exists and is attributed only to its owning module's migration set
- **THEN** no module migration creates or alters an object in another module's schema

#### Scenario: Module attempts cross-schema access

- **WHEN** a module uses its normal persistence access to read or write an object in another module's schema
- **THEN** PostgreSQL rejects the operation
- **THEN** no data or schema change is committed

#### Scenario: Architecture boundaries are inspected

- **WHEN** automated architecture tests inspect capability references, exported types, embedded SQL, and migration ownership
- **THEN** capability modules do not reference one another
- **THEN** each module exposes no persistence implementation type publicly
- **THEN** SQL and migrations are attributable to exactly one owning module schema

### Requirement: Database state changes use explicit transactions

The persistence foundation SHALL require state-changing work to execute within an explicit PostgreSQL transaction and SHALL commit all work only when the operation succeeds.

#### Scenario: Transactional operation succeeds

- **WHEN** a state-changing operation completes successfully within an explicit transaction
- **THEN** all changes made by that operation are committed together

#### Scenario: Transactional operation fails

- **WHEN** a state-changing operation throws or is cancelled before commit
- **THEN** all changes made by that operation are rolled back
- **THEN** the connection can be reused without retaining an active transaction

### Requirement: Contested writes support optimistic concurrency

The persistence foundation SHALL provide a module-neutral primitive for comparing an expected version and advancing the persisted version atomically within the caller's explicit transaction. A version mismatch SHALL be reported as a concurrency conflict and SHALL NOT be treated as a successful write.

#### Scenario: Expected version matches

- **WHEN** a write supplies the currently persisted version
- **THEN** the state change and version advance succeed atomically within the explicit transaction

#### Scenario: Expected version is stale

- **WHEN** a write supplies a version that no longer matches the persisted version
- **THEN** the write reports a concurrency conflict
- **THEN** no part of the contested state change is committed

### Requirement: The persistence model preserves the single-club stamp invariant

Platform persistence artifacts SHALL model the club as the implicit root of one deployment stamp and SHALL NOT introduce a `club_id` discriminator into module schemas, migration history, persistence commands, or persistence events.

#### Scenario: Persistence artifacts are inspected

- **WHEN** architecture tests inspect persistence types, SQL, and migrations and PostgreSQL tests inspect created columns
- **THEN** no inspected command, event, table, view, function argument, or migration-history field is named `club_id`

### Requirement: PostgreSQL behavior is verified against a real compatible engine

Automated integration tests SHALL create disposable PostgreSQL instances and verify migration ordering, checksum conflicts, repeat startup, migration rollback, explicit transaction rollback, optimistic concurrency, module schema ownership, cross-schema denial, and the absence of `club_id`.

#### Scenario: Contributor runs the platform test workflow

- **WHEN** a contributor runs the documented platform test command with a supported container runtime available
- **THEN** the PostgreSQL integration suite exercises the required persistence scenarios against disposable PostgreSQL instances
- **THEN** each instance is isolated from developer and production databases and is cleaned up after the test run
