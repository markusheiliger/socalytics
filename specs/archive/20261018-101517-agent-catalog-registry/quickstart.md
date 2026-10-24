# Quickstart: Agent Catalog Registry

Validation guide for the feature. Operations and payloads are in [contracts/agent-catalog-api.md](contracts/agent-catalog-api.md),
tables and rules in [data-model.md](data-model.md).

## Prerequisites

- .NET 10 SDK (`src/platform/global.json`) and a running Docker-compatible container runtime.
- Agent Orchestration Direct Invocations (`specs/archive/20261017-204332-agent-orchestration-invocations`) merged.

## Automated checks

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~AgentOrchestration"
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected: the nine `contracts/agent-catalog/*` contracts and `execution-envelope` 1.1.0 validate with their fixtures;
the catalog, pinning, and existing orchestration tests pass; the OpenAPI snapshot test passes against the refreshed
snapshot.

## Local scenario

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` and sign in to the API as a Club Admin.
2. `GET /api/v1/agent-catalog/enablements`: `specialist-tactics` and `specialist-video-analysis` are enabled at
   `0.1.0` by the Development seed; every other agent has no enabled version.
3. Register a new tool policy for `specialist-tactics` that drops `get_heatmap`, an agent version `0.2.0` that reuses
   the seeded prompt, model selection, and safety policy, approve it with its `ETag`, and enable it with the
   enablement's `ETag`.
4. As a Coach, create a triage conversation, add `specialist-tactics`, submit a question; `GET` the invocation shows
   `agent.version` `0.2.0` and the artifact digests; the simulated agent workload reads the pinned artifacts and
   completes with matching references; the lineage shows the same pins.
5. Enable `0.1.0` again; the earlier invocation's reads still show `0.2.0`.
6. Try a profile with weights totalling 99 and a tool policy with `get_weather`: each answers
   `400 agent-catalog-validation-failed` naming every violation; try enabling a Coach Agent version: `409
   coach-agent-enablement-unsupported`.

Passing these checks is development evidence only; it does not establish evaluation, promotion, deployment support, or
production readiness.
