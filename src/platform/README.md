# Platform

## Purpose

This area owns the SocAlytics control-plane platform and contains its first
executable .NET 10 host scaffold.

## Ownership

The boundary includes the API/BFF and the ASP.NET Core application, a
well-structured monolith organized in Domain, Application, Infrastructure, and
API layers, that owns platform application behavior and coordinates platform
services.

## Exclusions

Client application source, agent runtimes and tooling, Analyst execution
software, and Analyst capabilities do not belong here.

## Current Status

The executable scope is an ASP.NET Core API that uses PostgreSQL through the
Infrastructure layer, composed by an Aspire AppHost. The AppHost starts
PostgreSQL, then the one-off `SocAlytics.Platform.Migrator`, then the API, in
that order. The API exposes liveness at `/alive`, readiness at `/health`, and
the `v1` OpenAPI document at `/openapi/v1.json`. `/alive` checks the process
only; `/health` includes the `database` check and requires a reachable database
with a current migration state. The solution is split into the layer projects
Domain, Application, Infrastructure, and Api, plus the Migrator; the API wires
the Application and Infrastructure layers through their public composition
methods, and functional areas become folders and namespaces inside the layers.
Host tests cover startup, endpoints, OpenAPI, and composition;
`SocAlytics.Platform.Integration.Tests` covers migrations, units of work,
version triggers, and readiness against disposable PostgreSQL containers;
architecture tests enforce the layer dependency direction and
implementation-type visibility.

The persistence foundation (schema, migrations, Migrator, units of work,
optimistic concurrency, database-aware readiness) is implemented as
development evidence. Further domain behavior, NATS messaging, S3-compatible
storage, the transactional outbox, client
applications, deployment images and configuration, production credentials, and
production deployment remain deferred.

The identity foundation is implemented as development evidence: local accounts,
a server-validated BFF session with anti-forgery protection, membership, club
and team roles, team-scoped authorization, security audit events, and the
`Club > Season > Team > Match` hierarchy under `/api/v1`. OIDC, MFA
enforcement, self-service recovery, clients, Analyst Manager identity,
production values for POL-001, POL-002, and POL-009, deployment, and
production readiness remain deferred.

The AppHost bootstraps the first club with these parameters:

- `first-club-admin-password`: generated and persisted in the AppHost user
  secrets. Read it with
  `dotnet user-secrets list --project src/platform/SocAlytics.Platform.AppHost`.
- `first-club-admin-account-name`: defaults to `club-admin`.
- `club-display-name`: defaults to `Development Club`.

Interactive sign-in uses the API HTTPS endpoint, which needs the ASP.NET Core
development certificate (`dotnet dev-certs https --trust`). The endpoint is
omitted when the AppHost runs with `--SocAlytics:ApiHttpsEndpoint=false`, as
the host smoke test does. The first club administrator must change the
password at first sign-in; afterwards remove
`ClubBootstrap__FirstClubAdmin__InitialPassword` from the configuration.
Without a valid bootstrap configuration, `/health` stays unhealthy
(`club-not-established` or `bootstrap-conflict`).

Operator break-glass recovery: set `BreakGlassRecovery__AccountName`,
`BreakGlassRecovery__RecoveryId`, and `BreakGlassRecovery__TemporaryCredential`
(from the secret store), start the API, confirm the
`break-glass-recovery.applied` audit event, then remove the directive.
`IdentityAccess` settings exist only in `appsettings.Development.json` and
test configuration.
 This scaffold does not claim production
readiness.

## Development

A running Docker-compatible container runtime is required to run the AppHost
and the Host and Integration tests. Run the supported workflow from the
repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

Run only the integration tests, or the Migrator on its own:

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build
${env:ConnectionStrings__socalytics-migrator} = '<connection string>'
dotnet run --project src/platform/SocAlytics.Platform.Migrator
```

Local database credentials are generated development-only values kept in the
AppHost user secrets. To reset local data, stop the AppHost and run
`docker volume rm socalytics-postgres-data`. This removes development data
only.

## Migrator Contract

The Migrator applies all pending platform migrations once and exits. It is safe
to run repeatedly and concurrently against the same database. It takes no verbs
or required arguments. Configuration follows the .NET Generic Host order:
`appsettings.json`, `appsettings.{Environment}.json`, environment variables,
then `--Key=value` arguments.

| Key | Required | Default | Rule |
| --- | --- | --- | --- |
| `ConnectionStrings:socalytics-migrator` (`ConnectionStrings__socalytics-migrator`) | yes | none | Npgsql connection string for a login that is, or is a member of, `socalytics_migrator`. |
| `Migrator:ConnectTimeout` | no | `00:01:00` | Positive `TimeSpan`; total time spent retrying the initial connection. |
| `Migrator:LockWaitTimeout` | no | `00:02:00` | Positive `TimeSpan`; bounded wait for another run's migration lock. |
| `Migrator:ScriptTimeout` | no | `00:05:00` | Positive `TimeSpan`; command timeout per migration script. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | none | When set, logs and traces are exported through ServiceDefaults. |

| Code | Name | Meaning |
| --- | --- | --- |
| `0` | `Success` | Database is current. |
| `1` | `UnexpectedError` | Unclassified failure. |
| `2` | `ConfigurationInvalid` | Missing connection string or invalid option. |
| `3` | `DatabaseUnavailable` | No connection within `ConnectTimeout`, or authentication failed. |
| `4` | `CatalogInvalid` | Malformed name, empty script, duplicate sequence or identity. |
| `5` | `ChecksumMismatch` | An applied migration's registered content changed. |
| `6` | `SequenceConflict` | A pending migration is numbered at or below the highest applied sequence. |
| `7` | `MigrationFailed` | A script failed and was rolled back; later scripts were not attempted. |
| `8` | `LockTimeout` | Another run held the lock beyond `LockWaitTimeout`. |
| `9` | `Cancelled` | Shutdown signal received; the in-flight script was rolled back. |

Scripts that completed earlier in the same run stay committed for codes `1`,
`7`, and `9`. Logs never contain connection strings, passwords, script text, or
PostgreSQL server message details.

## Migration Authoring

- Migrations are forward-only files `NNNN_<area>_<description>.sql` under
  `SocAlytics.Platform.Infrastructure/Persistence/Migrations/`, embedded as
  resources. `NNNN` is the next free four-digit number on an up-to-date `main`;
  a feature renumbers before merge if its number was taken. `<area>` matches
  `[a-z][a-z0-9]*`; `<description>` is lowercase words joined by `_`.
- The identity is the file name without `.sql` and never changes. The checksum
  is `sha-256:` plus the hex SHA-256 of the BOM-stripped, LF-normalized content.
  Never edit or delete an applied migration; add a new one.
- Each migration runs in its own transaction with its history record. Do not
  issue `COMMIT`, `ROLLBACK`, or statements that cannot run in a transaction.
- Use schema-qualified names (`socalytics.<table>`). Do not create schemas,
  roles, or credentials, or grant privileges to roles other than
  `socalytics_app`. Never add `club_id`, `tenant_id`, or another cross-club
  discriminator.
- A mutable aggregate root has `version bigint NOT NULL DEFAULT 1` and calls
  `CALL socalytics.attach_version_trigger('socalytics.<root>');`. A child table
  whose rows always change with its root has no `version` column and calls
  `CALL socalytics.attach_aggregate_child_triggers('socalytics.<child>', 'socalytics.<root>', '<child_key_column>');`
  with a fourth argument when the root key is not `id`. Triggers are named with
  the bare table name.
- Every table without a `version` column needs an entry (`ChildOf(…)`,
  `Immutable`, or `Unversioned(reason)`) in
  `Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`.
- Data migrations advance the version of each changed versioned row by one. A
  migration that must not do so starts with
  `SET LOCAL socalytics.suppress_version = 'on';`, which lasts for that
  transaction and is honored only for members of `socalytics_migrator`.
- Edit handlers run `UPDATE … WHERE id = @Id AND version = @ExpectedVersion RETURNING version`
  through `VersionedWrites` inside an `IUnitOfWork` scope; a conflict means
  `412` where `If-Match` is required. Lifecycle transitions use
  `WHERE id = @Id AND state = @ExpectedState RETURNING version`; a conflict
  means `409`. Never increment `version` manually as the only change mechanism
  or derive meaning from the size of a version step.

## Authoritative Architecture

- [Architecture overview](../../docs/architecture/overview.md)
- [Platform implementation](../../docs/architecture/platform-implementation.md)
- [Contracts and compatibility](../../docs/architecture/contracts-and-compatibility.md)
- [Job processing](../../docs/architecture/job-processing.md)
