# Implementation Plan: Delegated Coach-to-Specialist Consultations

**Branch**: `20261018-121958-agent-consultations` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261018-121958-agent-consultations/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Coach Agents consult Specialist Agents through Hermes. A new catalog artifact, the consultation policy (allowed
Specialists, per-parent bound, length limits, `continue` or `fail` when a consultation is unavailable), is referenced
by Coach Agent versions (`agent-version` 1.1.0) and pinned at submission. The current attempt of a `user-to-coach`
invocation calls the new workload operation `requestAgentConsultation` (`consultation-request` 1.0.0); Agent
Orchestration checks staleness, route, scope, the pinned policy, the Specialist's enablement, its safety policy route
`coach-to-specialist`, tool-policy inheritance, and the bound, and in one transaction records the consultation (its
`pending` row is the parent's waiting edge), creates the child invocation (route `coach-to-specialist`,
`work-notification` 1.2.0, `execution-envelope` 1.3.0) with inherited context and the Specialist's pins, moves the
parent to `waiting-for-consultations`, and writes the child's outbox message. Children run as ordinary invocations;
their outcomes resolve the consultation (answered or attributed unavailable), move the parent to `synthesizing` or
queue it again, or fail it as the pinned policy says; parents that end take their unresolved children with them, and
scope loss fails the whole tree. Parent completions declare the influence of every consultation (`attempt-report`
1.3.0), which the platform checks and retains. Hermes offers consulting Coach Agents a Hermes-local
`request_consultation` function, waits in a separate waiting pool while polling `readAgentConsultationOutcomes`,
executes children with a consultation section, resumes the model with attributed results, and obtains the influence
declaration. Owner reads show consultations (responses once the parent ended), the Web UI shows them below the Coach
Agent advice and administers consultation policies, and a Development and Test seed adds consulting versions of the
seeded Coach and Specialist Agents with the restored consultation instruction. See [research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (platform, simulated agent workload, tests); Python 3.12 (Hermes; CPython 3.12.15
in verification); TypeScript with React (Web UI, Node.js 24).

**Primary Dependencies**: Existing only: the platform's Agent Orchestration, Catalog, and Tools code, Dapper, DbUp,
JsonSchema validation, and Testcontainers; Hermes' locked dependencies (`agent-framework-core`, `mcp`, `jsonschema`,
`rfc8785`); the Web UI's React, MUI, TanStack Query, the Kiota client, Vitest, and Playwright. No new dependency.

**Storage**: PostgreSQL stamp database: one forward-only migration `NNNN_agentorchestration_consultations.sql` (new
versioned table `agent_consultations`; columns and check constraints on `agent_invocations`, `agent_attempts`,
`agent_lineage_entries`, `agent_invocation_pins`, `agent_workload_operations`, `agent_accepted_advice`, and
`agent_catalog_artifacts`); seed records as embedded JSON. Hermes stores nothing.

**Testing**: Contract tests (new contract and releases with fixtures); integration tests in
`src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/`; Host tests (OpenAPI
snapshot); Hermes pytest with the scripted provider and fakes; the Docker-backed `AgentRuntimeStackTests` consultation
case; Web UI Vitest unit tests and Playwright `simulated` and `platform` projects.

**Target Platform**: Linux containers in a stamp (API, MCP server, Hermes); browsers for the Web UI; developer
machines through the AppHost.

**Project Type**: Additive changes to an existing web service, agent runtime, and web client.

**Performance Goals**: A consulting Coach Agent question answered within 120 seconds in the cross-process test
(SC-001); waiting parents use no execution slot of Hermes.

**Constraints**: No nested, `CA -> CA`, `SA -> SA`, or SA-initiated consultation; refusals create nothing; child
responses return only to the parent and become visible to the owner with the parent's outcome; no unrecorded
fallback; no questions, decision support, responses, notes, or advice in logs, audit events, telemetry, or
notifications; development values only.

**Scale/Scope**: One migration, one new artifact contract, one new workload contract, four changed contract releases,
Agent Orchestration request, read, transition, and completion handlers, catalog rules and seed, the simulated agent
workload, Hermes' artifacts, consultation, rendering, execution, and runner modules, the Web UI's triage and
agent-catalog feature folders, and documentation; at most 6 consultations per parent by contract (3 in the seed).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | Implements the consultation parts of [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority), [Durable State And Transaction Boundaries](../../docs/architecture/intelligence-and-agents.md#durable-state-and-transaction-boundaries), [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol), [Authorization, Lineage, And Lifecycle](../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle), [Post-Analysis Match Triage](../../docs/architecture/intelligence-and-agents.md#post-analysis-match-triage), the catalog's [Interaction Model](../../docs/architecture/intelligence-agent-catalog.md#interaction-model), and Wave 4 of [Implementation Waves](../../docs/architecture/intelligence-agent-catalog.md#implementation-waves); decisions the architecture did not state (consultation policy artifact, route value, operations, waiting pool, influence declaration, visibility after the parent ended, seed versions) are applied now as decided but not implemented (see Architecture Updates). The Wave 4 gates stay open and unclaimed. | Pass |
| II. Respect Source-Area Ownership | Platform changes stay in the Agent Orchestration folders (including `Catalog`) of the platform layers and `src/platform/Tests`; contracts in `contracts/agent-catalog/` and `contracts/agent-orchestration/`; Hermes changes in `src/agents/hermes`; Web UI changes in `src/clients/web`. No new first-level area or project. | Pass |
| III. API-First Control Plane | Hermes uses only versioned workload operations and the MCP server; members and the Web UI use `/api/v1` reads; the platform validates JSON Schema contracts and is the only authority for consultations, states, and accepted outcomes. | Pass |
| IV. Evidence Over Claims | Contract, integration, Host, Hermes, image, and Web UI tests cover FR-038 and FR-039; passing tests are not presented as consultation quality, latency, cost, evaluation evidence, or production readiness. | Pass |
| V. Focused, Minimal Changes | Children reuse the invocation, attempt, lineage, and tool machinery; one new table and one new artifact kind; the MCP server and tool operations stay unchanged; no new dependency. | Pass |
| Technology and Tooling | .NET 10, Docker, Node.js 24, and Playwright come from the existing verification; Python projects under `src/agents/` are verified by [Agents Python Verification Environment](../20261018-110526-agents-python-environment/spec.md), a dependency through the Hermes Agent Runtime. No other tool is needed. | Pass |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Architecture Updates

**Narratives** (implemented or changed by this feature):

- [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md):
  [Agent Groups And Interaction](../../docs/architecture/intelligence-and-agents.md#agent-groups-and-interaction),
  [Post-Analysis Match Triage](../../docs/architecture/intelligence-and-agents.md#post-analysis-match-triage),
  [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority)
  (identity model, child invocations, route rejections),
  [Durable State And Transaction Boundaries](../../docs/architecture/intelligence-and-agents.md#durable-state-and-transaction-boundaries)
  (`waiting-for-consultations`, `synthesizing`, Create consultation, Accept child outcome, Complete parent invocation),
  [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)
  (request a delegated consultation, waiting edges, unavailable consultations, versioned orchestration policy),
  [Authorization, Lineage, And Lifecycle](../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle)
  (re-evaluation at delegation, consulted-agent lineage),
  [Supported Interactions](../../docs/architecture/intelligence-and-agents.md#supported-interactions), and
  [Match Debrief And Practice Planning](../../docs/architecture/intelligence-and-agents.md#match-debrief-and-practice-planning).
- [Intelligence Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md): introduction, Catalog Rules,
  [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance)
  (consultation-policy artifact, consulted agents in lineage),
  [Coach Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md#coach-agent-catalog) ("Typical SA
  consultations"), [Rendered CA Prompt Context](../../docs/architecture/intelligence-agent-catalog.md#rendered-ca-prompt-context)
  (seed prompts without and with the consultation instruction),
  [Interaction Model](../../docs/architecture/intelligence-agent-catalog.md#interaction-model),
  [First-Draft System Prompts](../../docs/architecture/intelligence-agent-catalog.md#first-draft-system-prompts)
  (consultation paragraph of the Coach Agent drafts),
  [Implementation Waves](../../docs/architecture/intelligence-agent-catalog.md#implementation-waves) (Wave 0
  consultation test suite, Wave 4), and
  [Validation And Promotion Gates](../../docs/architecture/intelligence-agent-catalog.md#validation-and-promotion-gates)
  (stay open).
- [Platform Implementation Profile](../../docs/architecture/platform-implementation.md): the functional-area list
  (delegated consultations) and [Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime).
- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) (triage views and the agent
  catalog administration paragraph) and
  [Connected Agent Conversations](../../docs/architecture/client-applications.md#connected-agent-conversations).
- [Tenancy and Technology → AI](../../docs/architecture/tenancy-and-technology.md#ai).
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md): `DAT-007` (consultation
  policies are catalog records), `DAT-008` (consultations, child invocations, and influence declarations are advice
  lineage), `THR-007` (delegation evidence), and the audit event list.
- [Production Deployment and Operations](../../docs/architecture/production-operations.md) (Agent Orchestration owns
  consultation state) and [Architecture Overview](../../docs/architecture/overview.md) (current-state summary and the
  diagrams' `Consult` and `delegate` edges).

**Design updates** (applied now to the owning narratives, worded as decided but not yet implemented; the
implementation turns each into present tense):

1. `intelligence-and-agents.md` Agent Orchestration Authority: a paragraph after the direct Coach Agent paragraph on
   delegated consultations (consultation policy pinned with the Coach Agent version, `requestAgentConsultation` and its
   refusals, the `coach-to-specialist` child with inherited context and the Specialist's pins, the pending consultation
   as waiting edge, outcome delivery, unavailable consultations under the pinned policy, parents ending with their
   children, scope loss of the tree, the influence declaration, visibility of responses once the parent ended, Hermes
   waiting outside its execution slots, and the simulated workload).
2. `intelligence-agent-catalog.md` Prompt And Advice Governance: the registry paragraph names the consultation-policy
   artifact a Coach Agent version may reference.
3. `intelligence-agent-catalog.md` Interaction Model: a paragraph on the first consultation implementation (allowed
   Specialists, bound, and unavailable behavior from the pinned policy, the Hermes-local request function, fan-out
   within the bound, the influence declaration with adoption and agreement, and conflicts kept visible).
4. `intelligence-agent-catalog.md` Rendered CA Prompt Context: consulting seed versions restore the drafts'
   consultation paragraph and use a Coach Agent runtime section that permits consulting the allowed Specialists.
5. `intelligence-agent-catalog.md` Implementation Waves, Wave 4: delegated consultations are decided and not yet
   implemented; the gates remain open.
6. `platform-implementation.md` Agent Runtime: a paragraph on the migration (`agent_consultations`, child links,
   route and states, influence on accepted advice), the two workload operations, the contracts, the consultation seed,
   and the simulated workload's consultation behavior.
7. `client-applications.md` Web UI: sentences after the Coach Agent sentence on consultations in the triage views and
   consultation policies in the Agent Catalog area.
8. `tenancy-and-technology.md` AI: the Hermes line names delegated consultations, and the artifact sentence names the
   consultation-policy artifact (decided, not yet implemented).
9. `security-and-data-governance.md`: `DAT-007` names consultation-policy artifacts, and the audit list names
   consultation requests and their resolutions (identifiers and reason codes only; decided, not yet implemented).

**Status statements at merge** (each must become true for the merged state):

- `intelligence-and-agents.md` Agent Orchestration Authority: "… Consultations, Coach Agents, lifecycle operations, and
  tombstones remain later work." (first implementation paragraph, as left by earlier features), "Coach Agents,
  philosophy rendering, and consultations remain later work." (first Hermes slice paragraph), and "Consultations
  remain later work." (direct Coach Agent paragraph) → consultations exist as development evidence; lifecycle
  operations and tombstones stay later work; design update 1 loses "decided and not yet implemented". "The planned
  control-plane API exposes versioned submission, … consultation, completion, and lifecycle operations." → the
  consultation operations exist; lifecycle operations stay planned.
- `intelligence-and-agents.md` direct Coach Agent paragraph: "… applies only evidence-backed modifiers within the
  pinned policy, offers no consultation, …" → Coach Agent versions without a consultation policy offer none;
  consulting versions do.
- `intelligence-and-agents.md` Durable State And Transaction Boundaries, Client, Transport, And Runtime Protocol, and
  Authorization, Lineage, And Lifecycle: "**Create consultation:** …", "**Accept child outcome:** …", "Parent waiting
  edges and accepted child outcomes survive the restart. A child that exhausts its retry budget is recorded as an
  attributed unavailable consultation …", and "re-evaluates current authorization at submission, claim, delegation,
  …" → confirm they describe the implemented behavior; the role-rules paragraph ("… re-evaluates it at claim, lease
  renewal, checkpoint, completion, and failure, and in a periodic check of active attempts …") adds delegation.
- `intelligence-agent-catalog.md` introduction: "The catalog entries, prompt text, philosophy artifacts, model
  execution, MCP evidence tools, Hermes orchestration runtime, and user experiences are likewise design inputs …"
  (as left by earlier features) → consultations execute as development evidence; consultation policies, prompt
  drafts, and model choices stay Provisional; evaluation gates stay open.
- `intelligence-agent-catalog.md` Rendered CA Prompt Context: "… the consultation paragraph replaced by suggestions for
  the human coach until consultations exist." and the runtime section statement "that no other agent may be
  consulted" → the 0.1.0 seed prompts and non-consulting versions keep them; design update 4 loses "decided and not
  yet implemented".
- `intelligence-agent-catalog.md` Implementation Waves: Wave 0 "establish disclosure, grounding, safety, privacy,
  consultation, latency, and cost test suites … the test suites remain open" → stays open; Wave 4 (design update 5)
  → delegated consultations exist as development evidence; the gates remain open.
- `intelligence-agent-catalog.md` Prompt And Advice Governance and Interaction Model: design updates 2 and 3 lose
  "decided and not yet implemented" or "decided, not yet implemented".
- `platform-implementation.md` Agent Runtime: "The planned control-plane surface includes … checkpoint, consultation
  and completion operations …", "Hermes is a planned separate Python OCI deployment … to claim, renew, checkpoint,
  delegate, and complete work.", and "MCP tools, prompt/profile resolution, model execution, and Web UI agent
  workflows remain future implementation work" (as left by earlier features) → delegation exists; design update 6
  loses "decided and not yet implemented"; the simulated workload sentence ("… executes direct specialist invocations
  …", as left by earlier features) covers consultations.
- `client-applications.md` Web UI: "Coach Agents, consultations, streaming, editing, deletion, sharing, and evidence
  clip playback are not part of them" → consultations are part of them; design update 7 loses "decided and not yet
  implemented"; the agent catalog administration paragraph's list of shown artifacts ("… and the modifier policy.")
  names consultation policies.
- `tenancy-and-technology.md` AI: design update 8 loses "decided, not yet implemented".
- `security-and-data-governance.md`: design update 9 loses "decided, not yet implemented"; `DAT-008`, `GOV-OWN-009`,
  and `THR-007` keep their decision status and cover consultations and delegation.
- `overview.md` current-state summary ("… Hermes as a separate Python OCI service are planned.", as left by earlier
  features) and any statement that consultations or delegated `CA -> SA` work are planned → delegated consultations
  exist as development evidence; the diagrams' `Consult` and `delegate` edges stay.
- `production-operations.md`: "Agent Orchestration owns conversation, logical invocation, consultation, …" → confirm.
- Outside `docs/architecture/` (kept current by the same documentation task): `README.md` and `AGENTS.md` "Current
  State" name delegated consultations as development evidence and keep evaluation suites, lifecycle operations, and
  production readiness among what does not exist; `src/agents/README.md` and the Hermes README describe consultation
  execution.

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the listed
narratives and every status statement this feature affects in `docs/architecture/` true for the merged state,
re-reading the documents at implementation time (including `README.md`, `overview.md`,
`terminology-and-principles.md`, `security-and-data-governance.md`, `production-operations.md`, and any other
planned, future-tense, or "not implemented" wording about consultations, delegation, `CA -> SA`, child invocations,
`waiting-for-consultations`, `synthesizing`, consulted-agent lineage, or the Coach Agents' consultation instruction);
the rewrites listed above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261018-121958-agent-consultations/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── consultation-interfaces.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/agent-catalog/
├── consultation-policy/v1/                    # new: schema, releases/1.0.0, fixtures
└── agent-version/v1/                          # 1.1.0: optional consultationPolicy; releases/1.1.0; fixtures
contracts/agent-orchestration/
├── consultation-request/v1/                   # new: schema, releases/1.0.0, fixtures
├── work-notification/v1/                      # 1.2.0: route coach-to-specialist; releases/1.2.0; fixtures
├── execution-envelope/v1/                     # 1.3.0: route, kind, consultations, consultation; releases/1.3.0; fixtures
└── attempt-report/v1/                         # 1.3.0: consultationInfluence; releases/1.3.0; fixtures
contracts/README.md                            # index rows name the new versions

src/platform/
├── SocAlytics.Platform.Domain/AgentOrchestration/
│   ├── AgentRoute.cs                          # coach-to-specialist
│   ├── Consultations/                         # new: consultation states, request rules, influence rules
│   └── Catalog/AgentCatalogRules.cs           # consultation-policy kind and its rules
├── SocAlytics.Platform.Application/AgentOrchestration/
│   ├── Catalog/                               # kind registration, pins, seed contracts
│   ├── Consultations/                         # new: RequestAgentConsultation, ReadAgentConsultationOutcomes, ConsultationOutcomeDelivery
│   ├── Invocations/SubmitAgentInvocation.cs   # consultation-policy pin, consulting-invocation/1, unfinished count
│   ├── Invocations/                           # reads: consultations, child reads, states
│   ├── Execution/                             # claim (synthesizing, envelope 1.3.0), completion (influence), failure, expiry, scope loss with children
│   ├── AgentOrchestrationOptions.cs           # Execution:ConsultingParent, Execution:Consultation
│   └── AgentOrchestrationAuditEvents.cs       # consultation events
├── SocAlytics.Platform.Application/Telemetry/AgentOrchestrationMetrics.cs   # consultations counter, waiting gauge
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Migrations/NNNN_agentorchestration_consultations.sql     # numbered at implementation time
│   ├── Persistence/AgentOrchestration/        # consultation store, invocation and read queries
│   └── AgentOrchestration/Catalog/
│       ├── AgentCatalogSeeder.cs              # consultation step
│       └── Seed/consultations/*.json          # policies, safety policies, prompts, agent versions
├── SocAlytics.Platform.Api/
│   ├── AgentOrchestration/AgentWorkloadEndpoints.cs   # two workload operations
│   ├── AgentOrchestration/                    # schemas of changed member reads and catalog documents
│   ├── appsettings.Development.json           # execution policy values
│   └── openapi/v1.json                        # refreshed snapshot
└── Tests/
    ├── SocAlytics.Platform.SimulatedAgentWorkload/                    # children, [simulate:consult:…]
    ├── SocAlytics.Platform.Contracts.Tests/                           # new contract and releases
    ├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/   # new tests
    ├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/        # consultation-policy rules, seed
    ├── SocAlytics.Platform.Host.Tests/                                # OpenAPI snapshot
    └── SocAlytics.Platform.Images.Tests/AgentRuntimeStackTests.cs     # consultation case and scripts

src/agents/hermes/
├── src/socalytics_hermes/
│   ├── contracts/                             # added copies (R10)
│   ├── artifacts.py                           # consultation-policy kind, coach-to-specialist validation
│   ├── consultations.py                       # new: request and influence functions, local refusals, outcome reads
│   ├── rendering.py                           # hermes-coach-section/2, hermes-consultation-section/1, earlier consultations
│   ├── advice.py                              # influence declaration check
│   ├── execution.py                           # route dispatch, waiting, checkpoints, completion
│   ├── runner.py                              # waiting pool
│   ├── configuration.py                       # MAX_WAITING_ATTEMPTS, CONSULTATION_POLL_SECONDS
│   ├── platform/client.py                     # two workload operations on the path allowlist
│   └── logging.py                             # consultationId, consultationOutcome
├── tests/
│   ├── fakes/                                 # consultation operations, outcomes, child envelopes
│   ├── scripts/                               # consultation scenarios
│   ├── test_consultation_artifacts.py
│   ├── test_consultation_function.py
│   ├── test_consultation_waiting.py
│   ├── test_consultation_child.py
│   └── test_consultation_influence.py
└── README.md                                  # consultation execution and configuration

src/clients/web/
├── src/platform/models/agents.ts              # states, Consultation, child references
├── src/platform/models/agentCatalog.ts        # consultation-policy kind and reference
├── src/platform/adapters/agentConversationAdapter.ts
├── src/platform/adapters/agentCatalogAdapter.ts
├── src/i18n/en/triage.json, agentCatalog.json
├── src/features/triage/
│   ├── turnPresentation.ts                    # waiting-for-consultations, synthesizing
│   ├── ConsultationList.tsx                   # new
│   ├── AdviceCard.tsx                         # renders ConsultationList
│   └── LineagePanel.tsx                       # consultation kinds, child lineage
├── src/features/agent-catalog/
│   ├── catalogLimits.ts, outcomeText.ts
│   ├── artifacts/ConsultationPolicyView.tsx, ConsultationPolicyForm.tsx   # new
│   ├── artifacts/SafetyPolicyView.tsx         # coach-to-specialist wording
│   ├── ArtifactPage.tsx, ArtifactCreatePage.tsx, ArtifactsPage.tsx
│   └── AgentVersionPage.tsx, AgentVersionCreatePage.tsx
└── tests/
    ├── unit/                                  # adapters, triage and agent-catalog views
    └── e2e/
        ├── support/simulatedApi.ts            # consultations, states, consultation policies
        ├── simulated/triage-consultations.spec.ts         # new
        ├── simulated/agent-catalog-consultations.spec.ts  # new
        └── platform/agent-catalog*.spec.ts    # seeded consultation policies

docs/architecture/                             # documentation task
README.md, AGENTS.md, src/agents/README.md     # current state
```

**Structure Decision**: Consultations are a `Consultations` sub-folder of the Agent Orchestration functional area in
the Domain and Application layers, next to `Invocations`, `Execution`, `Catalog`, and `Tools`; their persistence and
seed live in the existing Infrastructure folders. Child invocations reuse every existing invocation component. Hermes
adds one module for the consultation functions and extends the existing ones; the Web UI extends its `triage` and
`agent-catalog` feature folders. The OpenAPI snapshot is refreshed with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
during implementation, never in the verification environment, and the Web UI's Kiota client is regenerated from it.
This feature is implemented after Coach Agent Direct Conversations, the Hermes Agent Runtime, the Agent Catalog
Registry, the Web UI Agent Catalog Administration, the Agent Evidence Tools API, the MCP Server, Agent Orchestration
Direct Invocations, and Web UI Match Triage Conversations have merged.

## Complexity Tracking

No violations.
