# Contract: Operational Endpoints and Database Readiness

Component: `SocAlytics.Platform.Api`. These endpoints are operational surfaces
and are not part of the `v1` OpenAPI document. The OpenAPI document keeps zero
paths after this feature.

## Endpoints

| Path | Checks evaluated | `200 OK` body | `503 Service Unavailable` body | Change in this feature |
| --- | --- | --- | --- | --- |
| `GET /alive` | Checks tagged `live` (`self`) | `Healthy` (`text/plain`) | `Unhealthy` | None. Never depends on the database. |
| `GET /health` | All registered checks (`self`, `database`) | `Healthy` | `Unhealthy` | Now includes the `database` check. |
| `GET /openapi/v1.json` | n/a | OpenAPI document with `info.version` `v1` and empty `paths` | n/a | None. |

The response format is the ASP.NET Core default health check writer, unchanged.
Aspire uses `/health` as the API resource's health probe.

## `database` Health Check

| Property | Value |
| --- | --- |
| Name | `database` |
| Tags | none (excluded from `/alive`) |
| Failure status | `Unhealthy` |
| Timeout | 5 seconds (registration); the check itself runs connect and query under an internal 3-second budget and maps cancellation or timeout to `database-unavailable`, so a hung database never yields the framework's generic timeout description |
| Connection | `ConnectionStrings:socalytics` (runtime role `socalytics_app`) |
| Query | `SELECT sequence, identity, checksum FROM socalytics_migrations.history` |
| Evaluation | `MigrationStateEvaluator` against the migration catalog embedded in `SocAlytics.Platform.Infrastructure` (see [data-model.md](../data-model.md#migration-state)) |
| Caching | None; every probe re-evaluates, so readiness recovers without a restart. |
| Side effects | None. The API never creates the history or applies migrations. |

### Outcomes

| Situation | Result | Description category (logged, not in the body) |
| --- | --- | --- |
| Every registered migration recorded with matching checksum | Healthy | — |
| As above, plus history rows unknown to the catalog | Healthy | `unknown-applied-migrations` (warning, once per distinct set per host) |
| Connection string missing or empty | Unhealthy | `configuration` |
| Connection refused, DNS failure, authentication failure, or timeout | Unhealthy | `database-unavailable` |
| History table missing (`42P01`) or at least one registered migration not recorded | Unhealthy | `migration-state-not-current` |
| Checksum mismatch or sequence conflict | Unhealthy | `migration-state-conflict` (identity included) |
| Permission denied (`42501`) | Unhealthy | `database-access-denied` |

Diagnostics never contain the connection string, the password, or migration
content (FR-023).

The description is the `HealthReportEntry.Description` of the `database`
entry. ASP.NET Core's health check service logs it; the `/health` body stays
the plain `Healthy` or `Unhealthy` text. Tests read it through
`HealthCheckService.CheckHealthAsync` with a predicate selecting `database`,
never by parsing the `/health` body.

## Local Composition Order (AppHost)

| Resource | Kind | Waits for | Reported state |
| --- | --- | --- | --- |
| `postgres` | PostgreSQL container `postgres:18` (pinned tag), `POSTGRES_DB=socalytics`, init script `PostgresInit/01-socalytics-roles.sh`, volume `socalytics-postgres-data` | — | Health (server connection check) |
| `migrator` | Project `SocAlytics.Platform.Migrator`, connection `socalytics-migrator` (`AddConnectionString` + `ReferenceExpression`) | `postgres` healthy (`WaitFor(postgres)`) | `Finished` with exit code (see [migrator-cli.md](migrator-cli.md#exit-codes)) |
| `api` | Project `SocAlytics.Platform.Api`, connection `socalytics` (`AddConnectionString` + `ReferenceExpression`) | `postgres` healthy and `migrator` finished with exit code 0 (`WaitFor(postgres)`, `WaitForCompletion(migrator)`) | Health from `/health` |

The connection-string resources have no health of their own, so every wait
targets the PostgreSQL resource or the Migrator. Spike A1 confirmed this
wiring on Aspire 13.4.6.

If the Migrator exits non-zero, the API resource is not started. If the API
runs against a database whose migration state is not current, `/health`
returns `503`.
