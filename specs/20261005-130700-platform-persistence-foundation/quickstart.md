# Quickstart: Validate the Platform Persistence Foundation

Runnable scenarios that prove the feature end to end once it is implemented.
Run every command from the repository root in PowerShell. Expected outcomes
reference the [contracts](contracts/) and the [data model](data-model.md).
Passing these scenarios is development evidence only. It does not show
deployment support or production readiness.

## Prerequisites

- The .NET SDK selected by `src/platform/global.json` (`10.0.400`, latest patch).
- A running Docker-compatible container runtime (Docker Desktop or Docker Engine). The AppHost, Host.Tests, and Integration.Tests all start PostgreSQL containers (`postgres:18`).
- Network access to pull `postgres:18` and the Testcontainers resource reaper image on first use.

## 1. Restore, build, and run every test

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected:

- The solution includes `SocAlytics.Platform.Migrator` and `Tests/SocAlytics.Platform.Integration.Tests` and builds with zero warnings.
- Architecture, host, and integration tests all pass.
- Afterwards, `docker ps -a` shows no leftover test containers (SC-010).

## 2. Run only the persistence verification

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~.Migrations."
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~.Concurrency."
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~.Readiness."
```

Expected: all pass. Coverage by folder:

- `Migrations`: US1 scenarios 1–6, unknown applied migrations, exit codes, redaction.
- `Transactions`: US3 scenarios 1–2.
- `Concurrency`: US3 scenarios 3–8.
- `Access`: SC-006.
- `Structure`: FR-028 and SC-009.
- `Readiness`: US2 scenario 5 and the readiness edge cases.

If Docker is not running, the tests fail with a container-start error and
never fall back to another database (FR-027).

## 3. Start the local platform

```powershell
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

Expected in the Aspire dashboard (URL printed on start):

- `postgres` is Running and Healthy.
- `migrator` is Finished with exit code `0`. Its logs show event `1006` "Migration run finished; 2 applied, database current" on a fresh volume.
- `api` starts only after the Migrator finished, then shows Running and Healthy.

Then, using the API's HTTP endpoint shown in the dashboard:

```powershell
$api = '<api http endpoint from the dashboard>'
Invoke-WebRequest "$api/alive"  | Select-Object StatusCode, Content     # 200, Healthy
Invoke-WebRequest "$api/health" | Select-Object StatusCode, Content     # 200, Healthy
(Invoke-RestMethod "$api/openapi/v1.json").paths.PSObject.Properties.Count  # 0
```

No other command or manual step is required (SC-001). Stop with `Ctrl+C`.

## 4. Restart against the already-migrated database

Run scenario 3 again and stop it, three times in a row.

Expected each time: the `migrator` logs show "0 applied, database current"
and no "Applying migration" events. The API becomes healthy (US2 scenario 4,
SC-003).

## 5. Inspect the migration history

While the AppHost runs:

```powershell
$pg = docker ps --filter "ancestor=postgres:18" --format "{{.Names}}" | Select-Object -First 1
docker exec $pg psql -U postgres -d socalytics -c "SELECT sequence, identity, checksum, applied_at FROM socalytics_migrations.history ORDER BY sequence;"
docker exec $pg psql -U postgres -d socalytics -c "\dn+"
docker exec $pg psql -U postgres -d socalytics -c "\du socalytics_*"
```

Expected:

- History contains exactly `1 | 0001_foundation_application_schema` and `2 | 0002_foundation_version_triggers`, each with a `sha-256:` checksum.
- Schemas `socalytics` and `socalytics_migrations` are owned by `socalytics_migrator`.
- Roles `socalytics_migrator` and `socalytics_app` exist.
- No table exists in `socalytics` (no domain tables).

## 6. Run the Migrator standalone

With the AppHost running, copy the `socalytics-migrator` connection string
from the `migrator` resource's environment in the dashboard. It holds
development-only values.

```powershell
${env:ConnectionStrings__socalytics-migrator} = '<connection string from the dashboard>'
dotnet run --project src/platform/SocAlytics.Platform.Migrator
$LASTEXITCODE   # 0
```

Expected: exit code `0` and "0 applied, database current" (FR-033).

Unreachable database:

```powershell
${env:ConnectionStrings__socalytics-migrator} = 'Host=127.0.0.1;Port=1;Database=socalytics;Username=socalytics_migrator;Password=not-a-secret'
dotnet run --project src/platform/SocAlytics.Platform.Migrator -- --Migrator:ConnectTimeout=00:00:05
$LASTEXITCODE   # 3
```

Expected: exit code `3` (`DatabaseUnavailable`). The output names the
category `database-unavailable` and does not contain `not-a-secret`.

Missing configuration:

```powershell
Remove-Item Env:\ConnectionStrings__socalytics-migrator
dotnet run --project src/platform/SocAlytics.Platform.Migrator
$LASTEXITCODE   # 2
```

## 7. Readiness without a current migration state

`Readiness/DatabaseReadinessTests` (scenario 2) covers the full matrix
automatically: current, pending, checksum conflict, unknown applied,
unreachable, and recovery without restart. For a quick manual check, start
the API alone against an unreachable database:

```powershell
${env:ConnectionStrings__socalytics} = 'Host=127.0.0.1;Port=1;Database=socalytics;Username=socalytics_app;Password=not-a-secret'
dotnet run --project src/platform/SocAlytics.Platform.Api --urls http://127.0.0.1:5089
# in a second terminal
Invoke-WebRequest http://127.0.0.1:5089/alive  -SkipHttpErrorCheck | Select-Object StatusCode   # 200
Invoke-WebRequest http://127.0.0.1:5089/health -SkipHttpErrorCheck | Select-Object StatusCode   # 503
```

Expected: the API starts and never applies migrations. `/alive` returns
`200` and `/health` returns `503`. The API log names `database-unavailable`
and does not contain `not-a-secret` (US2 scenario 5, edge cases).

## 8. Reset the local development database (manual, development data only)

```powershell
# stop the AppHost first
docker volume rm socalytics-postgres-data
```

Expected: the next scenario 3 run initializes a fresh database, provisions
both roles again, and applies both migrations. Use this reset also if the
AppHost user secrets were deleted and the stored passwords no longer match.

## 9. Documentation check

```powershell
node .github/scripts/check-markdown.mjs
```

Expected: 0 issues. The updated `README.md`, `AGENTS.md`, and
`src/platform/README.md` list the Docker prerequisite and the commands above,
and claim no domain behavior, deferred infrastructure, or production
readiness (SC-013).
