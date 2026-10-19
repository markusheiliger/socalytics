# Quickstart: Agent Orchestration Direct Invocations

Validation guide for the feature. Operations and payloads are in
[contracts/agent-orchestration-api.md](contracts/agent-orchestration-api.md); tables and states in
[data-model.md](data-model.md).

## Prerequisites

- .NET 10 SDK (`src/platform/global.json`) and a running Docker-compatible container runtime.
- From the repository root: `dotnet restore src/platform/SocAlytics.Platform.slnx` and
  `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`.

## Automated evidence

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~AgentOrchestration"
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter OpenApiSnapshotTests
dotnet test src/platform/Tests/SocAlytics.Platform.Architecture.Tests --no-build
```

Expected: every test passes; the integration tests cover the scenarios of User Stories 1 to 3, including the
simulated agent workload runner hosted in-process against a disposable PostgreSQL and NATS.

## Local walkthrough

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost`; the dashboard shows `api` and
   `simulated-agent-workload` running.
2. Sign in as the bootstrap Club Admin, create a season, team, and match, and produce a `Completed` analysis run (for
   example by uploading and finalizing a recording and letting an Analyst Manager with its simulated runtime execute
   the Development seed workflow, as in the analysis workflow quickstarts).
3. `POST /api/v1/matches/{matchId}/agent-conversations` with an `Idempotency-Key` and `{ "analysisRunId": "…" }`:
   expect `201`.
4. `GET /api/v1/agent-catalog/agents`: expect `specialist-tactics` and `specialist-video-analysis`.
5. Add `specialist-tactics` with `If-Match` set to the conversation `ETag`: expect `200`.
6. Submit `{ "agentId": "specialist-tactics", "question": "Where did our pressing break down?" }` with an
   `Idempotency-Key`: expect `202` with `Location`. Repeat the same request: expect the same invocation.
7. Poll `Location` with `If-None-Match`: expect `304` until the state changes, then `succeeded` with simulated
   advice within 30 seconds.
8. `GET /api/v1/agent-invocations/{id}/lineage`: expect `submitted`, `claimed`, `checkpoint`, `completed` in order.
9. Submit questions starting with `[simulate:fail-once]`, `[simulate:fail-retryable]`, `[simulate:fail-permanent]`,
   and `[simulate:stall]`: expect a retry and then success, failure with reason `attempts-exhausted` after three
   attempts, an immediate failure with reason `execution-failed`, and a lease expiry followed by a successful second
   attempt.
10. Read the conversation as another Coach of the team: expect `404`.

Successful local runs are development evidence only; they say nothing about production readiness.
