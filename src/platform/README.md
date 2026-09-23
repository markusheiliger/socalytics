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

The executable scope is a dependency-free ASP.NET Core API composed by an
Aspire AppHost. The API exposes liveness at `/alive`, readiness at `/health`,
and the `v1` OpenAPI document at `/openapi/v1.json`. It registers the Club,
Identity Access, Recordings, Registry, Analysis, and Agent Orchestration
capabilities through six public composition boundaries. Focused host tests cover
startup, endpoints, OpenAPI, and composition; architecture tests enforce project
dependencies and implementation-type visibility.

Domain behavior, PostgreSQL persistence with Dapper and DbUp, NATS messaging,
S3-compatible storage, identity and authentication, client applications,
Docker support, and production deployment remain deferred. This scaffold does
not claim production readiness.

## Development

Run the supported workflow from the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

## Authoritative Architecture

- [Architecture overview](../../docs/architecture/overview.md)
- [Platform implementation](../../docs/architecture/platform-implementation.md)
- [Contracts and compatibility](../../docs/architecture/contracts-and-compatibility.md)
- [Job processing](../../docs/architecture/job-processing.md)
