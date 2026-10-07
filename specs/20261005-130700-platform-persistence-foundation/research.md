# Research: Platform Persistence Foundation

Each topic records the decision, its rationale, and the alternatives considered.
Architecture references point to
[platform-implementation.md](../../docs/architecture/platform-implementation.md)
unless stated otherwise.

## R1. Migrator host and where DbUp lives

- **Decision**: Add `src/platform/SocAlytics.Platform.Migrator`, an `Exe` built on the .NET Generic Host (`Host.CreateApplicationBuilder`). It references `SocAlytics.Platform.Infrastructure` and `SocAlytics.Platform.ServiceDefaults` and is the only project with a `PackageReference` to `dbup-postgresql`. Infrastructure owns all SQL: the embedded migration scripts, the history DDL, the journal statements used by the Migrator's `TableJournal` subclass (`MigrationHistorySql`), lock statements, and history queries. It also owns the migration catalog, the checksum, and the state evaluator. The Migrator owns DbUp wiring, the run lifecycle, options, and exit codes. `Program.cs` declares `namespace SocAlytics.Platform.Migrator` with an explicit `Main`, so test projects that also reference the API do not see two global `Program` types. The Migrator calls `AddServiceDefaults()` for OpenTelemetry logs and traces in the Aspire dashboard, and starts and stops the host around the run so the exporters flush.
- **Rationale**: The architecture requires that the API image contain "no migration execution path" and that the Migrator be "the only component that ever receives database access able to create or alter data structures". Keeping DbUp out of Infrastructure removes it from the API's dependency closure entirely. Architecture tests can then prove that cheaply (see R16). The readiness check still needs the catalog and checksums, so those stay in Infrastructure.
- **Alternatives considered**:
  - DbUp in Infrastructure: rejected because DbUp.dll would ship in the API image and the "no migration code in the API" rule could only be enforced by convention.
  - Running migrations from the API at startup: rejected by the spec clarification and FR-021.
  - A separate `Persistence` class library: rejected because the conventions forbid adding projects beyond the Migrator.

## R2. Migration catalog, naming, and checksum

- **Decision**:
  - **Location and naming**: Migrations are `EmbeddedResource` `.sql` files under `SocAlytics.Platform.Infrastructure/Persistence/Migrations/`, named `NNNN_<area>_<description>.sql` and matching `^(?<sequence>[0-9]{4})_(?<area>[a-z][a-z0-9]*)_(?<description>[a-z0-9]+(?:_[a-z0-9]+)*)\.sql$`.
  - **Sequence and identity**: The sequence is the integer prefix (1 to 9999). The identity is the file name without `.sql`, for example `0001_foundation_application_schema`.
  - **Checksum**: SHA-256 over the UTF-8 bytes of the script after removing a leading BOM and normalizing CRLF and CR to LF, stored as `sha-256:<64 lowercase hex>`.
  - **Catalog validation**: `MigrationCatalog` loads the scripts of one assembly and resource prefix (the platform catalog, or test catalogs in Integration.Tests). Before any database work it rejects a malformed name, an empty script, a duplicate sequence, or a duplicate identity.
- **Rationale**:
  - The convention fixes the `NNNN_<area>_<description>` pattern and that later features take the next free number.
  - Normalizing line endings makes checksums identical across Windows (`core.autocrlf`) and Linux checkouts. Without it, the same migration could look tampered on another machine.
  - The `sha-256:` format reuses the digest representation in [contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md#representation-conventions).
- **Alternatives considered**:
  - DbUp's default script naming (full resource name) and journal: rejected because there is no checksum or sequence column.
  - Timestamp-based names: rejected because the architecture requires one ordered sequence and the convention fixes four digits.
  - Hashing raw bytes: rejected because of the line-ending drift.
  - Adding `.gitattributes eol=lf` for `.sql`: unnecessary once checksums are normalized. Spike A2(c) confirmed that a CRLF file and its LF copy hash identically after normalization and differently without it. (A `.gitattributes` rule is still needed for the `.sh` init script; see R6.)
  - DbUp's default script identity: rejected. DbUp names an embedded script by its manifest resource name, for example `Root.Namespace.Folder.0001_x.sql`, so the identity would change when a namespace or folder is renamed (spike A2(c)). The identity is therefore the file name.

## R3. DbUp usage and journal

- **Decision**: `dbup-postgresql` 7.0.1 (which depends on `dbup-core` 6.1.1 and `Npgsql >= 10.0.1`). The Migrator computes the pending set itself (R4, R5) and hands DbUp only those scripts:
  - **Script provider**: `PendingMigrationScriptProvider`, a DbUp `IScriptProvider` that yields `SqlScript(identity, content)` in sequence order. The identity is the file name without `.sql`, not DbUp's manifest resource name.
  - **Journal**: `SocAlyticsHistoryJournal`, a subclass of `DbUp.Support.TableJournal`. Its constructor is `(connectionManager, log, new PostgresqlObjectParser(), "socalytics_migrations", "history")`, and it is wired with `.JournalTo((connectionManager, log) => new SocAlyticsHistoryJournal(connectionManager, log))`. It overrides:
    - `CreateSchemaTableSql`: creates schema `socalytics_migrations` and table `history`, then grants read access to `socalytics_app`;
    - `DoesTableExistSql` and `GetJournalEntriesSql` (returns identities);
    - `GetInsertScriptCommand(Func<IDbCommand>, SqlScript)`: a parameterized insert of `sequence`, `identity`, and `checksum`. `sequence` is parsed from the script number and `checksum` is computed from `script.Contents`.

    The SQL text of these overrides comes from Infrastructure constants (`MigrationHistorySql`), so the Migrator holds no SQL.
  - **History sequence**: `history.sequence` is a plain `integer` primary key derived from the script number, not an identity column. Spike A2(b) showed that an identity column leaves gaps after failed scripts.
  - **Engine options**: `PostgresqlDatabase(connectionString)`, `WithScripts(provider)`, `JournalTo(…)`, `WithTransactionPerScript()`, `WithVariablesDisabled()`, `WithExecutionTimeout(ScriptTimeout)`, `LogToNowhere()`. The `ScriptExecuted` event drives sanitized progress logs, and `DatabaseUpgradeResult.ErrorScript` names the failing identity.
  - **Why variables are disabled**: Without `WithVariablesDisabled()`, named dollar quotes such as `$fn$` fail with "Variable fn has no value defined". Plain `$$` works either way (spike A2(a)).
  - **Spike evidence (A2(b))**:
    - The command from DbUp's factory carries the script's transaction, and the script and journal row share one `pg_current_xact_id()`.
    - A failing journal insert rolls back the script's DDL, and a failing script leaves no table, row, or history record.
    - `CreateSchemaTableSql` runs inside the first script's transaction, so a failing first script also rolls back the history table creation.
  - **Integration tests** re-prove these properties in this repository (`MigrationRollbackTests`, and `0002` with dollar-quoted bodies).
- **Rationale**: The architecture adopts DbUp. Restricting it to execution keeps every safety check (checksums, sequence rules, unknown history, locking) in code that this feature owns and tests. That code is also shared with readiness. A `TableJournal` subclass is the supported extension point and keeps the journal insert inside the script transaction.
- **Alternatives considered**:
  - Letting DbUp decide what is pending: rejected because it would have no checksum awareness and could diverge from readiness.
  - Hand-written execution without DbUp: rejected because it contradicts the adopted architecture.
  - `TransactionPerScript` versus a single transaction: one transaction for the whole run would undo earlier successful migrations, contradicting FR-007 ("earlier migrations remain applied").

## R4. Concurrency of Migrator runs

- **Decision**:
  - **Locking**: A dedicated lock connection takes the session-level advisory lock `pg_try_advisory_lock(5459779, 1)` (class key `0x534F43`, "SOC"; object key `1`, migrations). The Migrator polls every 500 ms until it acquires the lock or `Migrator:LockWaitTimeout` (default 2 minutes) expires. It logs "waiting for migration lock" once.
  - **Timeout**: Expiry exits with code 8 without touching anything.
  - **After acquiring**: The Migrator re-reads the history and recomputes the pending set, so a waiting run applies only what is still pending (usually nothing). A missing history table counts as an empty history. The journal creates the table in the first script's transaction (R3).
  - **Release**: The lock is held for the whole run and released by `pg_advisory_unlock` or implicitly when the session closes, including on crash.
- **Rationale**: The architecture names a session-level advisory lock held for the whole run. Advisory locks are scoped per database, which matches "the same database". Polling `pg_try_advisory_lock` avoids depending on how `lock_timeout` interacts with advisory locks and lets the wait be logged and cancelled. The history PK and UNIQUE constraints remain a second line of defense against double recording (FR-003).
- **Alternatives considered**:
  - `pg_advisory_xact_lock` per script: rejected because it would interleave two runs between scripts.
  - `LOCK TABLE history`: rejected because it needs the table to exist first and blocks readiness reads.
  - Blocking `pg_advisory_lock` with `lock_timeout`: rejected because it gives less control over diagnostics and cancellation.

## R5. Roles, ownership, and privileges

- **Decision**:
  - **Provisioning**: Two roles, `socalytics_migrator` and `socalytics_app`, are provisioned by the environment, not by migrations. Locally that is the AppHost init script (R6) and, in tests, the same script through Testcontainers. Production provisioning is deferred.
  - **Effective role**: The Migrator adds `Options=-c role=socalytics_migrator` to its connections. When the login identity is only a member of the role (a possible production mapping), all objects are still owned by `socalytics_migrator`, and the trigger suppression check sees that role.
  - **Database-level privileges**: The database `socalytics` is owned by `socalytics_migrator`. `REVOKE ALL ON DATABASE socalytics FROM PUBLIC` and `GRANT CONNECT ON DATABASE socalytics TO socalytics_app` remove `TEMP` and `CREATE` from runtime access.
  - **Schema privileges**: Migration 0001 creates `socalytics`, revokes it from `PUBLIC`, grants `USAGE` to `socalytics_app`, and sets default privileges on tables (`SELECT, INSERT, UPDATE, DELETE`) and on sequences (`USAGE, SELECT, UPDATE`) for objects later created by `socalytics_migrator`. The journal's table creation (R3) grants `USAGE` on `socalytics_migrations` and `SELECT` on `history` to `socalytics_app`.
  - **Fail fast**: Migration 0001 starts with a check that raises if `socalytics_app` is missing.
- **Rationale**: FR-012 and SC-006 require runtime access that cannot create or alter structures. Ownership by `socalytics_migrator` plus default privileges means later features' tables are reachable by the API without per-table grants. Creating login roles inside migrations would need `CREATEROLE` and embedded credentials, which the spec defers to production.
- **Alternatives considered**:
  - Migrations create `NOLOGIN` group roles: rejected because the Migrator would need `CREATEROLE` and role management would mix with schema history.
  - The superuser as migration identity locally: rejected because local runs would not exercise the separation the spec requires.
  - Per-area schemas or roles: rejected by FR-011 and the architecture.

## R6. Local role provisioning and AppHost wiring

All Aspire members below were confirmed against Aspire 13.4.6 in spike A1. The
spike build had 0 warnings and no experimental-API diagnostics.

- **Decision**: In `SocAlytics.Platform.AppHost`:
  - **User secrets**: Add `<UserSecretsId>` (a fixed GUID) to `SocAlytics.Platform.AppHost.csproj`. Without it, generated parameters get a new value on every run and Aspire emits no warning (spike A1(b)).
  - **Parameters**: `builder.AddParameter("socalytics-migrator-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true)`, and the same for `socalytics-app-password`. With `<UserSecretsId>`, the value stayed identical across separate AppHost runs (spike A1(b)).
  - **PostgreSQL**: `var postgres = builder.AddPostgres("postgres")`, then:
    - `.WithImageTag("18")`;
    - `.WithEnvironment("POSTGRES_DB", "socalytics")`;
    - `.WithEnvironment("SOCALYTICS_MIGRATOR_PASSWORD", migratorPassword)` and `.WithEnvironment("SOCALYTICS_APP_PASSWORD", appPassword)`;
    - `.WithInitFiles(Path.Combine(builder.AppHostDirectory, "PostgresInit"))`;
    - `.WithDataVolume("socalytics-postgres-data")`, unless configuration `SocAlytics:LocalDatabase:Persistent` is `false`. Host tests set it to `false`.
  - **Init script**: `PostgresInit/01-socalytics-roles.sh` (LF line endings). It calls `psql -v ON_ERROR_STOP=1 -v migrator_pw="$SOCALYTICS_MIGRATOR_PASSWORD" -v app_pw="$SOCALYTICS_APP_PASSWORD" --dbname "$POSTGRES_DB"` and does the following:
    - creates `socalytics_migrator` and `socalytics_app` with `LOGIN PASSWORD :'migrator_pw'` and `:'app_pw'`;
    - runs `ALTER DATABASE socalytics OWNER TO socalytics_migrator`;
    - applies `REVOKE ALL ON DATABASE socalytics FROM PUBLIC` and `GRANT CONNECT ON DATABASE socalytics TO socalytics_app`.

    A plain `.sql` init file cannot read secrets, so a `.sh` file reading environment variables is required (spike A1(a)). The repository gains a `.gitattributes` with `*.sh text eol=lf`, so Windows checkouts keep LF endings. That entry follows from the stack, as the constitution's `.gitattributes` rule requires.
  - **Connection strings**: `builder.AddConnectionString(name, ReferenceExpression.Create($"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Username=<role>;Password={passwordParameter};Database=socalytics"))`, where `endpoint` is `postgres.Resource.PrimaryEndpoint`. It is created once for `socalytics-migrator` (role `socalytics_migrator`) and once for `socalytics` (role `socalytics_app`). Spike A1(c) observed `current_user` equal to the custom role for both.
  - **Resources**: `migrator` is `AddProject<Projects.SocAlytics_Platform_Migrator>("migrator").WithReference(migratorConnection).WaitFor(postgres)`. `api` adds `.WithReference(appConnection).WaitFor(postgres).WaitForCompletion(migrator)` to its existing endpoint and health configuration.
  - **Health waits**: `WaitFor` targets the PostgreSQL resource, because a connection-string resource has no health of its own. Spike A1(d) showed the API starting only after the Migrator exited `0`, on every run.
- **Rationale**:
  - **Single init path**: The image runs `/docker-entrypoint-initdb.d` files once, on an empty data directory, before it opens TCP. During the entrypoint's init restart the health check can briefly report Unhealthy, and `WaitFor` absorbs this (spike A1(a)). The roles therefore exist before the Migrator connects.
  - **Repeat starts**: The data volume plus persisted passwords make US2 scenario 4 (repeat start applies nothing) true across separate starts. Init does not run again on a populated volume. If the persisted passwords are lost while the volume remains, the documented manual volume reset recovers ([quickstart.md](quickstart.md#8-reset-the-local-development-database-manual-development-data-only)).
  - **Test isolation**: Turning persistence off in tests keeps host tests on disposable instances (FR-027).
  - **Version pinning**: `postgres:18` is pinned by tag in both the AppHost and Testcontainers. The spikes ran 18.6. This keeps "instances of the same kind" (FR-026) and avoids volume-layout surprises when the Aspire default changes.
  - **Shared script**: Tests reuse the same init script, which proves it.
- **Alternatives considered**:
  - A `.sql` init file with `psql \getenv`: rejected. The spike confirmed only the `.sh` plus environment-variable pattern, and a static `.sql` file cannot carry secrets.
  - `AddDatabase("socalytics")`: rejected because its connection string uses the superuser and the database is already created by `POSTGRES_DB`.
  - Injecting connection strings with `WithEnvironment("ConnectionStrings__…", ReferenceExpression)`: it works (spike A1(c)), but was rejected because `AddConnectionString` shows the connection as a dashboard resource and composes with `WithReference`.
  - Fixed literal development passwords: rejected because they are secrets in source and trip secret scanning.
  - Provisioning roles from the Migrator with superuser access: rejected because it gives the Migrator superuser power.
  - No data volume: rejected because every start would be a fresh database, which cannot show "already migrated" behavior.

## R7. API readiness

- **Decision**: `AddInfrastructure()` registers `DatabaseReadinessHealthCheck` as health check `database` with `failureStatus: Unhealthy`, no tags (so it is excluded from `/alive`), and a 5-second timeout.
  - **What the check does**: It opens a connection from the runtime `NpgsqlDataSource` and reads `sequence, identity, checksum` from `socalytics_migrations.history`. It evaluates the result with `MigrationStateEvaluator` against the platform catalog, which is loaded once per process. Connect and query run under an internal 3-second budget linked to the framework token; any cancellation, timeout, or non-SQLSTATE-specific Npgsql failure maps to `database-unavailable`. Otherwise a paused or hung database would make the framework report "A timeout occurred while running check." instead of a category, and Npgsql's cancellation round trip could exceed the 5-second registration timeout.
  - **Results**:
    - Healthy when the state is current.
    - Unhealthy with a category description for: connection string missing (`configuration`), connection or timeout failure (`database-unavailable`), history missing (`42P01`) or pending migrations (`migration-state-not-current`), checksum mismatch or sequence conflict (`migration-state-conflict`), and permission denied (`database-access-denied`).
  - **Unknown applied migrations**: They keep the state healthy and are logged as a warning once per distinct set per host. The last reported set lives in an internal singleton, because the health check instance is created per run.
  - **Response bodies**: `/health` keeps the default `Healthy` or `Unhealthy` text with 200 or 503.
  - **Caching**: None. Every probe re-evaluates, so readiness recovers without a restart.
- **Rationale**:
  - FR-021 defines "current" exactly as the evaluator does.
  - The default health check response format and `/alive` stay unchanged (FR-025).
  - Sharing the evaluator with the Migrator rules out disagreement between "the Migrator thinks it is done" and "the API thinks it is current".
- **Alternatives considered**:
  - Comparing only the highest applied sequence: rejected because it misses checksum drift and gaps.
  - Caching a successful result: rejected because readiness must react to an unreachable database.
  - A JSON health response writer: rejected because it changes the existing surface.

## R8. Infrastructure composition and configuration

- **Decision**: Keep `public static IServiceCollection AddInfrastructure(this IServiceCollection services)` unchanged.
  - **Registrations**: Persistence services read `IConfiguration` from the container when first resolved: `NpgsqlDataSource` (singleton) from `ConnectionStrings:socalytics`, the scoped `DbSession` exposed as `IUnitOfWork` and the internal `IDbSession`, the `database` health check, and the platform `MigrationCatalog` (singleton). `InfrastructureLayerMarker` is removed because real registrations now anchor the layer.
  - **Visibility**: Infrastructure keeps internal implementation types. `InternalsVisibleTo` names exactly `SocAlytics.Platform.Migrator` and `SocAlytics.Platform.Integration.Tests`.
  - **Dapper naming**: Dapper's `DefaultTypeMap.MatchNamesWithUnderscores` is enabled once in Infrastructure, which sets the snake_case column convention.
- **Rationale**: The existing architecture tests require exactly one public type in Infrastructure, with one method whose only parameter is `IServiceCollection`. Resolving configuration lazily also lets the API start without a database. Readiness then reports `configuration` or `database-unavailable` instead of the process crashing, which matches the readiness-only dependency.
- **Alternatives considered**:
  - `AddInfrastructure(IConfiguration)`: rejected because it breaks the existing boundary test for no benefit.
  - A second public method for the Migrator: rejected because the convention is one public DI method.
  - Making migration types public: rejected because it widens the API-visible surface.

## R9. Unit of work

- **Decision**: In `SocAlytics.Platform.Application.Abstractions.Persistence`:
  - `IUnitOfWork.BeginAsync(CancellationToken)` returns an `IUnitOfWorkScope`, which is `IAsyncDisposable` with `CommitAsync(CancellationToken)` and `RollbackAsync(CancellationToken)`.
  - **Implementation**: The scoped Infrastructure `DbSession` opens one `NpgsqlConnection` and one `NpgsqlTransaction` at Read Committed (the PostgreSQL default).
  - **Behavior**:
    - A second `BeginAsync` while a scope is active throws `InvalidOperationException`.
    - Disposing a scope that was not committed rolls back with `CancellationToken.None`, then returns the connection to the pool and resets the session, so the next `BeginAsync` works.
    - Writes through `IDbSession` require an active transaction and throw otherwise. Reads may run without one and reuse the active transaction when present.
- **Rationale**: This follows the conventions ("begin/commit/rollback scope per command … one `NpgsqlConnection` + `NpgsqlTransaction`") and FR-015 and FR-016. Rollback on dispose makes exceptions, cancellations, and conflict returns safe by construction. Enforcing a transaction for writes makes FR-015 checkable rather than a convention.
- **Alternatives considered**:
  - An ambient `TransactionScope`: rejected because of poor async and Npgsql enlistment semantics.
  - A delegate-based `ExecuteAsync(work)`: rejected because a conflict result must roll back without throwing, which is awkward to express in a delegate signature.
  - A MediatR pipeline behavior: rejected because MediatR is excluded.

## R10. Optimistic concurrency outcome

- **Decision**:
  - **Result type**: In `Application.Abstractions.Persistence`, `VersionedWriteResult` is a `readonly record struct` with `VersionedWriteOutcome Outcome` (`Applied`, `NotFound`, `ConcurrencyConflict`) and `long? Version`. `Version` is the new version for `Applied` and the current persisted version for `ConcurrencyConflict`.
  - **Helper**: The Infrastructure helper `VersionedWrites.ExecuteAsync(IDbSession, CommandDefinition guardedWrite, CommandDefinition currentVersionProbe)` runs a guarded `UPDATE … WHERE id = @Id AND version = @ExpectedVersion RETURNING version`. If no row is returned, it runs the probe (`SELECT version … WHERE id = @Id`) to tell `NotFound` from `ConcurrencyConflict`. Both run inside the caller's unit of work.
  - **Lifecycle transitions**: Transitions use the same helper with `AND state = @ExpectedState`. Callers map `ConcurrencyConflict` to `409` for transitions and to `412` for edits, following the API conventions.
- **Rationale**: Endpoints need to tell 404 from 412 and need the new version to emit the ETag. `RETURNING version` sees the value set by the `BEFORE UPDATE` trigger. A no-op guarded write returns the unchanged version, which avoids false increments.
- **Alternatives considered**:
  - Throwing a `ConcurrencyException`: rejected because conflicts are expected outcomes that must roll back, not exceptional control flow.
  - A boolean result: rejected because it cannot tell not-found from conflict.
  - `SELECT … FOR UPDATE` first: rejected because it needs an extra round trip and pessimistic locking that the architecture does not adopt.

## R11. Version triggers and suppression

- **Decision**: Migration `0002_foundation_version_triggers.sql` creates:
  - **`socalytics.advance_version()`** (`plpgsql`, `SECURITY INVOKER`). Used as a `BEFORE UPDATE FOR EACH ROW WHEN (OLD.* IS DISTINCT FROM NEW.*)` trigger. When suppressed it sets `NEW.version := OLD.version`; otherwise `NEW.version := OLD.version + 1`.
  - **`socalytics.touch_aggregate_root()`** (`plpgsql`, `SECURITY INVOKER`, arguments `root_table`, `root_key`, `child_key`). Used by `AFTER INSERT OR DELETE` and `AFTER UPDATE … WHEN (OLD.* IS DISTINCT FROM NEW.*)` row triggers on child tables. It runs `EXECUTE format('UPDATE socalytics.%I SET version = version + 1 WHERE %I = ($1).%I', root_table, root_key, child_key) USING <row>` for the old row, the new row, or both, so the key keeps its column type. Both are touched when an update moves the child to a different root. When suppressed it does nothing.
  - **Suppression check**: Both functions treat suppression as `current_setting('socalytics.suppress_version', true) = 'on' AND pg_has_role(current_user, 'socalytics_migrator', 'MEMBER')`.
  - **Helper procedures**: Later migrations call `socalytics.attach_version_trigger(target regclass)` and `socalytics.attach_aggregate_child_triggers(child regclass, root regclass, child_key name, root_key name DEFAULT 'id')`. They create the triggers with deterministic names (`<table>_version_advance`, `<table>_root_touch`, `<table>_root_touch_update`). Every table name they put into trigger names or trigger arguments is the bare `pg_class.relname` of the `regclass` argument, because `regclass::text` is schema-qualified whenever `socalytics` is not on `search_path`, and `format('%I')` would then quote the whole `socalytics.<table>` as one identifier. `EXECUTE` on both procedures is revoked from `PUBLIC`.
- **Rationale**:
  - This implements the architecture's trigger design with the names fixed by the conventions.
  - **Idempotent increment**: Setting `NEW.version` from `OLD.version` makes the increment idempotent. A touch that writes `version + 1`, a handler that also writes `version + 1`, or a handler that writes an arbitrary version all end at exactly `OLD + 1` (FR-030). A no-op update never fires the trigger.
  - **Runtime cannot suppress**: A placeholder setting can be set by any role, so the role-membership check is what makes FR-031 hold for runtime access.
  - **Consistent attachment**: The helpers keep later features' migrations short and the trigger names checkable.
- **Alternatives considered**:
  - Incrementing in handlers: rejected by the architecture because it can be forgotten.
  - Statement-level triggers: rejected because they cannot advance once per individual change.
  - A per-root generated touch function: rejected because it duplicates code per aggregate.
  - Checking the session setting alone: rejected because runtime access could suppress versions.
  - `SECURITY DEFINER` functions: rejected because the invoker already has the needed DML, and definer rights would widen privileges.

## R12. Structural checks and table classification

- **Decision**: `Structure/PersistenceStructureTests` applies the full platform catalog to a fresh database and inspects `pg_catalog` and `information_schema`. These reusable tests assert only the rules below, never a table count or list, so they stay green when later features add tables. A separate foundation-only test proves FR-014: it applies the catalog filtered to the `foundation` migrations and asserts that `socalytics` contains no tables. The checks are:
  - **Versioned tables**: Every table in `socalytics` with a `version` column has type `bigint`, `NOT NULL`, default `1`, and a `BEFORE UPDATE` row trigger executing `socalytics.advance_version()`.
  - **Classification manifest**: Every table in `socalytics` without `version` appears in the manifest `Structure/PersistedTableClassifications.cs` as `Immutable`, `Unversioned` (with a reason, for example a later outbox), or `ChildOf(root, childKey, rootKey)`. Every `ChildOf` table has the two `touch_aggregate_root` triggers with matching arguments and no `version` column (FR-032). Every manifest entry exists in the database.
  - **No discriminator**: No column or table in `socalytics` or `socalytics_migrations` is named `club_id`, `clubid`, or `tenant_id`.
  - **Checker self-test**: A negative test applies the test-only `StructureViolations` migration and asserts that the checker reports each violation.
  - **Later features**: They run this test unchanged after adding migrations and add their tables to the manifest.
- **Rationale**: Child relationships cannot be inferred safely from foreign keys, because references across aggregates also use foreign keys. An explicit manifest forces each later feature to classify every new table where reviewers see it. Database-side metadata such as table comments is easy to overwrite silently. The negative test proves the checker is not vacuous while no domain tables exist.
- **Alternatives considered**:
  - `COMMENT ON TABLE` tags: rejected because they are fragile.
  - Foreign-key inference: rejected because it produces false positives.
  - Relying on code review: rejected because FR-028 requires automated checks.

## R13. Integration test infrastructure

- **Decision**:
  - **Project**: `src/platform/Tests/SocAlytics.Platform.Integration.Tests` uses xUnit v3, Shouldly, Testcontainers.PostgreSql 4.15.0, and Microsoft.AspNetCore.Mvc.Testing 10.0.12. It references Api, Migrator, Infrastructure, and Application.
  - **Shared container**: An assembly fixture starts one `postgres:18` container with `POSTGRES_DB=socalytics`. It maps the AppHost's `PostgresInit/01-socalytics-roles.sh` (linked into the test output) to `/docker-entrypoint-initdb.d/` and passes generated passwords through the same `SOCALYTICS_*_PASSWORD` environment variables. Spike A3 ran Testcontainers `postgres:18` with DbUp, Dapper, and `WebApplicationFactory<Program>` successfully.
  - **Per-test databases**: Each test creates its own database `t_<guid>` through the container superuser, with the same owner, revokes, and grants as the init script, and receives migrator and app connection strings.
  - **Dedicated containers**: Tests that pause the database (readiness recovery) use a dedicated container in a non-parallel collection.
  - **Test-only migrations**: Embedded resources under `TestMigrations/<Scenario>/` with sequences `9001+`. Catalogs combine the platform catalog with one scenario folder.
  - **API readiness tests**: `WebApplicationFactory<Program>` overrides `ConnectionStrings:socalytics` with in-memory configuration, so developer environment variables are ignored.
  - **No fallback**: If Docker is unavailable, the fixture fails. It never falls back to another database.
  - **Cleanup**: Testcontainers' resource reaper removes containers even after crashes.
- **Rationale**:
  - FR-026 and FR-027 require disposable instances of the same kind, cleanup, and no developer databases.
  - Per-test databases in one container keep the suite fast and parallel while fully isolated, and advisory locks are per database.
  - Reusing the init script verifies it.
- **Alternatives considered**:
  - One container per test: rejected because it is slow.
  - Transaction-rollback isolation: rejected because migrations and DDL tests need real commits and separate sessions.
  - Docker Compose in tests: rejected as unnecessary.
  - Running the Migrator as a child process for every test: rejected because the in-process `MigratorEntryPoint.RunAsync` returns the same exit code and accepts test catalogs. The AppHost test covers the real process path.

## R14. Package versions and central management

- **Decision**: Add to `src/platform/Directory.Packages.props` the versions that spike A3 resolved together under central package management and warnings-as-errors. Restore and build had 0 warnings and no NU1605 or NU1608:
  - `Npgsql` 10.0.3;
  - `Dapper` 2.1.89 (latest stable, no dependencies);
  - `dbup-postgresql` 7.0.1 (brings `dbup-core` 6.1.1; requires `Npgsql >= 10.0.1`);
  - `Aspire.Hosting.PostgreSQL` 13.4.6;
  - `Testcontainers.PostgreSql` 4.15.0;
  - `Microsoft.AspNetCore.Mvc.Testing` 10.0.12;
  - `Microsoft.Extensions.Diagnostics.HealthChecks`, `Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, and `Microsoft.Extensions.Hosting` at 10.0.12.

  Raise the existing `Microsoft.Extensions.DependencyInjection.Abstractions` pin from 10.0.0 to 10.0.12. The rule is a central floor of 10.0.12 for every directly referenced `Microsoft.Extensions.*` package. `Mvc.Testing` pulls 10.0.12 and `Aspire.Hosting.Testing` 13.4.6 pulls 10.0.8. A test project that directly referenced a `Microsoft.Extensions.*` package at 10.0.0 reproduced `NU1605` in spike A3, and pinning 10.0.12 removed it.
- **Rationale**: Central package management is already enabled, and warnings are errors, so a downgrade warning breaks the build. The 10.0.12 floor matches the installed runtime and `Mvc.Testing`.
- **Alternatives considered**:
  - Keeping 10.0.0 for production projects only: rejected because a single floor is simpler and was proven clean.
  - The `Aspire.Npgsql` client integration in the API: rejected because it would couple Infrastructure registration to Aspire hosting extensions. Its health check would also duplicate the migration-aware readiness.
  - The `Npgsql.DependencyInjection` package: rejected because the data source is built directly in Infrastructure.

## R15. Diagnostics and redaction

- **Decision**: Migrator and readiness diagnostics use source-generated `LoggerMessage` methods with fixed event IDs (see [contracts/migrator-cli.md](contracts/migrator-cli.md#diagnostics)).
  - **Logged**: The failure category, the migration identity when applicable, the PostgreSQL `SQLSTATE` code, and the exception type.
  - **Never logged**: Connection strings or their parts beyond the database role name, passwords, script text, `PostgresException.MessageText`, `Detail`, `Hint`, `Where`, `Statement`, `InternalQuery`, or the result of `ToString()` on a `PostgresException`.
  - **Silencing DbUp**: DbUp logging is turned off (`LogToNowhere`) so it cannot echo script content or server messages.
  - **Redaction test**: It runs a failing migration that contains a unique marker token and a known password. It asserts that captured logs, stdout, and stderr contain neither.
- **Rationale**: FR-023 forbids credentials and migration content. PostgreSQL syntax errors quote script fragments ("at or near …"), so the server message text itself counts as migration content. The identity and `SQLSTATE` together still tell a developer exactly which migration failed and why (for example `42601` syntax error, `23505` unique violation).
- **Alternatives considered**:
  - Logging `MessageText` at Debug level: rejected because it is still a log and still violates FR-023.
  - A regex-based scrubbing logger: rejected because it is fragile and can miss secrets.

## R16. Architecture test additions

- **Decision**:
  - **Allowed project references**: `PlatformArchitectureTests.AllowedProjectReferences` adds `SocAlytics.Platform.Migrator` with the allowed references `SocAlytics.Platform.Infrastructure` and `SocAlytics.Platform.ServiceDefaults`.
  - **New `PersistenceArchitectureTests`**:
    - Only the Migrator csproj has a `dbup-*` package reference. No project other than the AppHost and Integration.Tests references the Migrator project.
    - The Api, Application, Domain, and Infrastructure assemblies have no dependency on `DbUp` or on the Migrator assembly. The Api assembly also has none on `Npgsql` or `Dapper`.
    - Infrastructure's `InternalsVisibleToAttribute` set equals {Migrator, Integration.Tests}.
    - Embedded `.sql` resources exist only in the Infrastructure assembly, and the platform catalog validates.
    - No public type in Domain or Application declares a property or field named `ClubId`.
  - **Test project updates**: The architecture test project references the Migrator and links its csproj into `InspectedProjects`, following the existing pattern.
- **Rationale**: These tests make FR-012, FR-013, and FR-028, and the "API image contains no migration execution path" rule, executable. They reuse the established csproj and metadata inspection techniques.
- **Alternatives considered**:
  - Inspecting `deps.json`: rejected because Api's `deps.json` is not in the test output, and the reference rules above already imply it.
  - Banning SQL strings by text search: rejected because it is noisy.

## R17. Migrator exit codes and options

- **Decision**:
  - **Exit codes**: `0` success (including nothing pending), `1` unexpected error, `2` configuration invalid, `3` database unavailable, `4` migration catalog invalid, `5` checksum mismatch, `6` sequence conflict, `7` migration failed (rolled back), `8` lock wait timed out, `9` cancelled.
  - **Options**: `Migrator:ConnectTimeout` (default `00:01:00`; bounded connection retry with backoff), `Migrator:LockWaitTimeout` (default `00:02:00`), and `Migrator:ScriptTimeout` (default `00:05:00`, per script). All must be positive.
  - **Arguments**: No command-line verbs. Configuration comes from environment variables, `appsettings`, or `--key=value` arguments through the Generic Host.
- **Rationale**: FR-033 requires distinguishable outcomes. One code per spec failure category lets operators and Compose or Container Apps diagnose without parsing logs. Defaults are generous for local use and configurable for future deployments, whose values remain deferred.
- **Alternatives considered**:
  - A single non-zero code: rejected because outcomes would not be distinguishable.
  - CLI verbs (`verify`, `apply`): rejected as speculative, since no requirement asks for them.

## R18. Migrator packaging and deployment

- **Decision**: This feature adds no Dockerfile, container publishing settings, or Compose service. The Migrator is a separate deployable project, so a later deployment feature can publish it as its own OCI image, as the architecture requires. Production scheduling, identities, and credentials remain deferred.
- **Rationale**: The spec puts production hosting and deployment configuration out of scope. Adding image settings now would be speculative and unverified.
- **Alternatives considered**: Enabling SDK container publishing now was rejected, because deployment evidence would be claimed without a deployment feature.
