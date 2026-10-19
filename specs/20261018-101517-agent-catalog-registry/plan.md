# Implementation Plan: Agent Catalog Registry

**Branch**: `20261018-101517-agent-catalog-registry` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261018-101517-agent-catalog-registry/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

The platform becomes the authority for the Coach Agent and Specialist Agent catalog. A `Catalog` folder inside the
Agent Orchestration functional area of every platform layer stores immutable, content-addressed records of seven
artifact kinds (prompt template, model selection, tool policy, safety policy, dimension set, philosophy profile,
modifier policy) and agent versions that bind one of the nine catalog agent identifiers to exactly one artifact of each
required kind. Club Admins register records through `/api/v1`; each record is validated against a new JSON Schema 2020-12
contract in `contracts/agent-catalog/` through the existing `IContractValidator` and against the catalog rules in the
Domain (tool allowlist and per-agent baseline, 100-point profiles keyed by dimension, dimension-set compatibility,
modifier bounds, disclosure), and its digest is the SHA-256 of its canonical JSON (`ContentDigest`). Agent versions move
through draft, approved, and retired; one approved Specialist Agent version per identifier can be enabled per stamp
(Coach Agents are refused until the Coach Agent conversations feature). Agent Orchestration replaces its configured
enabled-agent list with the catalog enablement, pins the enabled version and every artifact digest into each invocation
at submission, carries the pin in `execution-envelope` 1.1.0, checks completion references against it, and serves the
pinned artifacts to the current attempt through an attempt-scoped workload read. A Development and Test seed registers
and enables `specialist-tactics` and `specialist-video-analysis` from the catalog's first-draft prompts. See
[research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: ASP.NET Core minimal APIs, Dapper and Npgsql, DbUp, JsonSchema.Net (runtime contract validation
through `IContractValidator` and contract tests), Microsoft.Extensions.Hosting (seeder), Aspire hosting. All are already
pinned in `src/platform/Directory.Packages.props`; no new dependency.

**Storage**: PostgreSQL through the persistence foundation: new tables `agent_catalog_artifacts` (immutable),
`agent_versions` (versioned lifecycle, immutable content), `agent_enablements` (versioned, one row per catalog agent
identifier), and `agent_invocation_pins` (immutable); `agent_invocations` gains the nullable pinned agent version columns;
one forward-only migration (see [data-model.md](data-model.md)).

**Testing**: xUnit v3 integration tests with Docker-backed PostgreSQL and NATS (`SocAlytics.Platform.Integration.Tests`),
Domain rule tests in the same project, contract tests (`SocAlytics.Platform.Contracts.Tests`), Host tests
(`OpenApiSnapshotTests`), architecture tests.

**Target Platform**: The API host on Linux containers and developer machines through the Aspire AppHost; the seed and the
simulated provider only in Development and Test.

**Project Type**: Web service (platform monolith).

**Performance Goals**: Registration, approval, and enablement answer within normal API latency; submission resolves and
pins the enabled version in the same unit of work without an extra round trip; a Club Admin can register, approve, and
enable a version and see it pinned within 2 minutes locally (SC-006).

**Constraints**: Records are immutable and never deleted; no secret is accepted in a model selection; prompt, rationale,
and provenance text never in audit, logs, or telemetry; pinned invocations never change; Coach Agent enablement and
evaluation gates stay out; production values stay open.

**Scale/Scope**: One club per stamp; nine catalog agent identifiers; tens of versions per agent; prompt templates up to
32,000 characters; eleven new member operations, one new workload operation, changed orchestration operations (agent
list, participant addition, submission, invocation and lineage reads, claim, completion), nine new contracts, one
contract revision.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | The feature implements the catalog-authority parts of [Intelligence Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md) and [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md); decisions not yet stated there (catalog registry in Agent Orchestration, pinning at submission, attempt-scoped artifact read, registry approval versus evaluation evidence) are applied now as decided but not implemented (see Architecture Updates). | Pass |
| II. Respect Source-Area Ownership | Production code lives in the existing platform layers inside the Agent Orchestration functional area; `src/agents/` stays reserved for Hermes; no new first-level area or project. | Pass |
| III. API-First Control Plane | Club Admins and the workload use only `/api/v1` operations in the OpenAPI document; nine JSON Schema 2020-12 contracts are authoritative for the records and validated at runtime from the embedded contract catalog. | Pass |
| IV. Evidence Over Claims | Integration, Domain, contract, Host, and architecture tests cover every requirement (FR-028); registry approval is not presented as evaluation or promotion evidence or production readiness. | Pass |
| V. Focused, Minimal Changes | Reuses `IContractValidator`, `CanonicalJson`/`ContentDigest`, the Analyst Registry lifecycle pattern, `IfMatchHeader`, `ProblemResults`, the audit trail, and the orchestration workload authentication; no new dependency or project. | Pass |
| Technology and Tooling | Needs only .NET 10 and Docker (PostgreSQL, NATS), which `.github/actions/environment-setup` and `environment-verify` provide; the platform verification scope covers `src/platform/` and `contracts/`; no environment spec is needed. | Pass |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Architecture Updates

**Narratives** (implemented or changed by this feature):

- [Intelligence Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md): introduction,
  [Catalog Rules](../../docs/architecture/intelligence-agent-catalog.md#catalog-rules),
  [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance),
  [Base Philosophy Profiles](../../docs/architecture/intelligence-agent-catalog.md#base-philosophy-profiles),
  [Context Modifiers](../../docs/architecture/intelligence-agent-catalog.md#context-modifiers),
  [Adding Or Revising A Coach Agent](../../docs/architecture/intelligence-agent-catalog.md#adding-or-revising-a-coach-agent),
  [MCP Tool Policy](../../docs/architecture/intelligence-agent-catalog.md#mcp-tool-policy),
  [Implementation Waves](../../docs/architecture/intelligence-agent-catalog.md#implementation-waves), and
  [Validation And Promotion Gates](../../docs/architecture/intelligence-agent-catalog.md#validation-and-promotion-gates):
  the registry of versioned artifacts, their formats and validation, approval and enablement, and Wave 0's artifact
  formats.
- [Intelligence and Agents → Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)
  and [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority):
  pinning at submission, the envelope's pinned references, the attempt-scoped artifact read, and how Hermes resolves
  artifacts.
- [Platform Implementation Profile → Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime)
  and [Data Organization](../../docs/architecture/platform-implementation.md#data-organization): the catalog folder
  and tables.
- [Tenancy and Technology → AI](../../docs/architecture/tenancy-and-technology.md#ai): where Hermes' versioned
  artifacts come from.
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md):
  [Data Classes](../../docs/architecture/security-and-data-governance.md#data-classes) (`DAT-007`) and
  [Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events).

**Design updates** (applied now to the owning narratives, worded as decided but not yet implemented; the
implementation turns each into present tense)

1. `intelligence-agent-catalog.md` Prompt And Advice Governance: a paragraph that the platform's agent catalog
   registry, in the Agent Orchestration functional area, is the authority for immutable, content-addressed agent
   versions and their artifacts (formats as contracts in `contracts/agent-catalog/`), registered, approved, retired,
   and enabled by Club Admins until a stamp-operator role exists; one approved version per agent identifier is enabled
   per stamp; submission pins the enabled version and every artifact digest into the invocation, so later catalog
   changes never alter running or historical invocations.
2. `intelligence-agent-catalog.md` Catalog Rules: registry approval records an accountable decision only; the
   evaluation artifacts and promotion gates remain required for production selection and are not yet enforced, while
   Development and Test may enable approved versions without them.
3. `intelligence-agent-catalog.md` Implementation Waves: Wave 0's artifact formats are decided as the
   `contracts/agent-catalog/` contracts (not yet implemented); the evaluation suites remain open.
4. `intelligence-and-agents.md` Client, Transport, And Runtime Protocol: Hermes does not choose versions; it resolves
   the artifacts Agent Orchestration pinned at submission from the claim's envelope and reads them through an
   attempt-scoped workload read with the current fencing token; a completion whose declared prompt, tool-policy, or
   safety-policy references differ from the pins is refused.
5. `platform-implementation.md` Agent Runtime: a paragraph on the catalog folder (tables `agent_catalog_artifacts`,
   `agent_versions`, `agent_enablements`, `agent_invocation_pins`, the Development and Test seed of the two direct
   specialists, and the replacement of the configured enabled-agent list).
6. `tenancy-and-technology.md` AI: the versioned artifacts Hermes resolves are those pinned from the platform's agent
   catalog at submission.
7. `security-and-data-governance.md` `DAT-007` includes agent catalog records (prompt, model-selection, tool-policy,
   safety-policy, and philosophy artifacts and agent versions; decided, not yet implemented), and Audit Events lists
   agent catalog registration, approval, retirement, and enablement changes (identifiers and digests only, never
   prompt, rationale, or provenance text; decided, not yet implemented).

**Status statements at merge** (each must become true for the merged state)

- `intelligence-agent-catalog.md` introduction: "The catalog entries, prompt text, philosophy artifacts, model
  execution, MCP evidence tools, Hermes orchestration runtime, and user experiences are likewise design inputs rather
  than deployed behavior." → catalog records, artifact formats, philosophy-artifact validation, enablement, and pinning
  exist as development evidence; the prompt drafts, profile values, and model choices stay Provisional design inputs;
  model execution, MCP tools, Hermes, and user experiences stay design inputs. "The prompts in this document are design
  inputs, not executable agent definitions." stays true and names the registry as the place of executable definitions.
- `intelligence-agent-catalog.md` Base Philosophy Profiles: "Their eventual artifact digests are assigned at promotion
  rather than invented in this architecture document." → digests are computed by the registry at registration; the
  three profiles are not registered by any seed.
- `intelligence-agent-catalog.md` Adding Or Revising A Coach Agent: "A future machine-oriented profile uses dimension
  IDs as keys" → the `philosophy-profile` contract keys weights by dimension ID.
- `intelligence-agent-catalog.md` Implementation Waves, Wave 0: "define prompt, model, tool-policy, and safety-policy
  artifact formats" → formats defined; the test suites remain open. Design updates 1–3 lose "decided, not yet
  implemented".
- `intelligence-and-agents.md` Client, Transport, And Runtime Protocol: design update 4 loses "decided, not yet
  implemented"; Hermes itself stays planned.
- `platform-implementation.md` Agent Runtime: "MCP tools, prompt/profile resolution, model execution, and Web UI agent
  workflows remain future implementation work." → the catalog, pinning, and the attempt-scoped read exist; prompt and
  profile rendering, model execution, MCP tools, and Web UI catalog administration remain future work; design update 5
  loses "decided, not yet implemented".
- `tenancy-and-technology.md` AI, `security-and-data-governance.md` `DAT-007` and Audit Events: the wording added now
  loses "decided, not yet implemented".
- `overview.md` "The Intelligence Agent Catalog provides the canonical initial CA and SA roster, prompt drafts, tool
  policies, …": confirm at merge that it still holds (the registry implements the catalog without changing decision
  statuses).
- `AGENTS.md` "Current State" (outside `docs/architecture/`, kept current by the same documentation task): names the
  agent catalog registry, pinning, and the Development and Test seed as development evidence.

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the listed
narratives and every status statement this feature affects in `docs/architecture/` true for the merged state,
re-reading the documents at implementation time (including `README.md`, `overview.md`, `terminology-and-principles.md`,
and any other planned, future-tense, or "not implemented" wording about the agent catalog, its artifacts, or how
versions are resolved); the rewrites listed above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261018-101517-agent-catalog-registry/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── agent-catalog-api.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/agent-catalog/
├── catalog-baseline/v1/            # schema, releases/1.0.0, valid baseline 1.0.0 fixture, invalid fixtures
├── prompt-template/v1/
├── model-selection/v1/
├── tool-policy/v1/
├── safety-policy/v1/
├── dimension-set/v1/               # valid fixture coach-philosophy-dimensions 1.0.0
├── philosophy-profile/v1/          # valid fixtures for the three provisional profiles
├── modifier-policy/v1/
└── agent-version/v1/
contracts/agent-orchestration/execution-envelope/v1/   # 1.1.0: optional `agent` pin; releases/1.1.0
contracts/README.md                                    # index rows

src/platform/
├── SocAlytics.Platform.Domain/AgentOrchestration/
│   ├── AgentCatalogIds.cs                             # extended into the catalog baseline (display names, tool baselines)
│   └── Catalog/                                       # record kinds, semver, validation rules, distance, pins
├── SocAlytics.Platform.Application/
│   ├── AgentOrchestration/AgentOrchestrationOptions.cs        # remove Agents:Enabled
│   ├── AgentOrchestration/Conversations/                      # participants and listAvailableAgents use enablement
│   ├── AgentOrchestration/Invocations/SubmitAgentInvocation.cs # resolve and pin
│   ├── AgentOrchestration/Execution/                          # claim (unpinned refusal, envelope agent), completion check, artifact read
│   ├── AgentOrchestration/Catalog/                            # register, read, approve, retire, enable, stores, audit events, seed
│   ├── Telemetry/AgentCatalogMetrics.cs                       # new meter
│   └── ApplicationServiceCollectionExtensions.cs
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Migrations/NNNN_agentorchestration_agent_catalog.sql   # numbered at implementation time
│   ├── Persistence/AgentOrchestration/Catalog/                # Dapper stores
│   ├── AgentOrchestration/Catalog/AgentCatalogSeeder.cs       # Development and Test seed
│   ├── AgentOrchestration/Catalog/Seed/*.json                 # seed records (first-draft prompts)
│   └── InfrastructureServiceCollectionExtensions.cs
├── SocAlytics.Platform.Api/
│   ├── AgentOrchestration/AgentCatalogEndpoints.cs            # member operations
│   ├── AgentOrchestration/AgentWorkloadEndpoints.cs           # pinned-artifact read
│   ├── appsettings.Development.json                           # remove AgentOrchestration:Agents:Enabled
│   └── openapi/v1.json                                        # refreshed OpenAPI snapshot
└── Tests/
    ├── SocAlytics.Platform.SimulatedAgentWorkload/            # read pinned artifacts, declare pinned references
    ├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/  # new tests
    ├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/          # fixtures use the seed instead of options
    ├── SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs
    └── SocAlytics.Platform.Contracts.Tests/                   # catalog picks up the new contracts
```

**Structure Decision**: The catalog is a `Catalog` sub-folder of the existing Agent Orchestration functional area in
each layer, so the platform keeps its functional-area list and the orchestration handlers resolve enablement and pins
without crossing areas. Lifecycle, approval, seed, and endpoint layout follow the Analyst Registry
(`Registry/Profiles`, `ConfiguredWorkflowSeeder`, `AnalystRegistryEndpoints`). `PersistedTableClassifications`
classifies `agent_catalog_artifacts` and `agent_invocation_pins` as immutable. The OpenAPI snapshot is refreshed with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
during implementation, never in the verification environment. This feature is implemented after
`specs/20261017-204332-agent-orchestration-invocations` has merged and changes its code in place.
