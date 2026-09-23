# Initial Platform Host Design

## Context

See [proposal.md](proposal.md) for motivation and [specs/platform-host/spec.md](specs/platform-host/spec.md) for required behavior. The repository currently has no project files or executable commands. The adopted architecture fixes .NET 10, ASP.NET Core, Aspire local composition, a modular-monolith host, six PostgreSQL ownership modules, standard health and OpenTelemetry defaults, and versioned OpenAPI. It does not yet justify infrastructure-backed behavior, domain operations, production orchestration, or security implementation.

This change affects platform architecture realization, implementation, tests, and documentation. It does not add client UX, change module responsibilities, implement security or data-governance controls, create persistence contracts, or supply production evidence. It refines the planned assembly names and physical project layout without changing ownership or runtime boundaries, so no ADR is needed; `docs/architecture/platform-implementation.md` must be synchronized for both the naming refinement and the repository's current executable state.

## Goals / Non-Goals

**Goals:**

- Establish a minimal .NET 10 project graph that can grow without collapsing module ownership into the API host.
- Make Aspire the single local entry point and reuse ServiceDefaults for health and telemetry wiring.
- Provide executable evidence for host startup, operational endpoints, OpenAPI generation, and module dependency rules.
- Keep dependency versions and compiler policy consistent across all platform projects.
- Centralize stable technical behavior that has multiple real consumers without creating a generic shared-code dumping ground.

**Non-Goals:**

- Define domain models, application handlers, persistence, migrations, outbox behavior, brokers, object storage, authentication, authorization, or generated clients.
- Add Dockerfiles, Docker Compose, deployment configuration, product CI, or production operational values.
- Scaffold Segment Service, Analyst Manager, Hermes, clients, agents, or analyst runtimes.
- Claim evidence for the infrastructure-backed acceptance targets in the architecture profile.

## Decisions

### 1. Keep the solution and peer platform projects under `src/platform`

Use `src/platform/SocAlytics.Platform.slnx` with centrally managed build and package settings in that directory. Add these production projects:

- `SocAlytics.Platform.Api`
- `SocAlytics.Platform.AppHost`
- `SocAlytics.Platform.ServiceDefaults`
- `SocAlytics.Platform.Club`
- `SocAlytics.Platform.IdentityAccess`
- `SocAlytics.Platform.Recordings`
- `SocAlytics.Platform.Registry`
- `SocAlytics.Platform.Analysis`
- `SocAlytics.Platform.AgentOrchestration`

Add `Tests/SocAlytics.Platform.Host.Tests` and `Tests/SocAlytics.Platform.Architecture.Tests`. Use `Directory.Build.props`, `Directory.Packages.props`, and `global.json` to enable nullable analysis, implicit usings, warnings as errors for repository-authored code, deterministic builds, central package versions, and the .NET 10 SDK policy.

All production projects are direct children of `src/platform`; the capability assemblies do not receive a separate `Modules` directory or `.Modules.` namespace segment. The common `SocAlytics.Platform` prefix states source ownership, while the final segment identifies the capability. This keeps navigation flat and avoids encoding the modular-monolith pattern redundantly in every project name.

This keeps the first executable component within its approved source owner and avoids creating a repository-wide solution before another source area has executable projects. A root solution was considered, but it would imply cross-area build ownership that does not yet exist. Grouping capability projects under `Modules/` was considered but rejected because the extra physical and namespace level adds no enforceable boundary. A project-per-layer topology was also considered, but it would introduce shared domain/application abstractions before any domain behavior exists.

### 2. Give each module one public composition boundary

Each module is one class library with a module-specific public dependency-injection registration extension. All other initial module types remain internal. The API references each module project only to invoke that public registration boundary; module projects do not reference one another.

This creates an executable boundary without inventing domain contracts. A shared `IModule` abstraction was considered but rejected for this increment because six direct registration calls are explicit and do not justify another shared project. Empty placeholder projects were rejected because they would not prove host composition.

Shared behavior follows a focused DRY policy:

- common compiler and dependency policy belongs in `Directory.Build.props` and `Directory.Packages.props`;
- shared local-hosting behavior belongs in `SocAlytics.Platform.ServiceDefaults`;
- capability-specific domain behavior remains with its owning project even when another capability has superficially similar code; and
- a focused shared project is introduced only when stable behavior or a contract has multiple real consumers and must change consistently.

Future cross-cutting application, persistence, or contract behavior may justify projects such as `SocAlytics.Platform.Application`, `SocAlytics.Platform.Persistence`, or `SocAlytics.Platform.Contracts`. This increment adds none of them because it has no concrete shared behavior beyond build policy and ServiceDefaults. A generic `Shared` project was rejected because it would obscure ownership and invite unrelated dependencies.

### 3. Keep the API dependency-free and operational-only

The API uses the ASP.NET Core empty host shape, calls ServiceDefaults, registers all six modules, maps Aspire health endpoints, and publishes the built-in OpenAPI document named `v1` at `/openapi/v1.json`. It exposes no scaffold domain endpoint. The documented health paths are `/alive` for liveness and `/health` for readiness.

Using a sample weather or club endpoint was rejected because sample domain behavior would be mistaken for an adopted contract. An OpenAPI UI was also rejected; the machine-readable document is sufficient until a meaningful API surface exists.

### 4. Use Aspire only for local composition

The AppHost references the API as its sole resource and waits on the API health check. ServiceDefaults supplies health checks, service discovery, resilient HTTP defaults, and OpenTelemetry wiring using the standard Aspire pattern. The AppHost adds no PostgreSQL, NATS, S3, or container resources.

Docker Compose alone was considered but conflicts with the adopted role of Aspire for local composition. Adding future dependencies now was rejected because connectivity-only resources would widen scope without proving domain behavior and could be confused with production topology.

### 5. Separate host behavior tests from architecture tests

`SocAlytics.Platform.Host.Tests` uses Aspire hosting test support, xUnit v3, and Shouldly to launch the AppHost, wait for the API resource to become healthy, and verify `/alive`, `/health`, and `/openapi/v1.json` through the composed resource's HTTP client. The smoke test also confirms that all six module registrations execute during host startup.

`SocAlytics.Platform.Architecture.Tests` uses NetArchTest.Rules plus direct project-reference assertions to verify that module namespaces do not depend on other module namespaces, module project files do not reference one another, and the API references module assemblies only through their public composition types. Internal-by-default module implementation makes prohibited host access a compile-time failure; reflection assertions verify that the initial module surface has not accidentally become public.

A direct ASP.NET Core test host was considered but rejected because it would not prove the Aspire composition path. A single mixed test project was also rejected because behavioral host failures and structural boundary failures have different ownership and diagnostic needs. Testcontainers are deferred because this increment has no external infrastructure boundary.

### 6. Document exact scoped commands and current evidence

The root README and `src/platform/README.md` document commands using the explicit solution and AppHost project paths:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

Update `AGENTS.md` and `docs/architecture/platform-implementation.md` where they currently say no executable projects or commands exist. Preserve all statements that infrastructure-backed, security, client, and production evidence remains absent.

## Risks / Trade-offs

- **[Module shells may look more complete than they are]** → Keep module content limited to composition markers and state the deferred behavior prominently in platform documentation.
- **[Architecture tests can overfit initial names and paths]** → Assert adopted dependency and visibility rules, not incidental file counts or source layout below each module.
- **[Aggressive deduplication can couple independently evolving capabilities]** → Extract only stable behavior with multiple real consumers; keep coincidentally similar domain rules capability-owned.
- **[Delayed extraction can leave repeated technical behavior]** → Review repeated non-domain behavior when adding each consumer and move it into a narrowly named owner once consistency is required.
- **[Aspire package and workload compatibility can drift]** → Pin centrally compatible package versions and the .NET 10 SDK feature band, then validate restore, build, test, and AppHost startup together.
- **[Warnings-as-errors can include generated-code diagnostics]** → Apply strict policy to repository-authored projects while using targeted exclusions only when generated Aspire or OpenAPI artifacts require them.
- **[Unauthenticated operational endpoints could be mistaken for a production security posture]** → Keep the host dependency-free and development-scoped, expose no domain data, and document that ingress, identity, authorization, and production controls remain unresolved.

## Migration Plan

1. Add shared SDK, package, solution, and peer project metadata without changing existing source-area ownership.
2. Add module composition boundaries, then wire ServiceDefaults, API, and AppHost.
3. Add host and architecture tests and run restore, build, test, and a bounded AppHost startup check.
4. Update repository, platform, agent, and architecture documentation with only the commands and evidence actually validated.

Rollback removes the new platform projects and restores the prior documentation statements; no data or deployed runtime migration is required.
