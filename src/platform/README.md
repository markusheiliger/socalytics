# Platform

## Purpose

This area owns the SocAlytics control-plane platform and contains its first
executable .NET 10 host scaffold.

## Ownership

The boundary includes the API/BFF and the ASP.NET Core modular monolith that
owns platform application behavior and coordinates platform services.

## Exclusions

Client application source, agent runtimes and tooling, Analyst execution
software, and Analyst capabilities do not belong here.

## Current Status

The executable scope is an ASP.NET Core API composed by an Aspire AppHost with
one PostgreSQL server and `platform` database. The API exposes liveness at
`/alive`, readiness at `/health`, and the `v1` OpenAPI document at
`/openapi/v1.json`. It registers the Club, Identity Access, Recordings,
Registry, Analysis, and Agent Orchestration capabilities through six public
composition boundaries and the module-neutral `Persistence` project (Npgsql,
Dapper, and DbUp). Startup applies ordered module migrations, and `/health`
reports ready only after they succeed. Role-based schema isolation gives each
module an owner role for migrations and a runtime role for normal access.

Host tests cover startup, PostgreSQL health, migrated and repeat startup,
endpoints, OpenAPI, and composition. Persistence tests use Testcontainers
PostgreSQL to cover migrations, role isolation, transactions, and optimistic
concurrency. Architecture tests enforce project dependencies and
implementation-type visibility.

Domain behavior and routes, NATS messaging, S3-compatible storage, identity and
authentication, client applications, Docker image support, production database
credentials, backup and recovery, and production deployment remain deferred.
This host does not claim production readiness.

## Development

Run the supported workflow from the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

The host and persistence tests and the AppHost need a supported container
runtime, such as Docker, to start PostgreSQL. Restore, build, and the
architecture tests do not. Stop the AppHost with `Ctrl+C`.

## Authoritative Architecture

- [Architecture overview](../../docs/architecture/overview.md)
- [Platform implementation](../../docs/architecture/platform-implementation.md)
- [Contracts and compatibility](../../docs/architecture/contracts-and-compatibility.md)
- [Job processing](../../docs/architecture/job-processing.md)
