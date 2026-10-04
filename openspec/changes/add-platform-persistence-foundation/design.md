# Platform Persistence Foundation Design

## Context

See `proposal.md` for motivation and the delta specs for required behavior. The current .NET 10 solution has six peer capability projects, one API, one Aspire AppHost, shared service defaults, host tests, and architecture tests. AppHost currently starts only the API; capability projects expose one public dependency-injection method each and do not reference one another. There are no database packages, persistence projects, migrations, or infrastructure tests.

The adopted architecture already selects one PostgreSQL database per single-club stamp, Npgsql, Dapper, DbUp, explicit transactions, optimistic concurrency, module-owned schemas, and `socalytics_migrations.history`. This design implements that adopted local foundation without adding domain state or resolving production configuration.

Impact categories:

- **UX:** Unaffected; no client, route, workflow, or accessibility behavior changes.
- **Architecture:** Adds a shared peer persistence boundary, a PostgreSQL integration-test project, local database composition, and database-enforced module schema isolation.
- **Security and governance:** Adds least-privilege owner/runtime role semantics for local and test databases and verifies cross-schema denial. Production identities, secrets, encryption, residency, retention, audit, backup, and recovery remain governed elsewhere and unimplemented.
- **Implementation:** Adds centrally versioned Aspire PostgreSQL, Npgsql, Dapper, DbUp, and Testcontainers dependencies; six schema-only module migration sets; startup migration orchestration; transaction/concurrency primitives; and focused tests.
- **Documentation:** During apply, synchronize `docs/architecture/platform-implementation.md`, `docs/architecture/tenancy-and-technology.md`, root `README.md`, `src/platform/README.md`, and `AGENTS.md` with executable evidence and supported commands.

## Goals / Non-Goals

**Goals:**

- Give all capability modules one reusable connection, transaction, concurrency, migration, and checksum implementation without placing domain SQL in the shared boundary.
- Make schema ownership and normal-runtime cross-schema denial executable in local and test PostgreSQL.
- Make migrations deterministic, forward-only, transactional per script, checksum-verified before new work, and safe to run repeatedly.
- Preserve the existing capability reference graph and narrow public composition surfaces.
- Produce focused architecture, host, and real-PostgreSQL evidence for every persistence-foundation requirement.

**Non-Goals:**

- Domain entities, repositories, handlers, tables, projections, identity stores, outbox records, or API endpoints.
- NATS JetStream, S3-compatible storage, authentication, authorization, or production deployment configuration.
- Down migrations, destructive database reset automation, or a general-purpose ORM/repository abstraction.
- Production role identities, credential delivery, secret values, ports, database names, operational objectives, backup/restore, or lifecycle policy.

## Decisions

### 1. Add one shared peer persistence project

Add `SocAlytics.Platform.Persistence` as a peer production project under `src/platform`. It owns only module-neutral infrastructure: Npgsql data-source configuration, Dapper integration support, explicit transaction execution, optimistic-concurrency result/exception semantics, migration descriptors, checksum verification, the shared journal, and migration orchestration. It owns `socalytics_migrations` but no capability schema or domain record.

Each capability project references Persistence, never another capability. Its existing public composition method registers an internal migration contributor and internal module-scoped persistence services against the shared contracts. Capability assemblies continue to export only their composition type. The API may call the shared persistence registration/startup boundary but must not consume module persistence implementations.

Alternatives considered:

- Put helpers in ServiceDefaults: rejected because ServiceDefaults owns cross-cutting hosting/telemetry defaults, not database policy or migration state.
- Duplicate connection and migration code in every module: rejected because checksum, transaction, startup, and isolation behavior would diverge.
- Put orchestration in the API: rejected because it couples the host to module migration details and makes reuse/testing harder.

This new shared boundary is a durable architecture refinement and an **ADR candidate**. During apply, record the shared-boundary and database-role decision in `docs/architecture/decisions/` if the architecture owner confirms it meets the repository's ADR threshold, then link it from the synchronized narratives.

### 2. Keep migrations embedded and owned by modules

Each capability embeds SQL resources under an internal migration namespace. A migration descriptor carries a stable module key, monotonically increasing module-local sequence, stable script identity, embedded bytes, and SHA-256 checksum. No module can register a migration for another module key, and architecture tests map each embedded resource to exactly one adopted schema.

The orchestrator uses the adopted module order (`club`, `identity_access`, `recordings`, `registry`, `analysis`, `agent_orchestration`) and then module-local sequence when selecting pending work. Modules do not declare cross-module migration dependencies. The initial migrations create only their owned schema and grants; test-only migration contributors provide disposable objects needed to prove ordering, rollback, and concurrency without introducing product domain tables.

Alternatives considered:

- Filesystem migrations: rejected because deployment working-directory behavior is less deterministic than assembly-embedded resources.
- One shared migration assembly: rejected because it transfers SQL ownership away from capability modules.
- Timestamp-only global ordering: rejected because parallel module work would create unnecessary coordination and implied cross-module coupling.

### 3. Extend DbUp with a checksum-aware shared journal

DbUp executes PostgreSQL scripts, but the shared orchestration owns journal policy. `socalytics_migrations.history` records module key, module-local sequence, stable script identity, SHA-256 checksum, and successful application time, with uniqueness on module plus script identity and module plus sequence.

Before applying any pending script, orchestration reads the complete applied history and validates every matching embedded script checksum. Any mismatch fails preflight before new migrations run. Matching entries are skipped. Pending scripts execute one at a time in deterministic order, each within its own transaction; the history insert commits in the same transaction as the script. A failed script therefore leaves neither partial schema effects nor a success record, while earlier committed scripts remain intact.

Alternatives considered:

- DbUp's default name-only journal: rejected because it cannot detect edited applied scripts.
- One transaction for every module and script: rejected because it increases lock duration and causes an unrelated later failure to roll back already valid module work.
- Automatic down migrations: rejected because rollback scripts are difficult to make universally safe; product rollback remains forward-compatible and operationally explicit.

### 4. Enforce schema isolation with owner and runtime roles

For local and test databases, shared bootstrap creates stable NOLOGIN owner and runtime roles for each adopted module. A module owner role owns only its schema and migration objects. Its runtime role receives only the schema usage and object privileges needed by normal module access; public and peer-module access are revoked. Migration execution assumes the owning module's owner role, while normal module sessions assume its runtime role.

The shared connection factory produces module-scoped sessions and applies the role at connection/session setup. Modules cannot request arbitrary schema names through public API; their internal registration binds a fixed adopted module identity. PostgreSQL tests execute cross-schema reads and writes through these normal module sessions and require permission failures. The bootstrap/admin connection is restricted to migration orchestration and is not injectable into module application services.

This establishes role semantics, not production credential values. A future production profile must map deployment identities and secrets to these privileges without changing the capability contract.

Alternatives considered:

- Rely only on naming conventions and architecture tests: rejected because they do not prevent an accidental cross-schema query at runtime.
- Give the API login ownership of every schema: rejected because it makes isolation advisory.
- Use one database per module: rejected because the adopted stamp architecture selects one logical database and logical CQRS does not require distributed transactions.

### 5. Require explicit transactions and a narrow concurrency primitive

The shared transaction executor opens a module-scoped Npgsql connection, begins a transaction, invokes the operation with the same connection and transaction, commits on success, and rolls back on exception or cancellation. It never hides nested or ambient transaction behavior; callers pass the transaction to Dapper commands explicitly. Read paths may use module-scoped connections without a write transaction, but all state-changing helpers require one.

Optimistic concurrency remains a SQL pattern owned by modules: an update includes its expected `BIGINT` version and atomically increments it. Shared infrastructure only standardizes validation of the affected-row count and the concurrency-conflict signal. It does not define entities, repositories, tables, or a universal base record.

Alternatives considered:

- `TransactionScope`: rejected because ambient transactions obscure ownership and can escalate unexpectedly.
- A generic repository/unit-of-work framework: rejected because it leaks persistence models across module boundaries and constrains purpose-built Dapper SQL.
- PostgreSQL `xmin`: rejected because an explicit application version is clearer, portable across projections, and under module schema control.

### 6. Run migrations before readiness succeeds

AppHost adds one Aspire PostgreSQL server/database resource, passes its reference to the API, waits for PostgreSQL, and keeps PostgreSQL local-development only. API startup registers shared persistence and all module contributors, runs migration orchestration once, and reports readiness only after successful completion. A checksum conflict, unavailable database, or migration failure keeps readiness unhealthy and surfaces a sanitized diagnostic.

The existing `/alive`, `/health`, and `/openapi/v1.json` paths remain unchanged, and no domain route is added. Host tests continue to start the full AppHost and now wait for both PostgreSQL and API health.

Alternatives considered:

- A separate migration executable: deferred because the current local modular-monolith host benefits from one startup path; production promotion and deployment migration sequencing remain unresolved.
- Lazy migration on first request: rejected because it introduces races and permits traffic before schema readiness.

### 7. Separate structural, host, and PostgreSQL evidence

Extend `SocAlytics.Platform.Architecture.Tests` to verify capability project-reference isolation, exported-type limits, API access limits, module migration-resource ownership, no cross-schema SQL ownership, and no `club_id` token in product persistence artifacts. Extend host tests for Aspire PostgreSQL composition, migration-gated readiness, repeat startup, and unchanged operational/OpenAPI behavior.

Host tests prove repeat startup against an already-migrated database without depending on slow process shutdown. Aspire's testing host disables the dashboard but still injects `OTEL_EXPORTER_OTLP_ENDPOINT` (`http://localhost:4317`) into project resources ([microsoft/aspire#3349](https://github.com/microsoft/aspire/issues/3349), open; no fix up to Aspire 13.6.0). On shutdown, the API's OTLP exporter then blocks against the unserved endpoint, so stopping or restarting the API in a host test takes 8–18 seconds, exceeds Aspire's stop timeout on Linux CI, and fails or crashes the test host. A host test that stops or restarts the API therefore removes `OTEL_EXPORTER_OTLP_ENDPOINT` from the API resource's environment in the test and waits for a terminal state, because a stopped project resource ends as `Finished`, not `Exited`. Starting a second, explicitly started API resource against the same database is an equivalent way to prove repeat startup. Product ServiceDefaults stay unchanged.

Add `Tests/SocAlytics.Platform.Persistence.Tests` using PostgreSQL Testcontainers. Use fresh disposable databases and test-only contributors to cover deterministic ordering, complete history, checksum conflict preflight, repeat runs, failed-script rollback, transaction rollback, optimistic concurrency, schema ownership/privileges, cross-schema denial, and catalog-level absence of `club_id`. Collection fixtures may share a container where isolation remains database-scoped; tests that mutate migration history use dedicated databases.

The supported `dotnet test` workflow requires an available supported container runtime. Tests must not fall back to developer-installed or production databases.

## Risks / Trade-offs

- **[Startup migrations increase API startup time and couple readiness to PostgreSQL]** -> Keep scripts small, instrument migration duration without SQL bodies, and fail readiness with sanitized diagnostics.
- **[A shared project could become a general dumping ground]** -> Limit its public surface and architecture tests to module-neutral connection, transaction, concurrency, and migration concerns; keep all domain SQL in modules.
- **[Admin bootstrap access is more privileged than normal module access]** -> Keep it isolated to startup orchestration, exclude it from module DI, and test runtime-role denial explicitly.
- **[Embedded-script edits after application cause startup failure]** -> Treat applied migrations as immutable, require a new sequence for corrections, and make the checksum conflict identify the exact module/script.
- **[Container-backed tests are slower and require Docker-compatible infrastructure]** -> Keep fast architecture tests separate, reuse containers only with database isolation, and document the prerequisite truthfully.
- **[Aspire's testing host injects an unserved OTLP endpoint, which makes API shutdown slow in host tests]** -> Host tests that stop or restart the API remove `OTEL_EXPORTER_OTLP_ENDPOINT` from that resource and wait for any terminal state, or prove repeat startup with a second, explicitly started API resource; revisit when microsoft/aspire#3349 is fixed.
- **[Token-based `club_id` checks can miss semantic aliases or flag comments]** -> Combine source/resource inspection with PostgreSQL catalog inspection and keep the single-club rule visible in review criteria.
- **[Role semantics may map differently to future production identities]** -> Specify privileges and denial behavior now while leaving providers, secret sources, login names, and credential delivery to the production profile.

## Migration Plan

1. Add centrally managed package versions, the shared persistence project, and the PostgreSQL integration-test project to the solution; retain a compiling host at each step.
2. Implement shared descriptors, checksum journal, role bootstrap, transaction execution, and concurrency signaling with focused Testcontainers tests.
3. Add one internal schema-only migration contributor to each capability and extend architecture tests before wiring runtime startup.
4. Add API migration/readiness composition, then add Aspire PostgreSQL and update host tests.
5. Synchronize current architecture and development guidance, resolve the ADR candidate, and run restore, build, all tests, OpenSpec validation, and independent verification/audit.

Application rollback removes the new host wiring and package/project references but does not automatically drop a developer database. Existing schema-only objects and migration history are harmless to the prior dependency-free host and may be removed only through an explicit local reset. Migration execution itself is forward-only: a failed script rolls back atomically, while a correction to an applied migration is delivered as a new migration rather than editing history or running an automatic down script.
