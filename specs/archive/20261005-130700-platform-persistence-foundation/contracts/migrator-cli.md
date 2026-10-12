# Contract: Migrator Invocation, Configuration, and Exit Codes

Component: `SocAlytics.Platform.Migrator`, a one-off console host. One
invocation applies all pending platform migrations once and exits. It is safe
to run repeatedly and concurrently against the same database (FR-010, FR-033).
How production deployments schedule it is deferred.

## Invocation

```text
dotnet run --project src/platform/SocAlytics.Platform.Migrator
dotnet SocAlytics.Platform.Migrator.dll            # published output
```

There are no verbs and no required arguments. Configuration follows the .NET
Generic Host order: `appsettings.json`, `appsettings.{Environment}.json`,
environment variables, then `--Key=value` command-line arguments.

## Configuration

| Key | Environment variable form | Required | Default | Rule |
| --- | --- | --- | --- | --- |
| `ConnectionStrings:socalytics-migrator` | `ConnectionStrings__socalytics-migrator` | yes | — | Npgsql connection string for a login that is, or is a member of, `socalytics_migrator`. The Migrator adds `role=socalytics_migrator` to the session options. |
| `Migrator:ConnectTimeout` | `Migrator__ConnectTimeout` | no | `00:01:00` | Positive `TimeSpan`; total time spent retrying the initial connection. |
| `Migrator:LockWaitTimeout` | `Migrator__LockWaitTimeout` | no | `00:02:00` | Positive `TimeSpan`; bounded wait for another run's migration lock. |
| `Migrator:ScriptTimeout` | `Migrator__ScriptTimeout` | no | `00:05:00` | Positive `TimeSpan`; command timeout per migration script. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | same | no | — | When set (Aspire sets it), logs and traces are exported through ServiceDefaults. |

## Behavior

1. Validate the configuration, then load and validate the embedded catalog.
2. Connect, retrying until `ConnectTimeout`.
3. Acquire the session advisory lock `pg_try_advisory_lock(5459779, 1)`, polling every 500 ms until `LockWaitTimeout`.
4. Read the history and evaluate it (checksum mismatch, then sequence conflict, then pending). A missing history table counts as an empty history. Report unknown applied migrations without blocking.
5. Apply each pending migration in ascending sequence with DbUp, using `WithTransactionPerScript`, `WithVariablesDisabled`, and the `TableJournal` subclass `SocAlyticsHistoryJournal`. The script and its history row (sequence from the script number, identity from the file name, normalized SHA-256 checksum) commit in one transaction. The journal creates `socalytics_migrations.history` in the first script's transaction when it is missing. Stop at the first failure.
6. Release the lock and exit.

## Exit Codes

| Code | Name | Meaning | Anything applied in this run? |
| --- | --- | --- | --- |
| `0` | `Success` | Database is current. Zero or more migrations applied. | Possibly |
| `1` | `UnexpectedError` | Unclassified failure. | Earlier scripts in this run stay committed. |
| `2` | `ConfigurationInvalid` | Missing connection string or invalid option. | No |
| `3` | `DatabaseUnavailable` | No connection within `ConnectTimeout`, or authentication failed. | No |
| `4` | `CatalogInvalid` | Malformed name, empty script, duplicate sequence or identity. | No |
| `5` | `ChecksumMismatch` | An applied migration's registered content changed. | No |
| `6` | `SequenceConflict` | A pending migration is numbered at or below the highest applied sequence. | No |
| `7` | `MigrationFailed` | A script failed; its effects and history row were rolled back; later scripts were not attempted. | Earlier scripts in this run stay committed. |
| `8` | `LockTimeout` | Another run held the lock beyond `LockWaitTimeout`. | No |
| `9` | `Cancelled` | Shutdown signal received (Ctrl+C, SIGTERM, or any other host shutdown through `IHostApplicationLifetime.ApplicationStopping`); the in-flight script was rolled back. | Earlier scripts in this run stay committed. |

## Diagnostics

The Migrator emits structured logs under category `SocAlytics.Platform.Migrator` with fixed event IDs.

| Event ID | Level | Message template |
| --- | --- | --- |
| 1000 | Information | `Migration run started; {RegisteredCount} migrations registered` |
| 1001 | Information | `Waiting for the migration lock held by another run` |
| 1002 | Information | `Migration lock acquired` |
| 1003 | Warning | `Applied migrations unknown to this release were left untouched: {Identities}` |
| 1004 | Information | `Applying migration {Identity}` |
| 1005 | Information | `Applied migration {Identity}` |
| 1006 | Information | `Migration run finished; {AppliedCount} applied, database current` |
| 1100 | Error | `Migration run failed: {Category}` (with `{Identity}` and `{SqlState}` when known) |

Rules:

- **Categories**: `configuration`, `database-unavailable`, `catalog-invalid`, `checksum-mismatch`, `sequence-conflict`, `migration-failed`, `lock-timeout`, `cancelled`, `unexpected`.
- **Never emitted**: Connection strings, hosts with credentials, passwords, script text, or PostgreSQL server message text, `Detail`, `Hint`, `Where`, `Statement`, or `InternalQuery`. DbUp's own logging is disabled.
