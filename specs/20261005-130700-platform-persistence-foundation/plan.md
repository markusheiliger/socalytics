# Implementation Plan: Platform Persistence Foundation

**Branch**: `20261005-130700-platform-persistence-foundation` | **Date**: 2026-10-07 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261005-130700-platform-persistence-foundation/spec.md`

## Summary

Make the planned persistence baseline of
[Platform Implementation](../../docs/architecture/platform-implementation.md#persistence-and-cqrs)
executable without adding domain behavior. The platform gains one PostgreSQL
database per stamp with the application schema `socalytics` and the migration
history `socalytics_migrations.history`; forward-only SQL migrations embedded
in the Infrastructure layer; and a new one-off console host,
`SocAlytics.Platform.Migrator`, that is the only holder of the migration role.
It serializes runs with a PostgreSQL advisory lock and a bounded wait,
verifies checksums and ordering before applying anything, applies each
migration and its history record in one transaction, tolerates unknown applied
history with a diagnostic, and exits with distinguishable codes. The API never
migrates. Its `/health` readiness gains a `database` check that is healthy only
when the database is reachable and every registered migration is recorded with
a matching checksum. Infrastructure provides the `IUnitOfWork` abstraction
(declared in Application) over one `NpgsqlConnection` and `NpgsqlTransaction`,
Dapper-based command helpers, and a `VersionedWriteResult` concurrency-conflict
outcome. The foundation migrations create the shared trigger functions
`socalytics.advance_version()` and `socalytics.touch_aggregate_root()` plus
attachment helpers that later features' migrations call. Version suppression
works only through `socalytics.suppress_version` and only for the migration
role. The local AppHost composes `postgres:18`, then the Migrator
(`WaitFor(postgres)`), then the API (`WaitForCompletion(migrator)`). A `.sh`
init script provisions both roles from generated development-only passwords,
which persist in the AppHost user secrets. DbUp runs with
`WithTransactionPerScript`, `WithVariablesDisabled`, and a custom
`TableJournal`. Spikes A1 to A3 confirmed every one of these mechanisms. A new `SocAlytics.Platform.Integration.Tests`
project proves every behavior against disposable PostgreSQL containers
(Testcontainers) with test-only migrations. Architecture tests gain the
Migrator layering and the rule that the API references no migration execution
code.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK `10.0.400`, `rollForward: latestPatch`, pinned by `src/platform/global.json`); nullable enabled; warnings as errors (existing `Directory.Build.props`).

**Primary Dependencies**: ASP.NET Core minimal API host (existing); Npgsql `10.0.3`; Dapper `2.1.89`; `dbup-postgresql` `7.0.1` (Migrator only; brings `dbup-core` `6.1.1`); `Microsoft.Extensions.Diagnostics.HealthChecks`, `.Configuration.Abstractions`, `.Logging.Abstractions`, and `.Hosting` at `10.0.12`, the central floor for every directly referenced `Microsoft.Extensions.*` package (the existing `DependencyInjection.Abstractions` pin rises from `10.0.0`; see [research.md](research.md#r14-package-versions-and-central-management)); `Aspire.Hosting.PostgreSQL` `13.4.6` (AppHost, matching `Aspire.AppHost.Sdk/13.4.6`). All versions are managed centrally in `src/platform/Directory.Packages.props`.

**Storage**: PostgreSQL 18 (image `postgres:18` pinned by tag in the AppHost and in Testcontainers; the spikes ran 18.6). There is one database per stamp, named `socalytics` locally. It has the application schema `socalytics` and the migration history schema `socalytics_migrations` with the table `history`. Two roles: `socalytics_migrator` (DDL, owns all objects) and `socalytics_app` (runtime DML, read-only history, no DDL). See [data-model.md](data-model.md).

**Testing**: xUnit v3 `3.2.2`, Shouldly `4.3.0`, NetArchTest.Rules `1.3.2` (existing); Testcontainers.PostgreSql `4.15.0` and Microsoft.AspNetCore.Mvc.Testing `10.0.12` (new, Integration.Tests only); Aspire.Hosting.Testing `13.4.6` (existing Host.Tests). NSubstitute is not needed by this feature. Docker is a prerequisite for Host.Tests and Integration.Tests (already present on GitHub runners per `.github/actions/environment-setup`).

**Target Platform**: Linux OCI containers for the API and the Migrator (images and deployment remain deferred); local development on Windows, macOS, or Linux with a Docker-compatible container runtime through the Aspire AppHost.

**Project Type**: Web service (ASP.NET Core API) plus a one-off console host (Migrator) in one .NET solution.

**Performance Goals**: None. The spec sets no startup or throughput target (Clarifications, SC-001).

**Constraints**: The API never applies migrations and its image contains no DbUp. The readiness check is bounded by a 5-second timeout. Migrator defaults: connect timeout 60 s, lock wait 2 min, per-script timeout 5 min, all configurable. No credentials, connection secrets, or migration content appear in diagnostics. No `club_id` or other cross-club discriminator. Forward-only migrations. No domain tables, endpoints, NATS, S3, or authentication.

**Scale/Scope**: One stamp database. Two foundation migrations. One new production project (Migrator), one new test project (Integration.Tests), and additions to the existing Host.Tests and Architecture.Tests. Later foundation features (Club and Identity, Recording Lineage, Durable Analysis, Analyst Manager Registration) consume the conventions defined here.

No `NEEDS CLARIFICATION` items remain; every open technical choice is resolved in [research.md](research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-design evaluation

| Gate | Result | Justification |
| --- | --- | --- |
| I. Architecture is the design authority | PASS | The design follows [Persistence And CQRS](../../docs/architecture/platform-implementation.md#persistence-and-cqrs), [Planned Data Organization](../../docs/architecture/platform-implementation.md#planned-data-organization), [Source And Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline) (Migrator host, AppHost order), and [Planned Stamp Persistence](../../docs/architecture/tenancy-and-technology.md#planned-stamp-persistence). Refinements (role provisioning, trigger and setting names, exit outcomes, local role provisioning) are listed under [Required Architecture Updates](#required-architecture-updates). Production scheduling of the Migrator, credentials, and role-to-identity mapping stay explicitly unresolved. |
| II. Respect source-area ownership | PASS | All code lives in `src/platform/`. The new production project `SocAlytics.Platform.Migrator` is the planned host named by the architecture. The new test project lives under `src/platform/Tests/`. No new first-level `src/` child, no empty placeholders. |
| III. API-first control plane | PASS | No domain endpoint is added; the OpenAPI document keeps zero paths. Clients and agents never reach the database. Readiness stays on the existing operational surface. The component-local contracts (Migrator invocation and exit codes, migration authoring rules) live with the platform component ([contracts/](contracts/)), not as generated code. Object storage is untouched. |
| IV. Evidence over claims | PASS | Each FR and SC maps to automated tests in Integration.Tests, Host.Tests, or Architecture.Tests (see [Requirement Coverage](#requirement-coverage)). Existing host and architecture suites stay green with their documented updates. Documentation states that local start and passing tests are development evidence only. |
| V. Focused, minimal changes | PASS | Dependencies are limited to those the architecture names (Npgsql, Dapper, DbUp, Aspire PostgreSQL, Testcontainers) plus the Microsoft.Extensions abstractions they need. No outbox, NATS, S3, authentication, MediatR, or EF Core. Abstractions are limited to `IUnitOfWork`, `IUnitOfWorkScope`, and `VersionedWriteResult`, which FR-015 to FR-018 require and later features consume. |
| Technology: pinned SDK, solution, supported commands | PASS | The four documented commands stay unchanged. New projects are added to `SocAlytics.Platform.slnx`, so restore, build, and test cover them. The AppHost stays the local entry point. |
| Technology: deferred technologies adopted only by a feature that plans them | PASS | This spec and plan adopt PostgreSQL, Npgsql, Dapper, and DbUp. NATS, S3, the outbox, and authentication stay deferred. |
| Technology: `.gitignore` derived from the stack | PASS | No new generated artifacts need ignoring. Containers and volumes live in Docker, not in the worktree. The new root `.gitattributes` holds only `*.sh text eol=lf`, which the stack requires because the PostgreSQL init shell script fails with CRLF line endings. |
| Technology: environment feature rule | PASS | `environment-setup` provides the .NET SDK from `global.json` and Node. Docker is on the runner, so Testcontainers and the Aspire PostgreSQL container work. NuGet packages restore through the solution. `environment-verify` restores, builds, and tests `src/platform/SocAlytics.Platform.slnx`, which will include the Migrator and Integration.Tests, and runs the Markdown check. All changes are under `src/platform/`, Markdown files, `specs/`, and `.gitattributes`, all covered by `environment-verify`. No environment feature is needed, so none is named under Dependencies. |
| Workflow: docs, Markdown check, CI, commits | PASS | `README.md`, `AGENTS.md`, and `src/platform/README.md` get the updates listed in [Documentation Updates](#documentation-updates). The Markdown check must pass. No product CI workflow is added. No commits or pushes are made by the plan. |

### Post-design re-evaluation

| Gate | Result | Justification |
| --- | --- | --- |
| I | PASS | [data-model.md](data-model.md) and [contracts/](contracts/) keep the architecture's names (`socalytics`, `socalytics_migrations.history`, the Migrator, `version` triggers, advisory lock, unknown-applied tolerance). Every refinement has exact wording under Required Architecture Updates. Unresolved production values remain marked deferred. |
| II | PASS | The project tree below adds only `SocAlytics.Platform.Migrator` and `Tests/SocAlytics.Platform.Integration.Tests`, each with real first artifacts. `InternalsVisibleTo` exposes Infrastructure internals only to these two projects, and an architecture test pins that list. |
| III | PASS | [contracts/readiness.md](contracts/readiness.md) keeps `/alive`, `/health`, and `/openapi/v1.json` unchanged apart from database-aware readiness. No domain API. |
| IV | PASS | [quickstart.md](quickstart.md) lists runnable validations. Every acceptance scenario and SC has a named test class. Docker-dependent tests fail visibly instead of falling back to a developer database (FR-027). |
| V | PASS | DbUp stays inside the Migrator. `AddInfrastructure` keeps its `IServiceCollection`-only signature by reading configuration from DI at resolution time, so the existing composition-boundary test holds. Only two foundation migrations exist, and both are schema plumbing. |
| Environment feature rule | PASS | The design needs no SDK, tool, or service beyond the .NET 10 SDK, NuGet restore, Node, and Docker, all provided by the current actions. |

No violations exist, so Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/20261005-130700-platform-persistence-foundation/
├── plan.md              # This file
├── research.md          # Phase 0 decisions
├── data-model.md        # Phase 1 schema, roles, triggers, runtime types
├── quickstart.md        # Phase 1 runnable validation guide
├── contracts/
│   ├── readiness.md               # /alive, /health, database readiness check
│   ├── migrator-cli.md            # Migrator invocation, configuration, exit codes, diagnostics
│   ├── migration-authoring.md     # Migration naming, checksums, trigger helpers (consumed by later features)
│   └── persistence-abstractions.md # IUnitOfWork, IUnitOfWorkScope, VersionedWriteResult signatures
├── checklists/
│   └── requirements.md  # Existing spec checklist
└── tasks.md             # Created later by /speckit-tasks
```

### Source Code (repository root)

Legend: `+` new, `~` modified, `-` removed; unmarked entries are unchanged context.

```text
+ .gitattributes                          # *.sh text eol=lf (the PostgreSQL init shell script must keep LF)
src/platform/
├── ~ Directory.Packages.props            # + Npgsql 10.0.3, Dapper 2.1.89, dbup-postgresql 7.0.1,
│                                         #   Aspire.Hosting.PostgreSQL 13.4.6, Testcontainers.PostgreSql 4.15.0,
│                                         #   Microsoft.AspNetCore.Mvc.Testing 10.0.12, Microsoft.Extensions.* 10.0.12
│                                         #   (HealthChecks, Configuration/Logging abstractions, Hosting);
│                                         #   ~ M.E.DependencyInjection.Abstractions 10.0.0 -> 10.0.12
├── ~ SocAlytics.Platform.slnx            # + Migrator, + Tests/Integration.Tests
├── ~ README.md                           # commands, prerequisites, persistence status
├── SocAlytics.Platform.Domain/           # unchanged
├── SocAlytics.Platform.Application/
│   └── + Abstractions/Persistence/
│       ├── IUnitOfWork.cs
│       ├── IUnitOfWorkScope.cs
│       └── VersionedWriteResult.cs       # VersionedWriteOutcome { Applied, NotFound, ConcurrencyConflict }
├── SocAlytics.Platform.Infrastructure/
│   ├── ~ SocAlytics.Platform.Infrastructure.csproj  # Npgsql, Dapper, HealthChecks; EmbeddedResource *.sql;
│   │                                                #   InternalsVisibleTo Migrator + Integration.Tests
│   ├── ~ InfrastructureServiceCollectionExtensions.cs  # AddInfrastructure() registers persistence + "database" check;
│   │                                                   #   InfrastructureLayerMarker removed
│   └── + Persistence/
│       ├── PersistenceRegistration.cs        # internal; called by AddInfrastructure()
│       ├── DatabaseConnectionNames.cs        # "socalytics", "socalytics-migrator"
│       ├── PlatformDataSource.cs             # singleton NpgsqlDataSource from IConfiguration (lazy)
│       ├── IDbSession.cs                     # internal: connection + active transaction for Dapper code
│       ├── DbSession.cs                      # internal: scoped IUnitOfWork + IDbSession
│       ├── UnitOfWorkScope.cs                # internal: commit / rollback / dispose-rollback
│       ├── VersionedWrites.cs                # internal Dapper helper -> VersionedWriteResult
│       ├── Readiness/
│       │   └── DatabaseReadinessHealthCheck.cs
│       ├── MigrationHistory/
│       │   ├── MigrationHistorySql.cs        # history DDL + grant, exists, entries, insert SQL (used by the journal)
│       │   ├── MigrationHistoryStore.cs      # read history (missing table = empty)
│       │   └── MigrationLock.cs              # session advisory lock with bounded wait
│       └── Migrations/
│           ├── 0001_foundation_application_schema.sql
│           ├── 0002_foundation_version_triggers.sql
│           ├── MigrationScript.cs            # Sequence, Identity, Checksum, Content
│           ├── MigrationCatalog.cs           # loads + validates embedded scripts from an assembly/prefix
│           ├── MigrationChecksum.cs          # sha-256 over BOM-stripped, LF-normalized UTF-8
│           └── MigrationStateEvaluator.cs    # catalog x history -> MigrationState
├── + SocAlytics.Platform.Migrator/
│   ├── SocAlytics.Platform.Migrator.csproj   # Exe; refs Infrastructure + ServiceDefaults; dbup-postgresql;
│   │                                         #   InternalsVisibleTo Integration.Tests
│   ├── Program.cs                            # namespaced Main -> MigratorEntryPoint.RunAsync
│   ├── MigratorEntryPoint.cs                 # Generic Host build, run, exit code
│   ├── MigratorOptions.cs                    # ConnectTimeout, LockWaitTimeout, ScriptTimeout
│   ├── MigrationRunner.cs                    # connect, lock, read history, verify, DbUp apply
│   ├── SocAlyticsHistoryJournal.cs           # DbUp TableJournal subclass over socalytics_migrations.history
│   ├── PendingMigrationScriptProvider.cs     # DbUp IScriptProvider yielding only verified pending scripts
│   ├── MigratorExitCode.cs
│   └── MigratorLog.cs                        # LoggerMessage source-generated diagnostics
├── SocAlytics.Platform.Api/                  # unchanged code; readiness comes from AddInfrastructure()
├── SocAlytics.Platform.AppHost/
│   ├── ~ SocAlytics.Platform.AppHost.csproj  # + Aspire.Hosting.PostgreSQL, + Migrator ProjectReference, + <UserSecretsId>
│   ├── ~ Program.cs                          # persisted generated passwords; postgres:18 -> migrator (WaitFor(postgres))
│   │                                         #   -> api (WaitFor(postgres), WaitForCompletion(migrator))
│   └── + PostgresInit/
│       └── 01-socalytics-roles.sh            # dev-only roles from env vars; ALTER DATABASE OWNER; revokes/grants
├── SocAlytics.Platform.ServiceDefaults/      # unchanged
└── Tests/
    ├── SocAlytics.Platform.Architecture.Tests/
    │   ├── ~ SocAlytics.Platform.Architecture.Tests.csproj  # + Migrator ref, + linked Migrator/ServiceDefaults csproj
    │   ├── ~ PlatformArchitectureTests.cs                   # Migrator in AllowedProjectReferences
    │   └── + PersistenceArchitectureTests.cs                # DbUp/Migrator isolation, IVT list, SQL location, no ClubId
    ├── SocAlytics.Platform.Host.Tests/
    │   └── ~ PlatformHostTests.cs                           # postgres + migrator(exit 0) + api; registration assertions
    └── + SocAlytics.Platform.Integration.Tests/
        ├── SocAlytics.Platform.Integration.Tests.csproj     # refs Api, Migrator, Infrastructure, Application;
        │                                                    #   links AppHost PostgresInit/*.sh; embeds TestMigrations/**
        ├── Infrastructure/
        │   ├── PostgresContainerFixture.cs                  # assembly fixture: postgres:18 + init script, per-test databases
        │   ├── IsolatedDatabase.cs                          # migrator/app connection strings for one fresh database
        │   ├── TestMigrationCatalogs.cs                     # platform catalog + scenario folders
        │   └── CapturingLoggerProvider.cs                   # redaction assertions
        ├── TestMigrations/                                  # test-only, never shipped
        │   ├── Versioning/9001_test_versioned_aggregate.sql
        │   ├── VersioningData/9002_test_backfill_advances.sql, 9003_test_backfill_suppressed.sql
        │   ├── Failing/9001_test_fails_midway.sql
        │   ├── Slow/9001_test_slow.sql
        │   ├── Marker/9001_test_content_marker.sql
        │   └── StructureViolations/9001_test_structure_violations.sql
        ├── Migrations/      # ordering, repeat, checksum, sequence, rollback, concurrency, unknown applied, exit codes, redaction
        ├── Access/          # runtime role cannot change structures or history
        ├── Transactions/    # unit of work commit, rollback, cancellation, reuse
        ├── Concurrency/     # version advancement, conflicts, child roots, migrations + opt-out
        ├── Structure/       # triggers present, child triggers, no club_id, classification manifest
        └── Readiness/       # API /health against current, pending, mismatched, unreachable, recovering databases
```

**Structure Decision**: The layered monolith keeps its four layer projects. Shared persistence plumbing lives in `SocAlytics.Platform.Infrastructure.Persistence` and the application-facing abstractions in `SocAlytics.Platform.Application.Abstractions.Persistence`. The only new production project is the architecture-named `SocAlytics.Platform.Migrator`, which owns DbUp and the run lifecycle and gets all SQL and migration state logic from Infrastructure. The only new test project is `src/platform/Tests/SocAlytics.Platform.Integration.Tests`, which later features extend.

## Design Overview

The detailed decisions are in [research.md](research.md), the schema and types in [data-model.md](data-model.md), and the external behavior in [contracts/](contracts/). The main flows are:

1. **Local start** (`dotnet run --project src/platform/SocAlytics.Platform.AppHost`). The AppHost first starts `postgres` (`postgres:18`, named volume `socalytics-postgres-data`). The entrypoint creates database `socalytics` from `POSTGRES_DB`. On first initialization of an empty data directory, `PostgresInit/01-socalytics-roles.sh` creates `socalytics_migrator` and `socalytics_app`. It reads their passwords from `SOCALYTICS_*_PASSWORD` environment variables, which come from `GenerateParameterDefault` parameters persisted to the AppHost user secrets. It then transfers database ownership to `socalytics_migrator`. Next, `migrator` waits for the `postgres` health check (`WaitFor(postgres)`), receives `ConnectionStrings__socalytics-migrator` (an `AddConnectionString` over a `ReferenceExpression`), runs once, and exits. Finally, `api` receives `ConnectionStrings__socalytics` (runtime role) and starts only after `migrator` finished with exit code 0 (`WaitForCompletion(migrator)`). The dashboard shows the PostgreSQL health, the Migrator's finished state and exit code, and the API health.
2. **Migrator run**:
   1. Validate configuration and the embedded catalog.
   2. Connect with bounded retry.
   3. Take the session advisory lock with a bounded wait.
   4. Read the history (a missing table counts as empty) and evaluate it against the catalog. Fail on a checksum mismatch or sequence conflict; report unknown applied migrations.
   5. Apply the verified pending scripts with DbUp, one transaction per script that includes its history row. The `SocAlyticsHistoryJournal` (`TableJournal` subclass) creates the history table in the first script's transaction when it is missing.
   6. Release the lock and exit `0`. Failures exit with the codes in [contracts/migrator-cli.md](contracts/migrator-cli.md).
3. **API readiness**. The `database` health check, registered by `AddInfrastructure()` and not tagged `live`, reads `socalytics_migrations.history` with the runtime role. It evaluates the same `MigrationStateEvaluator` against the catalog embedded in Infrastructure and reports Healthy only for a current state. Connect and query run under an internal 3-second budget, so an unreachable or hung database reports `database-unavailable` within the 5-second check timeout. `/alive` is unaffected.
4. **State change** (pattern for later features). A command handler calls `IUnitOfWork.BeginAsync` and performs Dapper writes through Infrastructure code bound to the scoped `IDbSession`. Guarded edits use `VersionedWrites`, which returns `VersionedWriteResult`. The handler commits, or disposes the scope to roll back, which also happens on conflict, exception, or cancellation. Triggers advance `version`.

## Requirement Coverage

| Requirement | Design element | Evidence (test class, planned) |
| --- | --- | --- |
| FR-001, FR-002, FR-003, FR-004, SC-002, SC-003 | Catalog ordering, `history` PK/UNIQUE, pending computation | `Migrations/MigrationOrderingTests`, `Migrations/RepeatRunTests` |
| FR-005, FR-006, SC-004 | `MigrationStateEvaluator`, `MigrationCatalog` validation | `Migrations/ChecksumVerificationTests`, `Migrations/SequenceConflictTests`, `Migrations/MigrationCatalogTests` |
| FR-007, FR-008, SC-005 | DbUp transaction per script including history insert; no down scripts | `Migrations/MigrationRollbackTests` |
| FR-009 | Unknown applied tolerated and logged (Migrator and readiness) | `Migrations/UnknownAppliedMigrationTests`, `Readiness/DatabaseReadinessTests` |
| FR-010, SC-003 | Session advisory lock, bounded wait, re-read history after lock | `Migrations/ConcurrentMigrationRunTests` |
| FR-011, FR-012, SC-006 | One schema; role grants and default privileges; DB-level revokes | `Access/RuntimeRoleAccessTests` |
| FR-013, FR-014, FR-028 | SQL only in Infrastructure; DbUp only in Migrator; no domain tables | `PersistenceArchitectureTests`, `Structure/PersistenceStructureTests` |
| FR-015, FR-016, SC-007 | `IUnitOfWork` / `DbSession` / `UnitOfWorkScope` | `Transactions/UnitOfWorkTests` |
| FR-017, FR-018, FR-030, FR-031, FR-032, SC-008 | `version` column, `advance_version`, `touch_aggregate_root`, suppression check, `VersionedWrites` | `Concurrency/VersionAdvancementTests`, `Concurrency/OptimisticConcurrencyTests`, `Concurrency/MigrationVersionAdvancementTests`, `Structure/PersistenceStructureTests` |
| FR-019, SC-009 | No `club_id`/`tenant_id` columns; no `ClubId` members | `Structure/PersistenceStructureTests`, `PersistenceArchitectureTests` |
| FR-020, FR-024, FR-025, SC-001, SC-012 | AppHost order and health; unchanged endpoints | `PlatformHostTests` (Host.Tests) |
| FR-021, FR-022, SC-011 | `database` readiness check; Migrator failure exit codes; API not started after a failed Migrator | `Readiness/DatabaseReadinessTests`, `Migrations/MigratorExitCodeTests`, `PlatformHostTests` (Host.Tests) |
| FR-023 | Sanitized diagnostics, no connection string or script text | `Migrations/MigratorDiagnosticsRedactionTests`, `Readiness/DatabaseReadinessTests` |
| FR-026, FR-027, SC-010 | Testcontainers fixture, per-test databases, Ryuk cleanup, no fallback | `Infrastructure/PostgresContainerFixture` (all integration tests) |
| FR-029, SC-013 | README, AGENTS, platform README updates | Markdown check plus review |
| FR-033 | Standalone Migrator host and exit codes | `Migrations/MigratorExitCodeTests`, quickstart scenario 6 |

## Documentation Updates

The implementation must make these documentation changes in the same change:

- `README.md` → *Development → Platform Host*:
  - State the new prerequisite: a running Docker-compatible container runtime for `dotnet run --project src/platform/SocAlytics.Platform.AppHost` and for `dotnet test` (Host.Tests and Integration.Tests).
  - Describe what the AppHost starts: PostgreSQL, the Migrator, then the API.
  - Add the standalone Migrator command and the targeted integration-test command from [quickstart.md](quickstart.md).
  - Replace "PostgreSQL persistence with Dapper and DbUp … remain deferred" with an accurate statement: the persistence foundation (schema, migrations, Migrator, units of work, optimistic concurrency, database-aware readiness) is implemented as development evidence. Domain data and behavior, NATS, S3-compatible storage, the transactional outbox, authentication, deployment images and configuration, production credentials, and production readiness remain deferred.
  - Clarify that "Docker support" still means deployment images and Compose files (deferred), not the local container runtime prerequisite.
- `AGENTS.md`:
  - *Current State*: rewrite the bullet that begins "Current executable evidence is limited to the dependency-free API host" so that "dependency-free", "Aspire local composition of the API alone", and "internal layer markers" (the Infrastructure marker is removed) no longer appear, and replace "No domain behavior, PostgreSQL/Dapper/DbUp, …" with the implemented persistence-foundation evidence and the remaining deferrals.
  - *Repository Setup*: add the Docker prerequisite, the Migrator command, and the integration-test command. State that tests must never target developer or production databases.
  - New conventions:
    - Migrations are forward-only files `NNNN_<area>_<description>.sql` under `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/`, numbered at implementation time as the next free number on an up-to-date `main` (features merge sequentially; a feature renumbers before merge if its number was taken).
    - Applied migrations are never edited.
    - Versioned tables call the trigger helpers from [contracts/migration-authoring.md](contracts/migration-authoring.md).
    - Every new table gets an entry in the Integration.Tests table classification manifest.
- `src/platform/README.md`:
  - *Current Status*: replace "a dependency-free ASP.NET Core API" and the paragraph beginning "Domain behavior, PostgreSQL persistence with Dapper and DbUp"; describe the Migrator and Integration.Tests projects, the AppHost order, the readiness semantics, and only the remaining deferrals.
  - *Development*: the commands above, the dev-only local credentials (generated and kept in AppHost user secrets), and the manual local reset (`docker volume rm socalytics-postgres-data` after stopping the AppHost; development data only).
  - A short component-local contract section that links the Migrator invocation and exit codes and the migration authoring rules. Their content moves from this plan's `contracts/` into the component documentation when implemented.

## Required Architecture Updates

The coordinator applies the design refinements (A) with this plan. The current-state evidence updates (B) are applied by the implementation change once the evidence exists.

### A. Design refinements

1. `docs/architecture/platform-implementation.md`, section *Planned Data Organization*: replace the sentence "Migration history is kept apart from domain data in `socalytics_migrations.history`, which records the migration sequence and checksum but contains no domain state." with:

   > Migration history is kept apart from domain data in `socalytics_migrations.history`, which records each applied migration's sequence, identity, SHA-256 checksum, and application time but contains no domain state. Two database roles separate access: `socalytics_migrator` owns both schemas and is the only role that creates or alters data structures, and `socalytics_app` may read and write tables in `socalytics` and read the migration history. The environment provisions both roles and their login identities before the Migrator first runs; migrations grant privileges but never create roles or credentials.

2. Same file, section *Persistence And CQRS*. In the Migrator bullet list, after the bullet that begins "a Migrator run that starts while another is in progress", add:

   > - before applying anything, the Migrator verifies that every applied migration it knows still has a matching checksum (computed over the script text with line endings normalized) and that no pending migration is numbered at or below the highest applied one; it applies each pending migration together with its history record in one transaction and exits with `0` on success or a distinct non-zero code per failure category (configuration, database unavailable, invalid migration set, checksum mismatch, sequence conflict, migration failure, lock-wait timeout, cancellation); diagnostics name the failing migration but never contain credentials, connection secrets, or migration content;

3. Same file, section *Persistence And CQRS*, trigger bullet list:
   - Replace "one shared `BEFORE UPDATE` trigger function, attached to every table with a `version` column," with "one shared `BEFORE UPDATE` trigger function, `socalytics.advance_version()`, attached to every table with a `version bigint not null default 1` column,".
   - Replace "`AFTER INSERT`, `UPDATE`, and `DELETE` triggers on an aggregate's child tables touch the aggregate root" with "`AFTER INSERT`, `UPDATE`, and `DELETE` triggers on an aggregate's child tables, using the shared function `socalytics.touch_aggregate_root()`, touch the aggregate root".
   - Replace "a migration may suppress the increment for its own transaction with a transaction-local setting that only migration scripts use;" with "a migration may suppress the increment for its own transaction with the transaction-local setting `socalytics.suppress_version = 'on'`; the trigger functions honor it only when the current role is a member of `socalytics_migrator`, so runtime access cannot suppress it;".

4. Same file, section *Source And Runtime Baseline*, after "Locally, the Aspire AppHost will mirror that order: PostgreSQL, then the Migrator, then the API, which waits for the Migrator to complete." add:

   > The local PostgreSQL container provisions both database roles from a committed initialization script with generated development-only passwords and keeps its data in a named development volume; these local values are not production values, and production credentials, role-to-identity mapping, and the scheduling of the Migrator remain unresolved.

### B. Evidence updates (in the implementation change)

1. `docs/architecture/platform-implementation.md`, *Source And Runtime Baseline*:
   - Add `SocAlytics.Platform.Migrator` to the project list and `Tests/SocAlytics.Platform.Integration.Tests` to the test projects.
   - Change "references only the API" to "references the API and the Migrator".
   - Change "The persistence foundation will add a second, planned host next to the API:" to "The persistence foundation adds a second host next to the API:".
   - Keep the following "It ships as its own OCI image …" sentence in future tense ("It will ship as its own OCI image and will be the only component that ever receives database access able to create or alter data structures; the API image will contain no migration execution path."), because images and deployment remain deferred.
2. Same file, *Control Plane*, first paragraph: replace "a dependency-free ASP.NET Core host" with "an ASP.NET Core host", and replace "The Aspire AppHost composes only the API resource and uses `/health` for readiness." with "The Aspire AppHost composes PostgreSQL, the one-off Migrator, and the API in that order; the API's `/health` readiness includes a `database` check that requires a reachable database with a current migration state." In *API And Identity*, replace "The current dependency-free API" with "The current API".
3. Same file, *Persistence And CQRS*: replace the first paragraph ("This persistence and messaging baseline remains unimplemented; …") with "The persistence foundation implements PostgreSQL access with Npgsql and Dapper, DbUp migrations applied by the Migrator, explicit units of work, and trigger-managed versions; NATS JetStream, S3-compatible storage, the transactional outbox, and all domain tables remain unimplemented."
4. Same file, *Test And Observability Baseline*: append "Testcontainers runs disposable PostgreSQL instances for `SocAlytics.Platform.Integration.Tests`."
5. Same file, *Planned Acceptance Evidence*:
   - Replace the bullet "Aspire starts the API as its sole resource and reports `/health` readiness;" with "Aspire starts PostgreSQL, the one-off Migrator, and the API in that order and reports `/health` readiness, including the `database` check;".
   - Add to the evidence list: "PostgreSQL integration tests cover ordered checksum-aware migrations, repeat and concurrent Migrator runs with the bounded lock wait, rollback of failing migrations, unknown applied migrations, distinguishable Migrator exit outcomes, explicit units of work, optimistic concurrency, the version guarantee including child-root and migration behavior, trigger presence, absence of `club_id`, runtime-role access limits, and API readiness against current and non-current migration states".
   - Remove those items from the validation-target bullets, leaving Dapper mappings of domain records, idempotency, authorization, immutable lineage, registry versions, analysis recovery, and state-plus-outbox atomicity as targets.
6. Same file, *Architecture Reassessment*:
   - In "Consequences and operational tradeoffs understood", replace "Layer dependency rules, host composition, and dependency-free startup are exercised." and move local transaction boundaries and local database unavailability from the unevidenced list to the exercised list, together with host composition with PostgreSQL and the Migrator. Duplicate delivery, restart recovery, other local infrastructure failure, production database access control, and the remaining items stay unevidenced.
   - In "Prototype, measurement, or implementation evidence supports the choice", change "Persistence, publication, …" to "Domain persistence, publication, …" and add "the persistence foundation (migrations, Migrator, units of work, version triggers)" to the implemented items.

## Risk Register

| ID | Risk | Disposition | Evidence / Owner | Revisit trigger |
| --- | --- | --- | --- | --- |
| PF-R1 | Aspire 13.4.6 lacks or changes the members the AppHost relies on (`WithInitFiles`, `GenerateParameterDefault` persistence, `AddConnectionString` with `ReferenceExpression`, `WaitFor`, `WaitForCompletion`) | Mitigated | Spike A1(a)–(d) confirmed every member with 0 build warnings. The API started only after the Migrator exited 0 on every run. Design: [research.md R6](research.md#r6-local-role-provisioning-and-apphost-wiring). | Aspire SDK or `Aspire.Hosting.PostgreSQL` version change |
| PF-R2 | Role passwords cannot reach the init script (a static `.sql` file cannot read secrets) | Mitigated | Spike A1(a): a `.sh` init file reads `SOCALYTICS_*_PASSWORD` set by `WithEnvironment`, and the custom roles connected. Design: `PostgresInit/01-socalytics-roles.sh`. | Change of the PostgreSQL image entrypoint |
| PF-R3 | The `.sh` init script breaks on Windows checkouts with CRLF endings | Mitigated | Root `.gitattributes` `*.sh text eol=lf`. Integration.Tests run the same script in every container start, so a broken script fails tests. | New shell scripts or `.gitattributes` changes |
| PF-R4 | DbUp splits or mangles dollar-quoted PL/pgSQL, or writes the journal outside the script transaction | Mitigated | Spike A2(a): `$$` works; named `$tag$` quotes need `WithVariablesDisabled()`, which the design sets. Spike A2(b): `TableJournal` subclass plus `WithTransactionPerScript()` share one transaction id, and failures roll back the script, journal row, and history table creation. Re-proven by `MigrationRollbackTests` and migration `0002`. | DbUp major or minor version change |
| PF-R5 | Migration identity drifts when the namespace or folder changes (DbUp names scripts by manifest resource) | Mitigated | Spike A2(c). Design: identity is the file name without `.sql` ([data-model.md](data-model.md#migration-catalog-entry)). | Change of the catalog loader |
| PF-R6 | Checksums differ between Windows and Linux checkouts | Mitigated | Spike A2(c): SHA-256 over BOM-stripped content with CRLF and CR normalized to LF gives the same hash for CRLF and LF files. | Change of the checksum algorithm |
| PF-R7 | History `sequence` gaps after failed scripts | Mitigated | Spike A2(b) showed gaps with an identity column. Design: `sequence` is derived from the script number. | — |
| PF-R8 | Package downgrade errors (NU1605) under warnings-as-errors | Mitigated | Spike A3: the full graph restored and built clean. Central floor `Microsoft.Extensions.*` 10.0.12 removes the reproduced test-project NU1605. Dapper 2.1.89 confirmed latest stable. | Any package version change in `Directory.Packages.props` |
| PF-R9 | Docker is required for Host.Tests and Integration.Tests and for the local AppHost | Accepted | Documented prerequisite in `README.md`, `AGENTS.md`, and `src/platform/README.md`. GitHub runners provide Docker (`environment-setup`). The tests fail visibly without Docker and never fall back (FR-027). | A required CI runner without Docker |
| PF-R10 | Host test duration grows (image pull plus Migrator run) | Accepted | The AppHost test timeout rises from 2 to 5 minutes. Pulled images are cached on runners between steps. | Host test flakiness from timeouts |
| PF-R11 | Local password drift: the persisted passwords are lost while the data volume keeps the old roles, because init does not rerun on a populated volume | Mitigated | `<UserSecretsId>` plus `GenerateParameterDefault` with `persist: true` gave a stable value across runs (spike A1(b)). The documented manual reset `docker volume rm socalytics-postgres-data` recovers ([quickstart.md](quickstart.md#8-reset-the-local-development-database-manual-development-data-only)). | Reports of local login failures |
| PF-R12 | The container image drifts under a floating default tag (the Aspire default may move past 18, with a different volume layout) | Mitigated | `postgres:18` is pinned by tag in the AppHost (`WithImageTag("18")`) and in Testcontainers. Patch updates within 18 are accepted. | PostgreSQL major upgrade decision |
| PF-R13 | The PostgreSQL health check briefly reports Unhealthy during the entrypoint's init restart | Accepted | Spike A1(a): expected behavior, and `WaitFor(postgres)` absorbs it. | — |
| PF-R14 | Production scheduling of the Migrator, role-to-identity mapping, credentials, and secret source are unresolved | Deferred | Owner: production operations profile ([production-operations.md](../../docs/architecture/production-operations.md)) and the future deployment feature. This plan adds no deployment configuration. | Start of the deployment or production-profile feature |
| PF-R15 | The API image could gain migration code through a future reference | Mitigated | `PersistenceArchitectureTests`: DbUp only in the Migrator, no Migrator reference from the API, and `InternalsVisibleTo` limited to the Migrator and Integration.Tests. | Changes to project references |

## Complexity Tracking

No constitution violations; nothing to justify.
