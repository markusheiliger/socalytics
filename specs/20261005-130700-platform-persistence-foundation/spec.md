# Feature Specification: Platform Persistence Foundation

**Feature Branch**: `20261005-130700-platform-persistence-foundation`

**Created**: 2026-10-05

**Status**: Draft

**Input**: User description: "Establish the shared platform persistence foundation that every capability module relies on: one platform database per single-club stamp, ordered and fingerprint-verified module migrations with a shared migration history, isolated module-owned data areas, explicit transactions, optimistic concurrency, and local composition whose readiness depends on the database and successful migrations. No domain behavior."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Evolve the platform database safely and repeatably (Priority: P1)

A platform developer adds versioned migrations to a capability module. Whenever the platform starts against a database, every registered migration that has not yet been applied is applied in a deterministic order and recorded in a shared migration history. Migrations that were already applied are never applied again, an applied migration whose content was changed is detected before any new work runs, and a failing migration leaves no partial effects.

**Why this priority**: Every later feature (club identity, recordings, analysis workflow, analyst manager registration) needs to create and evolve its data safely. Without ordered, recorded, tamper-evident, and atomic migrations, no module can persist anything trustworthy.

**Independent Test**: Can be fully tested by running migration orchestration against disposable, empty databases with test-only migration sets and inspecting the resulting data areas and migration history, without starting the platform host.

**Acceptance Scenarios**:

1. **Given** an empty database and registered migrations for several modules, **When** migration orchestration runs, **Then** every registered migration is applied in the declared module order and then module-local sequence order, and the migration history contains exactly one success record per applied migration with its owning module, sequence, identity, content fingerprint, and time applied.
2. **Given** a database on which all registered migrations already succeeded with matching fingerprints, **When** migration orchestration runs again, **Then** no migration is reapplied and neither the data areas nor the migration history change.
3. **Given** a database with an applied migration, **When** the registered migration with the same module and identity has different content, **Then** orchestration fails before applying any pending migration and reports the owning module and migration identity without exposing credentials or migration content.
4. **Given** a database with some applied migrations and a pending migration that fails partway, **When** orchestration runs, **Then** all effects of the failing migration are undone, no success record is written for it, earlier migrations remain applied, and no later migration is attempted.
5. **Given** a newly registered migration for a module whose sequence is lower than or equal to that module's highest applied sequence, **When** orchestration runs, **Then** it fails before applying any pending migration and identifies the module and the offending migration.

---

### User Story 2 - Start the local platform with a ready database (Priority: P2)

A platform developer or local platform operator starts the platform with the single documented local command. The local composition provides a development-only platform database, gives the platform API access to it, applies all registered migrations at startup, and reports the API as ready only once the database is reachable and migrations have succeeded. The existing liveness, readiness, and API description surfaces keep working, and no other deferred infrastructure is required.

**Why this priority**: Developers must be able to run the platform end to end with its database before any domain feature can be built or demonstrated, and readiness must never be reported while the data foundation is unusable.

**Independent Test**: Can be fully tested by starting the local composition, waiting for the database and the API to report healthy, inspecting the migration history, and then repeating the start against the already-migrated database and against an unavailable database or a failing migration.

**Acceptance Scenarios**:

1. **Given** a contributor with the documented prerequisites, **When** they run the documented local start command, **Then** the composition starts a development-only database and the platform API, and reports the health of both.
2. **Given** the local composition is starting, **When** the database is reachable and all registered migrations succeed, **Then** the API reports ready and its liveness, readiness, and API description surfaces behave as before this feature.
3. **Given** the database is unavailable, or a migration fails, or an applied migration's fingerprint conflicts, **When** the API starts, **Then** the API never reports ready and a diagnostic identifies the failure category (and, for migration failures, the owning module and migration identity) without exposing credentials.
4. **Given** a database already migrated by a previous start, **When** the platform is started again, **Then** it becomes ready without reapplying any migration.

---

### User Story 3 - Keep each capability module's data isolated (Priority: P3)

Each capability module (Club, Identity Access, Recordings, Registry, Analysis, and Agent Orchestration) owns exactly one data area, its own data definitions and queries, and its own migrations. A module's normal data access cannot read or write another module's data area, module persistence internals are not exposed to the host or to other modules, and automated checks fail if any of these boundaries is violated.

**Why this priority**: Module isolation is the architectural guardrail that keeps the modular platform maintainable and extractable. It must exist before domain data arrives, but the platform can run and migrate without it being exercised by domain behavior yet.

**Independent Test**: Can be fully tested by migrating a disposable database, then attempting cross-module reads and writes through each module's normal data access, and by running the automated structural checks over module references, exposed types, and migration ownership.

**Acceptance Scenarios**:

1. **Given** all registered migrations ran against an empty database, **When** the data areas are inspected, **Then** each of the six adopted modules has exactly its own data area, attributed only to its own migrations, and no module migration created or altered anything in another module's data area.
2. **Given** a module's normal data access, **When** it attempts to read or write an object in another module's data area, **Then** the operation is rejected and nothing is committed.
3. **Given** the platform's automated structural checks, **When** they run, **Then** they confirm that modules do not reference one another, that no module exposes persistence implementation publicly, that the host uses only public module composition boundaries, and that every migration belongs to exactly one module.
4. **Given** a module attempts to register a migration under another module's identity, **When** migrations are registered, **Then** registration is rejected before any migration runs.

---

### User Story 4 - Change state reliably under failure and contention (Priority: P4)

A capability module performing a state change does so inside an explicit all-or-nothing unit of work, so either all of its changes are committed or none are. When two writers contend for the same record, a writer holding a stale version receives a distinguishable concurrency conflict instead of silently overwriting the other writer's change.

**Why this priority**: Later domain features depend on these guarantees for correctness, but they are only exercised once modules start writing domain data; the foundation must provide and prove them now so later features do not reinvent them.

**Independent Test**: Can be fully tested with test-only data in a disposable database by running successful, failing, and cancelled units of work, and by issuing writes with matching and stale expected versions.

**Acceptance Scenarios**:

1. **Given** a state-changing operation inside an explicit unit of work, **When** it completes successfully, **Then** all of its changes are committed together.
2. **Given** a state-changing operation inside an explicit unit of work, **When** it fails or is cancelled before commit, **Then** none of its changes are committed and subsequent work is not affected by a leftover open unit of work.
3. **Given** a versioned record, **When** a write supplies the currently persisted version, **Then** the change and the version advance by exactly one are committed atomically within the caller's unit of work.
4. **Given** a versioned record, **When** a write supplies a version that no longer matches the persisted version, **Then** the write reports a concurrency conflict and no part of the contested change is committed.

---

### Edge Cases

- **First start on an empty database**: the shared migration history area is created and all registered migrations are applied in order.
- **Database not yet reachable during local start**: the API waits for the database and does not report ready until it is reachable and migrations succeed.
- **Database becomes unreachable after the API was ready**: readiness reports not ready while the database is unreachable and recovers without a restart once it is reachable again; no migration is reapplied.
- **Applied migration edited after application**: startup fails before any new migration runs; the correction must be delivered as a new migration.
- **Pending migration inserted below an already-applied sequence of the same module**: startup fails before any pending migration runs and identifies the module and migration.
- **Duplicate sequence or identity within one module's registered migrations**: registration fails before any migration runs.
- **Migration history contains a record for a migration that is no longer registered** (for example an older platform version started against a newer database): the record is left untouched, startup proceeds if all remaining checks pass, and a diagnostic notes the unknown applied migration.
- **Two migration runs race against the same database**: no migration is applied or recorded twice; the run that loses fails its conflicting migration atomically instead of corrupting the history.
- **A module registers no pending migrations**: orchestration completes without changes for that module.
- **Unit of work cancelled mid-operation**: all of its changes are undone.
- **Diagnostics for connection or migration failures**: they never contain credentials, connection secrets, or migration content.
- **Disposable verification database cannot be created**: the verification run fails visibly rather than falling back to a developer or production database.

## Requirements *(mandatory)*

### Functional Requirements

#### Migration orchestration and history

- **FR-001**: The platform MUST discover the versioned migrations registered by each capability module and apply pending migrations in a deterministic order: first by the adopted module order (Club, Identity Access, Recordings, Registry, Analysis, Agent Orchestration), then by each module's ascending module-local sequence.
- **FR-002**: The platform MUST record each successfully applied migration in a shared migration history with its owning module, module-local sequence, stable identity, content fingerprint, and time applied; the migration history MUST contain no domain data.
- **FR-003**: The migration history MUST reject a second success record for the same module and identity or for the same module and sequence.
- **FR-004**: Migration orchestration MUST NOT reapply a migration that is already recorded as successful with a matching content fingerprint, and a run with no pending migrations MUST leave the data areas and the migration history unchanged.
- **FR-005**: Before applying any pending migration, orchestration MUST verify the content fingerprint of every registered migration that is already recorded as applied and MUST fail without applying anything if any fingerprint differs, identifying the owning module and migration identity.
- **FR-006**: Before applying any pending migration, orchestration MUST fail without applying anything if a module's registered migrations contain a duplicate sequence or identity, or if a pending migration's sequence is lower than or equal to that module's highest applied sequence.
- **FR-007**: Each migration MUST be applied as one all-or-nothing unit together with its history record: if any part fails, all of its effects MUST be undone, no success record MUST be written, earlier migrations MUST remain applied, and no later migration MUST be attempted in that run.
- **FR-008**: Migrations MUST be forward-only; the platform MUST NOT provide automatic reversal of applied migrations, and corrections MUST be delivered as new migrations.
- **FR-009**: A history record for a migration that is not registered MUST be left untouched, MUST NOT block startup on its own, and MUST be reported in a diagnostic.

#### Module data ownership and isolation

- **FR-010**: Each of the six adopted capability modules MUST own exactly one data area, and the shared persistence foundation MUST own only the migration history area.
- **FR-011**: A module MUST be able to register migrations only under its own module identity, and a module's migrations MUST create or alter objects only within its own data area.
- **FR-012**: A module's normal runtime data access MUST be rejected when it attempts to read or write another module's data area, and no data or structural change from such an attempt MUST be committed.
- **FR-013**: The elevated access needed to apply migrations MUST be available only to migration orchestration and MUST NOT be available to modules' normal runtime data access.
- **FR-014**: Module data definitions, queries, migrations, and persistence implementation MUST remain internal to the owning module; the host and other modules MUST interact with a module only through its public composition boundary, and modules MUST NOT reference one another.
- **FR-015**: The shared persistence foundation MUST be module-neutral: it MUST NOT contain domain data definitions, domain queries, or domain records.

#### Explicit transactions and optimistic concurrency

- **FR-016**: Every state-changing operation MUST execute within an explicit all-or-nothing unit of work that commits only when the whole operation succeeds.
- **FR-017**: When a state-changing operation fails or is cancelled before commit, all of its changes MUST be undone, and the resources it used MUST be reusable without a leftover open unit of work.
- **FR-018**: The foundation MUST provide a module-neutral optimistic-concurrency capability that compares a caller-supplied expected version with the persisted version and, on match, advances the version by exactly one atomically within the caller's unit of work.
- **FR-019**: A version mismatch MUST be reported to the caller as a distinguishable concurrency conflict, MUST NOT be treated as a successful write, and MUST leave no part of the contested change committed.

#### Single-club stamp invariant

- **FR-020**: Persistence artifacts MUST treat the club as the implicit root of its deployment stamp: no module data area, migration history field, persistence command, or persistence event MUST carry a club identifier or other cross-club discriminator.

#### Local composition and readiness

- **FR-021**: The local composition MUST provide one development-only platform database, supply the platform API with access to it, and start the API only after the database is available, without requiring messaging, object storage, or other deferred infrastructure.
- **FR-022**: The platform API MUST apply all registered migrations during startup and MUST report ready only after the database is reachable and migration orchestration has succeeded; readiness MUST report not ready while the database is unreachable.
- **FR-023**: When the database is unavailable, a migration fails, or a fingerprint or sequence conflict is detected, the API MUST NOT report ready and MUST surface a diagnostic that identifies the failure category and, for migration failures, the owning module and migration identity.
- **FR-024**: Diagnostics, logs, and telemetry produced by the persistence foundation MUST NOT contain credentials, connection secrets, or migration content.
- **FR-025**: The local composition MUST report the health of both the database and the platform API.
- **FR-026**: The existing liveness, readiness, and API description surfaces MUST remain available with unchanged behavior apart from readiness now depending on the database, and no domain endpoint MUST be added.

#### Verification evidence and documentation

- **FR-027**: Automated verification MUST exercise migration ordering, history completeness, repeat runs, fingerprint conflicts, sequence conflicts, migration rollback, unit-of-work commit and rollback, optimistic concurrency, data-area ownership, cross-module access denial, and the absence of a club discriminator against disposable, isolated database instances of the same kind the platform uses.
- **FR-028**: Automated verification MUST NOT connect to developer-owned or production databases and MUST remove every disposable database instance it creates after the run.
- **FR-029**: Automated structural checks MUST verify module reference isolation, the public surface of each module, the host's use of public composition boundaries only, single-module ownership of every migration, and the absence of a club discriminator in persistence artifacts.
- **FR-030**: Repository and platform development documentation MUST describe the supported commands and prerequisites for running the local platform and its persistence verification, and MUST distinguish the implemented persistence foundation from deferred domain behavior, deferred infrastructure, and production readiness.

### Key Entities

- **Capability Module**: One of the six adopted platform capabilities (Club, Identity Access, Recordings, Registry, Analysis, Agent Orchestration); owns one data area, its data definitions, queries, and migrations, and exposes only a public composition boundary.
- **Module Data Area**: The isolated portion of the stamp's single platform database owned by exactly one capability module; only its owning module's migrations may shape it and only its owning module's runtime access may use it.
- **Migration**: A versioned, immutable change to one module's data area, identified by owning module, module-local sequence, stable identity, and content fingerprint.
- **Migration History Record**: The shared, domain-free record of one successfully applied migration: owning module, sequence, identity, content fingerprint, and time applied; unique per module and identity and per module and sequence.
- **Unit of Work**: An explicit all-or-nothing scope within which a state change is committed entirely or not at all.
- **Record Version**: A per-record counter used for optimistic concurrency; a write succeeds only when it supplies the current version, which then advances by exactly one.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A contributor with the documented prerequisites starts the local platform with one documented command and sees both the database and the API reported healthy within 3 minutes, excluding first-time downloads of prerequisites.
- **SC-002**: On an empty database, 100% of registered migrations are applied in the declared order and each has exactly one success record in the migration history.
- **SC-003**: Across 3 consecutive starts against an already-migrated database, 0 migrations are reapplied and 0 changes occur in the data areas or the migration history.
- **SC-004**: In 100% of fingerprint-conflict and sequence-conflict cases, 0 pending migrations are applied and the diagnostic names the owning module and migration identity, with 0 credentials or migration content exposed.
- **SC-005**: In 100% of injected migration failures, the failing migration leaves 0 partial effects and 0 success records, while all previously applied migrations remain intact.
- **SC-006**: All 60 cross-module access attempts (read and write from each of the 6 modules into each of the 5 other modules' data areas) through normal runtime access are rejected, with 0 committed changes.
- **SC-007**: In 100% of failed or cancelled units of work, 0 changes are committed; in 100% of successful units of work, all changes are committed.
- **SC-008**: 100% of stale-version writes are reported as concurrency conflicts with 0 partial commits, and 100% of matching-version writes succeed and advance the version by exactly one.
- **SC-009**: Inspection of all data areas, the migration history, and persistence commands and events finds 0 club identifiers or cross-club discriminators.
- **SC-010**: 100% of persistence verification runs use disposable isolated database instances, with 0 connections to non-disposable databases and 0 instances left behind after the run.
- **SC-011**: In 100% of startup tests with an unavailable database or a failing or conflicting migration, the API never reports ready and a diagnostic is surfaced.
- **SC-012**: All pre-existing host and architecture checks continue to pass, the liveness, readiness, and API description surfaces remain available, and 0 domain endpoints are added.
- **SC-013**: A reviewer reading the updated development documentation can identify every supported command and prerequisite, and finds 0 statements claiming that domain behavior, deferred infrastructure, or production readiness is implemented.

## Assumptions

- **Actors**: platform developers (add migrations and module persistence), local platform operators and contributors (start and inspect the local platform), and the platform's capability modules (consume the persistence foundation). There is no end-user interface in this feature.
- **Initial module migrations** only establish each module's own data area and its access rights; no domain tables, records, or endpoints are added. Behavior that needs data (rollback, concurrency, cross-module denial) is proven with test-only migrations and test-only data.
- **Migration ordering default**: modules are ordered Club, Identity Access, Recordings, Registry, Analysis, Agent Orchestration, as in the adopted ownership map; modules declare no cross-module migration dependencies, and module-local sequences only increase.
- **Startup migrations**: migrations run once during platform API startup in the local, single-host composition. A separate migration step and migration sequencing for production deployments remain unresolved in the architecture and are deferred.
- **Unknown applied migrations** (history records not in the registered set) are tolerated with a diagnostic so that a backward-compatible older platform version can start; this is a local-foundation default and does not decide the production rollback policy, which the architecture governs separately.
- **Liveness** continues to reflect only the API process and does not depend on the database; only readiness depends on the database and migrations.
- **Access levels**: migration orchestration uses access that can create and alter a module's data area, while each module's normal runtime access is limited to its own data area. These are role semantics only; production identities, credential values, secret sources, and role-to-identity mapping are deferred.
- **Local database** is development-only; its local connection values are not production values and must not be presented as such.
- **Out of scope / deferred**: domain entities, tables, queries, and endpoints (owned by later features: club identity, recording lineage and upload, durable analysis workflow, and analyst manager registration); the transactional outbox and event publication (deferred to the feature that adopts the event backbone, although the architecture's persistence baseline includes them); messaging, object storage, authentication, and authorization; production hosting, ports, database name, credentials, secret source, role-to-identity mapping, encryption, residency, backup and restore, retention and deletion, capacity, recovery objectives, and deployment configuration, all of which the architecture leaves unresolved or blocking production; automatic down-migrations; and destructive database-reset automation.
- **Production readiness**: successful local start or passing verification is development evidence only and does not claim deployment support or production readiness.
- **Dependencies**: the existing executable platform host scaffold, the six capability modules' public composition boundaries, and the existing host and architecture test suites; a documented local prerequisite for creating disposable database instances. This feature has no dependency on other specifications; the later foundation specifications listed in `specs/README.md` build on it.
- **Architecture synchronization**: where this feature makes planned persistence behavior executable or refines it (for example the shared persistence boundary and module access levels), the owning architecture narratives are updated in the same change, as the constitution requires.
- **Architecture References**: [docs/architecture/platform-implementation.md](../../docs/architecture/platform-implementation.md), [docs/architecture/tenancy-and-technology.md](../../docs/architecture/tenancy-and-technology.md), [docs/architecture/overview.md](../../docs/architecture/overview.md), [docs/architecture/security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md), [docs/architecture/production-operations.md](../../docs/architecture/production-operations.md).
