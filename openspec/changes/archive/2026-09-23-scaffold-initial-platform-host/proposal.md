# Initial Platform Host Proposal

## Why

SocAlytics has an adopted .NET platform baseline but no executable evidence that its host, local composition, or modular-monolith boundaries can build and run. This change establishes the smallest executable platform foundation so later domain and infrastructure increments begin from verified structure rather than another documentation-only scaffold.

## What Changes

- Add a .NET 10 solution under `src/platform` with an ASP.NET Core API host, Aspire AppHost, shared service defaults, six planned module assemblies, and focused test projects.
- Make the API host start through Aspire, expose standard health endpoints, publish a versioned OpenAPI document, and register each planned module through its public composition boundary.
- Add architecture tests that enforce module isolation and prevent the API host from depending on module-internal implementation types.
- Add one minimal host smoke test and document root restore, build, test, and local-run commands.
- Update repository and platform documentation so it truthfully distinguishes this executable host scaffold from deferred domain, persistence, messaging, storage, identity, client, and production-deployment behavior.
- Keep PostgreSQL, Dapper, DbUp, NATS JetStream, S3-compatible storage, Docker Compose, authentication, domain endpoints, and generated clients out of this increment.

## Capabilities

### New Capabilities

- `platform-host`: Defines the executable .NET platform host, Aspire local composition, module registration boundaries, baseline health and OpenAPI behavior, structural verification, and supported developer commands.

### Modified Capabilities

None.

## Impact

- Adds the first executable projects, dependency manifests, and tests under `src/platform`.
- Introduces .NET 10 SDK, ASP.NET Core, .NET Aspire, OpenAPI, xUnit v3, Shouldly, and an architecture-testing dependency selected in the design.
- Changes root and platform development guidance from "no executable commands" to the exact supported restore, build, test, and local-run workflow.
- Does not add a product API contract beyond operational health and generated OpenAPI discovery, and does not claim production readiness or implementation evidence for deferred architecture areas.
- Leaves production topology, security controls, persistence, messaging, storage, domain behavior, and deployment artifacts unresolved for future approved changes.
