# Implementation Plan: Agent Evidence Tools API

**Branch**: `20261018-104924-agent-evidence-tools` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/archive/20261018-104924-agent-evidence-tools/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

The platform gains its MCP-facing application tools: a dedicated `/api/v1/agent-tools` route group with exactly one
read-only operation per tool identifier of the catalog baseline (eleven, with stable `agentTool*` operation
identifiers), described by the versioned contract `contracts/agent-tools/tool-allowlist/v1`. Only the configured agent
workload identity may call them, for its current attempt with the current fencing token, and only for tools in the
invocation's pinned tool policy. Each call re-evaluates the initiating member's execution authority with the
orchestration's scope evaluator and, on scope loss, fences the attempt and fails the invocation in the same unit of
work; it then reserves the call (unique call identifier, per-attempt limits) in an immutable `agent_tool_calls` row,
runs the read outside the invocation lock through the read cores shared with the member fact and evidence-media
operations, and appends a `tool-call` lineage entry with digests and evidence references only. `get_match` returns the
match, bound run, and fact streams, and with a fact selection a page of accepted facts with coverage; `get_video_clip`
resolves evidence media and returns links only to the caller; `get_player_stats` and the eight concept tools answer
"analysis unavailable" naming their producing capability. The simulated agent workload calls the tools. See
[research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: ASP.NET Core minimal APIs and authorization, Dapper and Npgsql, DbUp, JsonSchema.Net (contract
tests), Microsoft.Extensions.Hosting (simulated workload), Aspire hosting. All are already pinned in
`src/platform/Directory.Packages.props`; no new dependency.

**Storage**: PostgreSQL through the persistence foundation: new immutable table `agent_tool_calls`; the
`agent_lineage_entries.kind` check constraint gains `tool-call`; one forward-only migration (see
[data-model.md](data-model.md)). No evidence content is persisted; links are never stored.

**Testing**: xUnit v3 integration tests with Docker-backed PostgreSQL, NATS, and S3-compatible storage
(`SocAlytics.Platform.Integration.Tests`), contract tests (`SocAlytics.Platform.Contracts.Tests`), Host tests
(`OpenApiSnapshotTests`), architecture tests (route group and allowlist equality, layer rules).

**Target Platform**: The API host on Linux containers and developer machines through the Aspire AppHost; the simulated
agent workload only in Development and Test.

**Project Type**: Web service (platform monolith).

**Performance Goals**: Tool answers within the latency of the member reads they reuse; the invocation lock is held only
for authorization and lineage append, never during reads; a member's question is answered within 30 seconds with the
simulated workload (SC-006).

**Constraints**: Read-only tools only; no evidence content, links, or payloads in storage, lineage, logs, audit, or
telemetry; no concept derivation; scope loss fails closed; production limits stay open.

**Scale/Scope**: One club per stamp; eleven tool operations; up to 50 tool calls and 10 clip resolutions per attempt
(development); one new contract; one migration; one changed orchestration read (lineage).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | The feature implements the platform side of [MCP Tools](../../../docs/architecture/intelligence-and-agents.md#mcp-tools), the evidence rules of [Authorization, Lineage, And Lifecycle](../../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle), and the "MCP-facing application tools" area of [Control Plane](../../../docs/architecture/platform-implementation.md#control-plane); decisions not yet stated there (route group, allowlist contract, `get_match` fact selection, unavailable concept tools, limits, lineage entries) are applied now as decided but not implemented (see Architecture Updates). | Pass |
| II. Respect Source-Area Ownership | Production code lives in the existing platform layers inside the Agent Orchestration functional area; the simulated workload stays under `src/platform/Tests`; `src/agents/` stays reserved for Hermes and the later MCP server is not added; no new first-level area or project. | Pass |
| III. API-First Control Plane | Agents reach evidence only through `/api/v1` operations in the OpenAPI document; the allowlist is a JSON Schema 2020-12 contract; the API never reads or relays media bytes and returns per-object links only. | Pass |
| IV. Evidence Over Claims | Integration, contract, Host, and architecture tests cover every requirement (FR-021); passing tests are not presented as an MCP server, Hermes, or production readiness. | Pass |
| V. Focused, Minimal Changes | Reuses the workload authentication, scope evaluator, scope-loss transition, lineage store, fact and evidence read cores (split, not duplicated), `CanonicalJson`/`ContentDigest`, `ProblemResults`, the audit trail, and the simulator conventions; no new dependency or project. | Pass |
| Technology and Tooling | Needs only .NET 10 and Docker (PostgreSQL, NATS, S3-compatible storage), which `.github/actions/environment-setup` and `environment-verify` provide; the platform verification scope covers `src/platform/` and `contracts/`; no environment spec is needed. | Pass |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Architecture Updates

**Narratives** (implemented or changed by this feature):

- [Intelligence and Agents → MCP Tools](../../../docs/architecture/intelligence-and-agents.md#mcp-tools): the allowlist,
  the route group, attempt and policy binding, `get_match`, `get_video_clip`, unavailable concept tools, limits, and
  lineage.
- [Intelligence and Agents → Authorization, Lineage, And Lifecycle](../../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle):
  re-evaluation and fencing at every evidence tool call; MCP calls and evidence references in lineage.
- [Intelligence Agent Catalog → MCP Tool Policy](../../../docs/architecture/intelligence-agent-catalog.md#mcp-tool-policy):
  enforcement of the pinned tool policy.
- [Platform Implementation Profile → Control Plane](../../../docs/architecture/platform-implementation.md#control-plane)
  ("MCP-facing application tools") and [Agent Runtime](../../../docs/architecture/platform-implementation.md#agent-runtime):
  the `Tools` folder and table.
- [Analyst Capability Catalog](../../../docs/architecture/analyst-capability-catalog.md) (evidence clips paragraph) and
  [Analysts, Models, and Hardware → Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access):
  the API side of `get_video_clip()` for agents.
- [Security and Data Governance](../../../docs/architecture/security-and-data-governance.md):
  [Audit Events](../../../docs/architecture/security-and-data-governance.md#audit-events), `CTL-007`, `THR-007`, and
  `DAT-008` (tool calls and evidence references in agent state).

**Design updates** (applied now to the owning narratives, worded as decided but not yet implemented; the
implementation turns each into present tense)

1. `intelligence-and-agents.md` MCP Tools: a paragraph on the `/api/v1/agent-tools` route group with one read-only
   operation per tool and stable `agentTool*` identifiers, the `contracts/agent-tools/tool-allowlist/v1` contract equal
   to the catalog baseline's tool identifiers, workload-only, attempt-bound, policy-bound calls without team, match, or
   run arguments, `get_match()` with an optional fact selection, `get_video_clip()` returning links to the caller only,
   "analysis unavailable" answers of `get_player_stats()` and the concept tools, per-attempt limits, and `tool-call`
   lineage entries without evidence content; the MCP server process stays future work.
2. `intelligence-and-agents.md` Authorization, Lineage, And Lifecycle: a paragraph that every evidence tool call
   re-evaluates execution authority, that scope loss denies the call, fences the attempt, and fails the invocation in
   one transaction with stale dispositions afterwards, and that a tool outside the pinned policy is refused without
   fencing.
3. `intelligence-agent-catalog.md` MCP Tool Policy: the platform enforces the pinned tool policy on every call and
   refuses other tools without fencing.
4. `platform-implementation.md` Agent Runtime: a paragraph on the `Tools` folder, the `agent_tool_calls` reservation
   table, reads outside the invocation lock through the shared read cores, and the `tool-call` lineage entry.
5. `security-and-data-governance.md` Audit Events: agent evidence tool refusals, scope-loss denials, and evidence-media
   grants to the agent workload (identifiers and digests only, never links or fact payloads).

**Status statements at merge** (each must become true for the merged state)

- `intelligence-and-agents.md` MCP Tools, `get_video_clip()` bullet: "(its API side, the member evidence-media
  operation …, is decided, not yet implemented; the MCP tool is future work)" → the member operation and the agent
  tool operation exist (once the evidence-clip feature has merged); only the MCP server exposing it is future work.
  Design update 1 loses "decided and not yet implemented" except for the MCP server.
- `intelligence-and-agents.md` Authorization, Lineage, And Lifecycle: design update 2 loses "decided and not yet
  implemented".
- `intelligence-agent-catalog.md` introduction: "The catalog entries, prompt text, philosophy artifacts, model
  execution, MCP evidence tools, Hermes orchestration runtime, and user experiences are likewise design inputs …" →
  the evidence tool operations exist as development evidence behind the workload; the MCP server, Hermes, model
  execution, and user experiences remain design inputs (coordinate with the wording the Agent Catalog Registry left).
  MCP Tool Policy: design update 3 loses "decided, not yet implemented".
- `analyst-capability-catalog.md` evidence clips paragraph: "The API side is decided, but not yet implemented, …; the
  MCP tool remains future work." → the API side exists for members and agents; the MCP server remains future work.
- `analysts-models-and-hardware.md` Capability-Scoped Result Access: "This is the API side of `get_video_clip()`; the
  MCP tool remains future work." → the agent tool operation reuses it; the MCP server remains future work.
- `platform-implementation.md` Agent Runtime: "MCP tools, prompt/profile resolution, model execution, and Web UI agent
  workflows remain future implementation work." → the API side of the MCP tools exists; the MCP server, prompt and
  profile rendering, model execution, and Web UI agent workflows remain future work; design update 4 loses "decided
  and not yet implemented".
- `platform-implementation.md` Control Plane: "MCP-facing application tools" stays true and now has an
  implementation; confirm at merge.
- `security-and-data-governance.md` Audit Events: design update 5 loses "decided, not yet implemented". `CTL-007`
  (Provisional) and `THR-007`/`GOV-THR-007` (Open / Blocking): keep their decision status, which this development
  evidence does not resolve; confirm at merge that no statement claims the control is unimplemented.
- `tenancy-and-technology.md` AI ("MCP with an explicit agent-safe read-only API allowlist") and `overview.md`
  (MCP boundary rows and diagrams): confirm at merge that they still hold.
- `AGENTS.md` "Current State" (outside `docs/architecture/`, kept current by the same documentation task): names the
  agent evidence tools route group, the allowlist contract, and their development evidence, and keeps the MCP server
  and Hermes among what does not exist.

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the listed
narratives and every status statement this feature affects in `docs/architecture/` true for the merged state,
re-reading the documents at implementation time (including `README.md`, `overview.md`,
`terminology-and-principles.md`, and any other planned, future-tense, or "not implemented" wording about MCP tools,
evidence tools, or agent evidence access); the rewrites listed above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261018-104924-agent-evidence-tools/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── agent-tools-api.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/agent-tools/
└── tool-allowlist/v1/                 # schema, releases/1.0.0, valid allowlist-1.0.0 and invalid fixtures
contracts/README.md                    # index row

src/platform/
├── SocAlytics.Platform.Domain/AgentOrchestration/Tools/
│   ├── AgentToolAllowlist.cs          # version 1.0.0 and the eleven entries
│   └── ToolCallOutcome.cs             # outcomes, reasons, evidence references
├── SocAlytics.Platform.Application/
│   ├── Analysis/Facts/                # split ListRunFactStreams / ListRunFacts into member handlers and IRunFactStreamReader / IRunFactReader cores
│   ├── Analysis/Evidence/             # split ResolveEvidenceMedia into the member handler and IEvidenceMediaResolver core
│   ├── AgentOrchestration/Tools/
│   │   ├── AgentToolOptions.cs        # AgentOrchestration:Tools limits, validated at start
│   │   ├── AuthorizeToolCall.cs       # binding, scope evaluation and fencing, policy, call id, limits, reservation
│   │   ├── GetMatchTool.cs            # overview and fact selection
│   │   ├── GetVideoClipTool.cs
│   │   ├── UnavailableAnalysisTool.cs # get_player_stats and the eight concept tools
│   │   ├── ToolCallLineage.cs         # digests, evidence references, tool-call entry append
│   │   ├── IAgentToolCallStore.cs
│   │   └── AgentToolAuditEvents.cs
│   ├── AgentOrchestration/Execution/MonitorAgentAttempts.cs   # close reservations without lineage as interrupted
│   ├── Telemetry/AgentToolMetrics.cs  # new meter
│   └── ApplicationServiceCollectionExtensions.cs
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Migrations/NNNN_agentorchestration_tool_calls.sql   # numbered at implementation time
│   ├── Persistence/AgentOrchestration/Tools/AgentToolCallStore.cs
│   ├── Persistence/PostgresAuditTrail.cs                # detail allow-list
│   └── InfrastructureServiceCollectionExtensions.cs
├── SocAlytics.Platform.Api/
│   ├── AgentOrchestration/AgentToolEndpoints.cs         # MapAgentToolApi() and the eleven operations
│   ├── AgentOrchestration/AgentToolAuthorizationResultHandler.cs  # audit caller-not-workload refusals
│   ├── Program.cs                                       # mapping
│   ├── appsettings.Development.json                     # AgentOrchestration:Tools
│   └── openapi/v1.json                                  # refreshed OpenAPI snapshot
└── Tests/
    ├── SocAlytics.Platform.SimulatedAgentWorkload/      # tool calls and [simulate:tools-*] markers
    ├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/Tools/   # new tests
    ├── SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs
    ├── SocAlytics.Platform.Contracts.Tests/             # catalog picks up the new contract
    └── SocAlytics.Platform.Architecture.Tests/          # route group equals allowlist; read-only rule
```

**Structure Decision**: The tools are a `Tools` sub-folder of the existing Agent Orchestration functional area in each
layer, so attempt binding, scope loss, and lineage stay inside the area that owns them, and the area depends on the
Analysis read cores only through Application interfaces. The fact-indexing and evidence-clip handlers are split into
member handlers and shared cores without changing member behavior (their existing tests keep passing).
`PersistedTableClassifications` classifies `agent_tool_calls` as immutable. The OpenAPI snapshot is refreshed with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
during implementation, never in the verification environment. This feature is implemented after the orchestration,
catalog registry, fact-indexing, evidence-clip, and supersession features have merged and changes their code in place.
