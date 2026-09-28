# Platform

## Purpose

This area owns the SocAlytics control-plane platform, including its .NET 10
host and local PostgreSQL persistence foundation.

## Ownership

The boundary includes the API/BFF and the ASP.NET Core modular monolith that
owns platform application behavior and coordinates platform services.

## Exclusions

Client application source, agent runtimes and tooling, Analyst execution
software, and Analyst capabilities do not belong here.

## Current Status

The Aspire AppHost composes the ASP.NET Core API with a local PostgreSQL
database. API readiness at `/health` depends on successful registered
migrations; liveness is exposed at `/alive`, and the `v1` OpenAPI document is
available at `/openapi/v1.json`. The API registers the Club, Identity Access,
Recordings, Registry, Analysis, and Agent Orchestration capabilities through six
public composition boundaries. Focused host tests cover startup, PostgreSQL,
migrations, endpoints, OpenAPI, and composition; architecture tests enforce
project dependencies and implementation-type visibility. PostgreSQL
integration tests use disposable containers to exercise migration ordering and
checksums, rollback, explicit transactions, optimistic concurrency, module
schema ownership and isolation, and the absence of `club_id`.

Domain behavior and domain tables, NATS messaging, S3-compatible storage,
identity and authentication, client applications, and production database and
deployment configuration remain deferred. The local persistence foundation
does not establish production readiness.

## Development

The .NET 10 SDK and a Docker-compatible container runtime are required for the
Aspire PostgreSQL resource and disposable PostgreSQL integration tests. Run the
supported workflow from the repository root:

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
