# Feature Specification: Platform Persistence Foundation

**Feature Branch**: `20261005-130700-platform-persistence-foundation`

**Created**: 2026-10-05

**Status**: Draft

**Input**: User description: "Establish the shared platform persistence foundation that every platform feature relies on: one platform database per single-club stamp with one application data area, ordered and fingerprint-verified migrations in one platform-wide sequence with a migration history kept apart from domain data, explicit transactions, optimistic concurrency, persistence confined to the platform's data-access layer, and local composition whose readiness depends on the database and successful migrations. No domain behavior." (Updated 2026-10-07: the platform is a layered monolith without isolated modules, so per-module data areas, migration streams, and access separation are removed.)

## Clarifications

### Session 2026-10-07

- Q: When should a changeable record's version go up? → A: On every real change, through any write path and including data migrations, by exactly one; writes that change nothing leave it unchanged; only a migration may explicitly opt out.
- Q: When a record that belongs to a versioned main record changes, should the main record's version go up? → A: Yes; adding, changing, or removing a dependent record advances the main record's version by exactly one per committed change, and dependent records carry no version of their own.
- Q: Should startup continue when the migration history contains an applied migration that the starting platform doesn't know? → A: Yes; leave the unknown history record untouched, start if all other checks pass, and report it in a diagnostic.
- Q: Should migrations run as a separate one-off step instead of inside the API? → A: Yes; a separate one-off migration step that the local composition runs before starting the API; the API never migrates and reports ready only when the database is reachable and its migration state is current; a concurrent second run waits up to a bounded time for the first and then applies only what is still pending; production hosting of the step stays deferred.
- Q: How long may the documented local start take before everything is reported healthy (SC-001)? → A: No timing target; the start must only complete with the single documented command and no manual steps.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Evolve the platform database safely and repeatably (Priority: P1)

A platform developer adds a versioned migration to the platform's single migration sequence. Whenever the separate migration step runs against a database, every migration that has not yet been applied is applied in sequence order and recorded in the migration history. Migrations that were already applied are never applied again, an applied migration whose content was changed is detected before any new work runs, and a failing migration leaves no partial effects.

**Why this priority**: Every later feature (club identity, recordings, analysis workflow, analyst manager registration) needs to create and evolve its data safely. Without ordered, recorded, tamper-evident, and atomic migrations, nothing can be persisted in a trustworthy way.

**Independent Test**: Can be fully tested by running migration orchestration against disposable, empty databases with test-only migration sets and inspecting the resulting application data and migration history, without starting the platform host.

**Acceptance Scenarios**:

1. **Given** an empty database and a set of registered migrations, **When** migration orchestration runs, **Then** every migration is applied in ascending sequence order, and the migration history contains exactly one success record per applied migration with its sequence, identity, content fingerprint, and time applied.
2. **Given** a database on which all registered migrations already succeeded with matching fingerprints, **When** migration orchestration runs again, **Then** no migration is reapplied and neither the application data nor the migration history change.
3. **Given** a database with an applied migration, **When** the registered migration with the same identity has different content, **Then** orchestration fails before applying any pending migration and reports the migration identity without exposing credentials or migration content.
4. **Given** a database with some applied migrations and a pending migration that fails partway, **When** orchestration runs, **Then** all effects of the failing migration are undone, no success record is written for it, earlier migrations remain applied, and no later migration is attempted.
5. **Given** a newly registered migration whose sequence is lower than or equal to the highest applied sequence, **When** orchestration runs, **Then** it fails before applying any pending migration and identifies the offending migration.
6. **Given** a migration run in progress against a database, **When** a second run starts against the same database, **Then** the second run waits, up to a bounded time, for the first to finish and then applies only migrations that are still pending (usually none); if the wait expires, it fails visibly without applying anything, and no migration is ever applied or recorded twice.

---

### User Story 2 - Start the local platform with a ready database (Priority: P2)

A platform developer or local platform operator starts the platform with the single documented local command. The local composition provides a development-only platform database, runs the separate one-off migration step against it, and starts the platform API only after that step has succeeded. The API never applies migrations itself; it reports ready only while the database is reachable and its migration state is current. The existing liveness, readiness, and API description surfaces keep working, and no other deferred infrastructure is required.

**Why this priority**: Developers must be able to run the platform end to end with its database before any domain feature can be built or demonstrated, and readiness must never be reported while the data foundation is unusable.

**Independent Test**: Can be fully tested by starting the local composition, waiting for the database, the migration step, and the API to report their outcome, inspecting the migration history, and then repeating the start against the already-migrated database and against an unavailable database or a failing migration.

**Acceptance Scenarios**:

1. **Given** a contributor with the documented prerequisites, **When** they run the documented local start command, **Then** the composition starts a development-only database, runs the migration step, starts the platform API, and reports the database health, the migration step outcome, and the API health.
2. **Given** the local composition is starting, **When** the database is reachable and the migration step succeeds, **Then** the API starts, reports ready, and its liveness, readiness, and API description surfaces behave as before this feature.
3. **Given** the database is unavailable, or a migration fails, or an applied migration's fingerprint conflicts, **When** the migration step runs, **Then** it fails with a diagnostic that identifies the failure category (and, for migration failures, the migration identity) without exposing credentials, and the API is not started or never reports ready.
4. **Given** a database already migrated by a previous start, **When** the platform is started again, **Then** the migration step applies nothing and the API becomes ready.
5. **Given** a database with at least one registered migration not yet applied, **When** the API runs against it without a successful migration step, **Then** the API never reports ready and a diagnostic states that the migration state is not current.

---

### User Story 3 - Change state reliably under failure and contention (Priority: P3)

Platform code performing a state change does so inside an explicit all-or-nothing unit of work, so either all of its changes are committed or none are. The version of a changeable record goes up by exactly one whenever its data actually changes, whichever way the change is made, so when two writers contend for the same record, a writer holding a stale version receives a distinguishable concurrency conflict instead of silently overwriting the other writer's change.

**Why this priority**: Later domain features depend on these guarantees for correctness, but they are only exercised once features start writing domain data; the foundation must provide and prove them now so later features do not reinvent them.

**Independent Test**: Can be fully tested with test-only data in a disposable database by running successful, failing, and cancelled units of work, and by issuing writes with matching and stale expected versions.

**Acceptance Scenarios**:

1. **Given** a state-changing operation inside an explicit unit of work, **When** it completes successfully, **Then** all of its changes are committed together.
2. **Given** a state-changing operation inside an explicit unit of work, **When** it fails or is cancelled before commit, **Then** none of its changes are committed and subsequent work is not affected by a leftover open unit of work.
3. **Given** a versioned record, **When** a write supplies the currently persisted version, **Then** the change and the version advance by exactly one are committed atomically within the caller's unit of work.
4. **Given** a versioned record, **When** a write supplies a version that no longer matches the persisted version, **Then** the write reports a concurrency conflict and no part of the contested change is committed.
5. **Given** a versioned record, **When** a write changes its data without supplying an expected version, **Then** the version still advances by exactly one when the change is committed.
6. **Given** a versioned record, **When** a write leaves all of its data unchanged, **Then** the version does not advance.
7. **Given** versioned records with test-only data, **When** a test-only migration changes them, **Then** their versions advance by exactly one, unless that migration explicitly opts out of version advancement.
8. **Given** a versioned main record with test-only dependent records, **When** a write adds, changes, or removes one dependent record, **Then** the main record's version advances by exactly one, and a writer holding the previous main-record version receives a concurrency conflict.

---

### Edge Cases

- **First start on an empty database**: the migration history is created and all registered migrations are applied in order.
- **Database not yet reachable during local start**: the migration step waits for the database, and the API is not started until the migration step has succeeded.
- **Database becomes unreachable after the API was ready**: readiness reports not ready while the database is unreachable and recovers without a restart once it is reachable again; the API never applies migrations.
- **Applied migration edited after application**: the migration step fails before any new migration runs; the correction must be delivered as a new migration.
- **Pending migration inserted below an already-applied sequence**: the migration step fails before any pending migration runs and identifies the migration.
- **Duplicate sequence or identity among the registered migrations**: registration fails before any migration runs.
- **Migration history contains a record for a migration that is no longer registered** (for example an older platform version started against a newer database): the record is left untouched, the migration step and the API proceed if all remaining checks pass, and a diagnostic notes the unknown applied migration.
- **Two migration runs race against the same database** (for example a repeated run or one run per API instance): the second run waits, up to a bounded time, for the first to finish and then applies only what is still pending; if the wait expires, it fails visibly without applying anything; no migration is applied or recorded twice and the history is never corrupted.
- **API started against a database whose migration state is not current** (the migration step was skipped or failed): the API never reports ready and reports that the migration state is not current.
- **No pending migrations**: orchestration completes without changes.
- **Unit of work cancelled mid-operation**: all of its changes are undone.
- **Data migration changes versioned records**: their versions advance so that clients holding older versions see a conflict, unless the migration explicitly opts out (for example a purely technical backfill that does not change what clients see).
- **Write that changes nothing**: the version stays the same, so it causes no false conflict for other writers.
- **One unit of work changes several parts of a main record**: the main record's version advances once per individual change, so it may advance by more than one; clients compare versions only for equality and never derive meaning from the size of the step.
- **Diagnostics for connection or migration failures**: they never contain credentials, connection secrets, or migration content.
- **Disposable verification database cannot be created**: the verification run fails visibly rather than falling back to a developer or production database.

## Requirements *(mandatory)*

### Functional Requirements

#### Migration orchestration and history

- **FR-001**: The platform MUST maintain one platform-wide sequence of versioned migrations and apply pending migrations in ascending sequence order.
- **FR-002**: The platform MUST record each successfully applied migration in a migration history with its sequence, stable identity, content fingerprint, and time applied; the migration history MUST be kept apart from domain data and MUST contain no domain data.
- **FR-003**: The migration history MUST reject a second success record for the same identity or the same sequence.
- **FR-004**: Migration orchestration MUST NOT reapply a migration that is already recorded as successful with a matching content fingerprint, and a run with no pending migrations MUST leave the application data and the migration history unchanged.
- **FR-005**: Before applying any pending migration, orchestration MUST verify the content fingerprint of every registered migration that is already recorded as applied and MUST fail without applying anything if any fingerprint differs, identifying the migration identity.
- **FR-006**: Before applying any pending migration, orchestration MUST fail without applying anything if the registered migrations contain a duplicate sequence or identity, or if a pending migration's sequence is lower than or equal to the highest applied sequence.
- **FR-007**: Each migration MUST be applied as one all-or-nothing unit together with its history record: if any part fails, all of its effects MUST be undone, no success record MUST be written, earlier migrations MUST remain applied, and no later migration MUST be attempted in that run.
- **FR-008**: Migrations MUST be forward-only; the platform MUST NOT provide automatic reversal of applied migrations, and corrections MUST be delivered as new migrations.
- **FR-009**: A history record for a migration that is not registered MUST be left untouched, MUST NOT on its own block the migration step or API readiness, and MUST be reported in a diagnostic.
- **FR-010**: A migration run that starts while another run is in progress against the same database MUST wait, up to a bounded time, for that run to finish and then apply only migrations that are still pending; if the wait expires it MUST fail visibly without applying anything; no migration MUST ever be applied or recorded twice.

#### Data organization and access

- **FR-011**: The platform MUST keep all application data of a stamp in one application data area of the stamp's single platform database; functional areas of the platform MUST NOT require separate data areas or separate access separation.
- **FR-012**: The elevated access needed to apply migrations MUST be used only by the migration step; the platform API's runtime data access MUST be able to read and write application data and read the migration history but MUST NOT be able to create or alter data structures.
- **FR-013**: Data definitions, queries, and migrations MUST be confined to the platform's data-access layer; the API surface and the platform's domain and application logic MUST NOT depend on persistence implementation details.
- **FR-014**: The persistence foundation MUST NOT add domain data definitions, domain queries, or domain records.

#### Explicit transactions and optimistic concurrency

- **FR-015**: Every state-changing operation MUST execute within an explicit all-or-nothing unit of work that commits only when the whole operation succeeds.
- **FR-016**: When a state-changing operation fails or is cancelled before commit, all of its changes MUST be undone, and the resources it used MUST be reusable without a leftover open unit of work.
- **FR-017**: The foundation MUST provide optimistic concurrency for records that can change: each such record carries a version, and a write that supplies an expected version MUST succeed only when it matches the persisted version; immutable records need no version.
- **FR-018**: A version mismatch MUST be reported to the caller as a distinguishable concurrency conflict, MUST NOT be treated as a successful write, and MUST leave no part of the contested change committed.
- **FR-030**: Each individual change to a versioned record's data MUST advance its version by exactly one, atomically within the same unit of work, whether or not the write supplied an expected version, including changes made by data migrations; a write that leaves the record's data unchanged MUST NOT advance its version.
- **FR-031**: Only migration orchestration MAY suppress version advancement, and only through an explicit opt-out declared by an individual migration; normal runtime data access MUST NOT be able to suppress it.
- **FR-032**: Adding, changing, or removing a record that belongs to a versioned main record MUST advance the main record's version by exactly one per individual change, under the same rules as FR-030 and FR-031; dependent records MUST NOT carry a version of their own.

#### Single-club stamp invariant

- **FR-019**: Persistence artifacts MUST treat the club as the implicit root of its deployment stamp: no application data structure, migration history field, persistence command, or persistence event MUST carry a club identifier or other cross-club discriminator.

#### Local composition and readiness

- **FR-020**: The local composition MUST provide one development-only platform database, run the migration step once the database is available, and start the platform API only after the migration step has succeeded, without requiring messaging, object storage, or other deferred infrastructure.
- **FR-021**: The platform API MUST NOT apply migrations; it MUST report ready only while the database is reachable and its migration state is current, meaning every registered migration is recorded as applied with a matching content fingerprint; readiness MUST report not ready while the database is unreachable or the migration state is not current.
- **FR-022**: When the database is unavailable, a migration fails, or a fingerprint or sequence conflict is detected, the migration step MUST fail with a diagnostic that identifies the failure category and, for migration failures, the migration identity, and the API MUST NOT report ready.
- **FR-023**: Diagnostics, logs, and telemetry produced by the persistence foundation MUST NOT contain credentials, connection secrets, or migration content.
- **FR-024**: The local composition MUST report the health of the database and the platform API and the outcome of the migration step.
- **FR-025**: The existing liveness, readiness, and API description surfaces MUST remain available with unchanged behavior apart from readiness now depending on the database and its migration state, and no domain endpoint MUST be added.
- **FR-033**: The migration step MUST be runnable as a standalone one-off unit, independently of the platform API, so that a deployment can run it before starting any number of API instances; it MUST exit with a distinguishable success or failure outcome. How production deployments schedule the step is out of scope.

#### Verification evidence and documentation

- **FR-026**: Automated verification MUST exercise migration ordering, history completeness, repeat runs, concurrent runs, fingerprint conflicts, sequence conflicts, migration rollback, unit-of-work commit and rollback, optimistic concurrency, version advancement by every write path including migrations and their explicit opt-out, version advancement of a main record by changes to its dependent records, the absence of advancement for writes that change nothing, API readiness against current and non-current migration states, the separation of migration access from runtime access, and the absence of a club discriminator against disposable, isolated database instances of the same kind the platform uses.
- **FR-027**: Automated verification MUST NOT connect to developer-owned or production databases and MUST remove every disposable database instance it creates after the run.
- **FR-028**: Automated structural checks MUST verify that persistence implementation stays in the data-access layer, that the domain and application layers do not depend on it, that every versioned record and every record that belongs to a versioned main record is covered by the version advancement of FR-030 and FR-032, and that persistence artifacts contain no club discriminator.
- **FR-029**: Repository and platform development documentation MUST describe the supported commands and prerequisites for running the local platform and its persistence verification, and MUST distinguish the implemented persistence foundation from deferred domain behavior, deferred infrastructure, and production readiness.

### Key Entities

- **Application Data Area**: The single portion of the stamp's platform database that holds all application data of all functional areas; shaped only by migrations and used by the platform's normal runtime data access.
- **Migration**: A versioned, immutable change to the application data area, identified by its position in the platform-wide sequence, a stable identity, and a content fingerprint.
- **Migration History Record**: The domain-free record of one successfully applied migration: sequence, identity, content fingerprint, and time applied; unique per identity and per sequence; kept apart from application data.
- **Unit of Work**: An explicit all-or-nothing scope within which a state change is committed entirely or not at all.
- **Migration Step**: A standalone one-off run of migration orchestration, separate from the platform API, that applies pending migrations with elevated access and ends with a success or failure outcome; any number of API instances may start only after it succeeded.
- **Record Version**: A counter on each record that can change (for a main record together with the dependent records that always change with it, on the main record only) used for optimistic concurrency; it advances by exactly one with every committed change to the record's data or to one of its dependent records, by any write path including data migrations (unless a migration explicitly opts out), and never for a write that changes nothing. A write that supplies an expected version succeeds only when it matches the current version. Immutable records have no version.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A contributor with the documented prerequisites starts the local platform with one documented command and 0 further manual steps, and sees the database and the API reported healthy and the migration step reported successful; no startup duration target applies.
- **SC-002**: On an empty database, 100% of registered migrations are applied in sequence order and each has exactly one success record in the migration history.
- **SC-003**: Across 3 consecutive starts against an already-migrated database, 0 migrations are reapplied and 0 changes occur in the application data or the migration history; across concurrent migration runs against the same database, 0 migrations are applied or recorded twice.
- **SC-004**: In 100% of fingerprint-conflict and sequence-conflict cases, 0 pending migrations are applied and the diagnostic names the migration identity, with 0 credentials or migration content exposed.
- **SC-005**: In 100% of injected migration failures, the failing migration leaves 0 partial effects and 0 success records, while all previously applied migrations remain intact.
- **SC-006**: 100% of attempts by normal runtime data access to create or alter data structures are rejected, with 0 structural changes committed.
- **SC-007**: In 100% of failed or cancelled units of work, 0 changes are committed; in 100% of successful units of work, all changes are committed.
- **SC-008**: 100% of stale-version writes are reported as concurrency conflicts with 0 partial commits; 100% of committed changes to versioned records or their dependent records, by any write path, advance the (main) record's version by exactly one per change; 0 writes that change nothing advance it; and structural checks find 0 versioned or dependent record kinds without version advancement.
- **SC-009**: Inspection of the application data area, the migration history, and persistence commands and events finds 0 club identifiers or cross-club discriminators.
- **SC-010**: 100% of persistence verification runs use disposable isolated database instances, with 0 connections to non-disposable databases and 0 instances left behind after the run.
- **SC-011**: In 100% of startup tests with an unavailable database or a failing or conflicting migration, the migration step fails with a diagnostic and the API never reports ready; in 100% of tests where the API runs against a database whose migration state is not current, the API never reports ready.
- **SC-012**: All pre-existing host and architecture checks continue to pass, the liveness, readiness, and API description surfaces remain available, and 0 domain endpoints are added.
- **SC-013**: A reviewer reading the updated development documentation can identify every supported command and prerequisite, and finds 0 statements claiming that domain behavior, deferred infrastructure, or production readiness is implemented.

## Assumptions

- **Actors**: platform developers (add migrations and persistence code), local platform operators and contributors (start and inspect the local platform), and the platform's later features (consume the persistence foundation). There is no end-user interface in this feature.
- **Platform structure**: the platform is one well-structured monolith organized in layers; functional areas (club, identity and access, recordings, registry, analysis, agent orchestration) share one application data area and are distinguished by naming inside it, not by separate data areas.
- **Initial migrations** only establish the application data area and its access rights; no domain tables, records, or endpoints are added. Behavior that needs data (rollback, concurrency) is proven with test-only migrations and test-only data.
- **Separate migration step**: migrations run in a standalone one-off migration step, never inside the platform API. Locally the composition runs the step before starting the API. Production deployments (the provisional container-composition deployment and any cloud container hosting) may run it once per deployment or once per API instance; the concurrency protection of FR-010 keeps either safe. How production schedules the step and sequences migrations across releases remains unresolved in the architecture and is deferred.
- **Unknown applied migrations** (history records not in the registered set) are tolerated with a diagnostic so that a backward-compatible older platform version can start; this is a local-foundation default and does not decide the production rollback policy, which the architecture governs separately.
- **Liveness** continues to reflect only the API process and does not depend on the database; only readiness depends on the database and its migration state.
- **Access levels**: the migration step uses access that can create and alter data structures, while the API's runtime access can only read and write application data and read the migration history. These are role semantics only; production identities, credential values, secret sources, and role-to-identity mapping are deferred.
- **Local database** is development-only; its local connection values are not production values and must not be presented as such.
- **Out of scope / deferred**: domain entities, tables, queries, and endpoints (owned by later features: club identity, recording lineage and upload, durable analysis workflow, and analyst manager registration); the transactional outbox and event publication (deferred to the feature that adopts the event backbone, although the architecture's persistence baseline includes them); messaging, object storage, authentication, and authorization; production hosting, ports, database name, credentials, secret source, role-to-identity mapping, encryption, residency, backup and restore, retention and deletion, capacity, recovery objectives, and deployment configuration, all of which the architecture leaves unresolved or blocking production; automatic down-migrations; and destructive database-reset automation.
- **Production readiness**: successful local start or passing verification is development evidence only and does not claim deployment support or production readiness.
- **Dependencies**: the existing executable platform host scaffold with its layer projects, and the existing host and architecture test suites; a documented local prerequisite for creating disposable database instances. This feature has no dependency on other specifications; the later foundation specifications listed in `specs/README.md` build on it.
- **Architecture synchronization**: where this feature makes planned persistence behavior executable or refines it (for example the data organization, the separate migration step, and the migration and runtime access levels), the owning architecture narratives are updated in the same change, as the constitution requires.
- **Architecture References**: [docs/architecture/platform-implementation.md](../../docs/architecture/platform-implementation.md), [docs/architecture/tenancy-and-technology.md](../../docs/architecture/tenancy-and-technology.md), [docs/architecture/overview.md](../../docs/architecture/overview.md), [docs/architecture/security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md), [docs/architecture/production-operations.md](../../docs/architecture/production-operations.md).
