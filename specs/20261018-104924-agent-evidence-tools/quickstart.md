# Quickstart: Agent Evidence Tools API

Validation guide for the feature; see [contracts/agent-tools-api.md](contracts/agent-tools-api.md) and
[data-model.md](data-model.md) for the shapes. Passing these steps is development evidence only.

## Prerequisites

- .NET 10 SDK (`src/platform/global.json`) and a running Docker-compatible container runtime.
- The features this one builds on are merged: Agent Orchestration Direct Invocations, Agent Catalog Registry, Accepted
  Fact Indexing and Capability-Scoped Fact Queries, Evidence Clip Access, and Analysis Run Supersession.

## Build and automated tests

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~AgentOrchestration.Tools"
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --no-build
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected: the tool tests prove equal answers to the member reads, attempt binding and every stale disposition, the
pinned tool policy, call identifiers and limits, scope loss and fencing, "analysis unavailable" answers, lineage and
audit content without links or payloads; the contract tests validate the `tool-allowlist` fixtures; the Host tests'
`OpenApiSnapshotTests` match the refreshed snapshot; the architecture tests confirm that the `/api/v1/agent-tools`
group contains exactly the allowlisted operations.

After changing the API, refresh the snapshot outside the verification environment:

```powershell
$env:SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT = '1'; dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests
```

## Local end-to-end check

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` (starts PostgreSQL, NATS, storage, the Migrator,
   the API, and the simulated agent workload).
2. As a Coach of a team with a completed analysis run, create a triage conversation for that run, add
   `specialist-tactics`, and submit a question that starts with `[simulate:tools-facts]`.
3. Poll the invocation until it has succeeded (within 30 seconds), then read its lineage.

Expected: the lineage lists `tool-call` entries for `get_match` (twice: overview and fact selection) and
`get_formations` (outcome `unavailable`, `formation-analysis`), each with digests and evidence references and without
fact payloads or links; the advice names the tool outcomes as simulated.
