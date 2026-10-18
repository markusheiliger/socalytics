# Implementation Plan: Coach Agent Direct Conversations

**Branch**: `20261018-115720-coach-agent-conversations` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261018-115720-coach-agent-conversations/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Direct `User -> CA` conversations work end to end. The platform enables approved Coach Agent versions like Specialist
Agent versions, lists them with their simulation disclosure, accepts them as participants, and gives their invocations
the route `user-to-coach` (`work-notification` 1.1.0, `execution-envelope` 1.2.0) with an additional pin of the
profile's dimension set. Hermes validates the pinned dimension set, base profile, and modifier policy (no fallback),
renders the versioned profile block `hermes-profile-block/1` into the prompt's `philosophy_profile` slot and a Coach
Agent runtime section, lets the model propose one set of context modifiers through a Hermes-local function, applies it
only when it satisfies the pinned policy and references answered tool calls of the attempt, and records the applied
profile in `profile-rendered` checkpoints and the completion (`attempt-report` 1.2.0). Agent Orchestration re-checks
the applied profile against the pins and the attempt's tool calls, retains it in lineage and accepted advice, and
attaches the pinned disclosure statement to every Coach Agent advice in its reads. The simulated agent workload
executes Coach Agent invocations, and a Development and Test seed registers, approves, and enables the three
first-draft Coach Agents. The Web UI shows Coach Agents as a separate participant group with their disclosure, the
disclosure on every Coach Agent advice, the applied emphasis on request, and enables Coach Agent versions without the
former warning. See [research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (platform, simulated agent workload, tests); Python 3.12 (Hermes; CPython 3.12.15
in verification); TypeScript with React (Web UI, Node.js 24).

**Primary Dependencies**: Existing only: the platform's Agent Orchestration and Catalog code, Dapper, DbUp,
JsonSchema validation, and Testcontainers; Hermes' locked dependencies (`agent-framework-core`, `mcp`, `jsonschema`,
`rfc8785`); the Web UI's React, MUI, TanStack Query, the Kiota client, Vitest, and Playwright. No new dependency.

**Storage**: PostgreSQL stamp database: one forward-only migration `NNNN_agentorchestration_coach_agents.sql`
(route check, `agent_accepted_advice.applied_philosophy_profile jsonb`); seed records as embedded JSON. Hermes stores
nothing.

**Testing**: Contract tests (new releases and fixtures); integration tests in
`src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/`; Host tests (OpenAPI
snapshot); Hermes pytest with the scripted provider and fakes; the Docker-backed `AgentRuntimeStackTests` Coach Agent
case; Web UI Vitest unit tests and Playwright `simulated` and `platform` projects.

**Target Platform**: Linux containers in a stamp (API, MCP server, Hermes); browsers for the Web UI; developer
machines through the AppHost.

**Project Type**: Additive changes to an existing web service, agent runtime, and web client.

**Performance Goals**: A Coach Agent question answered within 60 seconds in the cross-process test (SC-001); validation
and rendering add no provider calls.

**Constraints**: Profile numbers come only from the pinned profile; no fallback; modifiers bounded, budget-neutral,
evidence-backed; no weights, reasons, prompts, questions, or advice in logs, audit events, or telemetry; no
consultations; development values only.

**Scale/Scope**: One migration, three contract releases, changes in Agent Orchestration and Catalog handlers and reads,
the simulated agent workload, the seed, Hermes' artifacts, rendering, modifiers, and execution modules, two Web UI
feature folders, and documentation.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | Implements the Coach Agent parts of [Coach Philosophy Weighting](../../docs/architecture/intelligence-agent-catalog.md#coach-philosophy-weighting) (Context Modifiers, Rendered CA Prompt Context), [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance), Wave 3 of [Implementation Waves](../../docs/architecture/intelligence-agent-catalog.md#implementation-waves), and the direct `User -> CA` route of [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority); decisions the architecture did not state (route value, dimension-set pin, modifier proposal function, profile block version, profile checkpoints, `attempt-report` 1.2.0, platform-attached disclosure, seed prompts with the slot) are applied now as decided but not implemented (see Architecture Updates). Evaluation gates stay open and unclaimed. | Pass |
| II. Respect Source-Area Ownership | Platform changes stay in the Agent Orchestration folders of the platform layers and `src/platform/Tests`; contracts in `contracts/agent-orchestration/`; Hermes changes in `src/agents/hermes`; Web UI changes in `src/clients/web`. No new first-level area or project. | Pass |
| III. API-First Control Plane | Members and the Web UI use `/api/v1` operations; Hermes uses only workload operations and the MCP server; the platform validates against JSON Schema contracts and remains authoritative for pins and applied profiles. | Pass |
| IV. Evidence Over Claims | Contract, integration, Host, Hermes, image, and Web UI tests cover FR-029 and FR-030; passing tests are not presented as model quality, behavioral fidelity, evaluation evidence, or production readiness. | Pass |
| V. Focused, Minimal Changes | Additive releases, one migration with one column, no new dependency; reuse of checkpoints instead of a new workload operation; the MCP server and tool operations stay unchanged. | Pass |
| Technology and Tooling | .NET 10, Docker, Node.js 24, and Playwright come from the existing verification; Python projects under `src/agents/` are verified by [Agents Python Verification Environment](../20261018-110526-agents-python-environment/spec.md), a dependency through the Hermes Agent Runtime. No other tool is needed. | Pass |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Architecture Updates

**Narratives** (implemented or changed by this feature):

- [Intelligence Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md): introduction, Catalog Rules,
  [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance)
  (Coach Agent enablement, pins, disclosure, lineage of applied modifiers),
  [Coach Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md#coach-agent-catalog),
  [Context Modifiers](../../docs/architecture/intelligence-agent-catalog.md#context-modifiers),
  [Rendered CA Prompt Context](../../docs/architecture/intelligence-agent-catalog.md#rendered-ca-prompt-context),
  [First-Draft System Prompts](../../docs/architecture/intelligence-agent-catalog.md#first-draft-system-prompts) (seed
  prompts derived from the Coach Agent drafts), [Implementation Waves](../../docs/architecture/intelligence-agent-catalog.md#implementation-waves)
  (Wave 3), and [Validation And Promotion Gates](../../docs/architecture/intelligence-agent-catalog.md#validation-and-promotion-gates)
  (stay open).
- [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md):
  [Agent Groups And Interaction](../../docs/architecture/intelligence-and-agents.md#agent-groups-and-interaction),
  [Post-Analysis Match Triage](../../docs/architecture/intelligence-and-agents.md#post-analysis-match-triage),
  [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority),
  [Durable State And Transaction Boundaries](../../docs/architecture/intelligence-and-agents.md#durable-state-and-transaction-boundaries)
  (submit `User -> CA`), [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)
  (Hermes profile rendering), [Authorization, Lineage, And Lifecycle](../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle)
  (applied modifier snapshots), and [Supported Interactions](../../docs/architecture/intelligence-and-agents.md#supported-interactions).
- [Architectural Principles](../../docs/architecture/terminology-and-principles.md) (Coach Agent, philosophy dimension
  set, philosophy weight, base and applied philosophy profiles): terms used unchanged.
- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) and
  [Connected Agent Conversations](../../docs/architecture/client-applications.md#connected-agent-conversations).
- [Platform Implementation Profile → Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime).
- [Tenancy and Technology → AI](../../docs/architecture/tenancy-and-technology.md#ai).
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md): `DAT-007` (Coach Agent
  catalog records), `DAT-008` (applied profile snapshots and modifier reasons are advice lineage), and the audit event
  list (route detail, no weights or reasons).
- [Architecture Overview](../../docs/architecture/overview.md) (current-state summary).

**Design updates** (applied now to the owning narratives, worded as decided but not yet implemented; the
implementation turns each into present tense):

1. `intelligence-agent-catalog.md` Prompt And Advice Governance: the enablement sentence names direct Coach Agent
   conversations, which enable Coach Agent versions like Specialist Agent versions and pin the profile's dimension set.
2. `intelligence-agent-catalog.md` Context Modifiers: a paragraph on how modifiers arise (one proposal through a
   Hermes-local function, the evidence rule, omission of invalid sets, the platform's re-check).
3. `intelligence-agent-catalog.md` Rendered CA Prompt Context: a paragraph on `hermes-profile-block/1`, the Coach Agent
   runtime section, profile checkpoints and `attempt-report` 1.2.0, retention, the platform-attached disclosure, and
   the seed prompts with the slot in place of the weight table and without the consultation instruction.
4. `intelligence-agent-catalog.md` Implementation Waves, Wave 3: direct Coach Agent conversations are decided and not
   yet implemented; the evaluation suites remain open.
5. `intelligence-and-agents.md` Agent Orchestration Authority: a paragraph after the first Hermes slice on direct
   Coach Agent conversations (enablement, `user-to-coach` and the contract releases, dimension-set pin, validation,
   rendering, modifiers, no consultation, profile checkpoints and completion checks, simulated workload).
6. `platform-implementation.md` Agent Runtime: a paragraph on the migration, `attempt-report` 1.2.0 checks, the Coach
   Agent seed, and Coach Agent execution by the simulated workload and Hermes.
7. `client-applications.md` Web UI: a sentence after the triage conversations paragraph on Coach Agents in the triage
   views (participant group, disclosure, applied emphasis on request) and Coach Agent enablement in the Agent Catalog
   area.
8. `tenancy-and-technology.md` AI: the Hermes line names direct Coach Agents, and the artifact sentence names the Coach
   Agent's dimension-set, philosophy-profile, and modifier-policy artifacts (decided, not yet implemented).

**Status statements at merge** (each must become true for the merged state):

- `intelligence-agent-catalog.md` Prompt And Advice Governance: "… a stamp enables at most one approved version per
  agent identifier, Coach Agents only once their conversations are implemented (direct Coach Agent conversations …
  are decided and not yet implemented …)" → Coach Agent versions are enabled like Specialist Agent versions; design
  update 1 loses "decided and not yet implemented".
- `intelligence-agent-catalog.md` introduction: "The catalog entries, prompt text, philosophy artifacts, model
  execution, MCP evidence tools, Hermes orchestration runtime, and user experiences are likewise design inputs …" (as
  left by earlier features) → philosophy artifacts are registered, rendered, and executed for direct Coach Agents as
  development evidence; profile values, prompt drafts, and model choices stay Provisional; evaluation gates stay open.
- `intelligence-agent-catalog.md` Catalog Rules: "Development and Test may enable approved versions without them" →
  confirm it holds for Coach Agent versions and that production promotion evidence stays required and unenforced.
- `intelligence-agent-catalog.md` Base Philosophy Profiles: "Their eventual artifact digests are assigned at promotion
  rather than invented in this architecture document." → confirm; the Development and Test seed's digests are not
  promotion digests.
- `intelligence-agent-catalog.md` Context Modifiers and Rendered CA Prompt Context: design updates 2 and 3 lose
  "decided and not yet implemented".
- `intelligence-agent-catalog.md` Implementation Waves, Wave 3: design update 4 → direct Coach Agent conversations
  exist as development evidence; the evaluation suites remain open.
- `intelligence-and-agents.md` Agent Orchestration Authority: "… Consultations, Coach Agents, lifecycle operations,
  and tombstones remain later work." (first implementation paragraph) and "Coach Agents, philosophy rendering, and
  consultations remain later work." (first Hermes slice paragraph) → Coach Agents and philosophy rendering exist;
  consultations, lifecycle operations, and tombstones stay later work; design update 5 loses "decided and not yet
  implemented".
- `intelligence-and-agents.md` Client, Transport, And Runtime Protocol: "For a CA, Hermes validates the profile and
  renders its complete base vector, modifier policy, and any evidence-backed applied modifiers …" → confirm it
  describes the implemented runtime.
- `platform-implementation.md` Agent Runtime: "MCP tools, prompt/profile resolution, model execution, and Web UI agent
  workflows remain future implementation work" (as left by earlier features) → profile resolution and rendering exist
  for direct Coach Agents; the catalog paragraph's seed sentence ("… a seed registers and enables
  `specialist-tactics` and `specialist-video-analysis` …") also names the three Coach Agents; the simulated workload
  sentence ("… executes direct specialist invocations …") covers Coach Agent invocations; design update 6 loses
  "decided and not yet implemented".
- `client-applications.md` Web UI: "Coach Agents, consultations, streaming, editing, deletion, sharing, and evidence
  clip playback are not part of them" → Coach Agents are part of them; design update 7 loses "decided and not yet
  implemented"; the agent catalog administration paragraph stays true. Connected Agent Conversations: "A valid direct
  Coach Agent or Specialist Agent submission …" → confirm it holds for both groups.
- `tenancy-and-technology.md` AI: design update 8 loses "decided, not yet implemented".
- `overview.md` current-state summary: any statement that Coach Agents or philosophy profiles are planned (as left by
  earlier features) → direct Coach Agents exist as development evidence; consultations stay planned.
- `security-and-data-governance.md`: `DAT-007` and `DAT-008` keep their decision status; confirm that applied profile
  snapshots and modifier reasons are covered as advice lineage and that the audit list's "identifiers only" wording
  covers the route detail.
- Outside `docs/architecture/` (kept current by the same documentation task): `README.md` and `AGENTS.md` "Current
  State" name direct Coach Agent conversations as development evidence and keep consultations, evaluation suites, and
  production readiness among what does not exist; `src/agents/README.md` and the Hermes README describe Coach Agent
  execution.

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the listed
narratives and every status statement this feature affects in `docs/architecture/` true for the merged state,
re-reading the documents at implementation time (including `README.md`, `overview.md`,
`terminology-and-principles.md`, `security-and-data-governance.md`, and any other planned, future-tense, or "not
implemented" wording about Coach Agents, philosophy profiles, context modifiers, profile rendering, the `User -> CA`
route, or the agent seed); the rewrites listed above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261018-115720-coach-agent-conversations/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── attempt-report-1.2.0.md
│   └── coach-agent-interfaces.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/agent-orchestration/
├── work-notification/v1/                     # 1.1.0: route enum; releases/1.1.0; fixtures
├── execution-envelope/v1/                    # 1.2.0: route enum, dimension-set pin kind; releases/1.2.0; fixtures
└── attempt-report/v1/                        # 1.2.0: appliedPhilosophyProfile; releases/1.2.0; fixtures
contracts/README.md                           # index rows name the new versions

src/platform/
├── SocAlytics.Platform.Domain/AgentOrchestration/
│   ├── AgentRoute.cs                         # user-to-coach (wire names)
│   └── Catalog/AppliedPhilosophyProfileRules.cs   # new: snapshot validation and violation codes
├── SocAlytics.Platform.Application/AgentOrchestration/
│   ├── Catalog/SetAgentEnablement.cs         # Coach Agent versions enableable
│   ├── Conversations/                        # participants of both groups; listAvailableAgents disclosure
│   ├── Invocations/SubmitAgentInvocation.cs  # route by group; dimension-set pin
│   ├── Invocations/                          # reads: disclosure, applied profile, dimension-set pin
│   ├── Execution/RecordAgentCheckpoint.cs    # profile-rendered validation
│   ├── Execution/CompleteAgentAttempt.cs     # applied profile validation and storage
│   ├── Execution/ReadPinnedAgentArtifacts.cs # dimension set from the pin
│   └── AgentOrchestrationAuditEvents.cs      # route detail; refused applied profile
├── SocAlytics.Platform.Application/Telemetry/AgentOrchestrationMetrics.cs   # route tag
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Migrations/NNNN_agentorchestration_coach_agents.sql      # numbered at implementation time
│   ├── Persistence/AgentOrchestration/       # stores: applied profile column, tool-call outcome lookup, reads
│   └── AgentOrchestration/Catalog/
│       ├── AgentCatalogSeeder.cs             # Coach Agent step
│       └── Seed/coach/*.json                 # dimension set, profiles, modifier policy, prompts, policies, versions
├── SocAlytics.Platform.Api/
│   ├── AgentOrchestration/                   # schemas of changed member reads and workload requests
│   └── openapi/v1.json                       # refreshed snapshot
└── Tests/
    ├── SocAlytics.Platform.SimulatedAgentWorkload/                  # user-to-coach execution
    ├── SocAlytics.Platform.Contracts.Tests/                         # new releases and fixtures
    ├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/   # new tests
    ├── SocAlytics.Platform.Host.Tests/                              # OpenAPI snapshot
    └── SocAlytics.Platform.Images.Tests/AgentRuntimeStackTests.cs   # Coach Agent case and script

src/agents/hermes/
├── src/socalytics_hermes/
│   ├── contracts/                            # added copies (R4)
│   ├── artifacts.py                          # Coach Agent kinds and validation
│   ├── philosophy.py                         # new: applied profile, modifier rules
│   ├── rendering.py                          # profile block, Coach Agent section, re-render
│   ├── evidence.py                           # record callId and outcome of tool results
│   ├── execution.py                          # route dispatch, propose_context_modifiers, profile checkpoints, completion
│   └── logging.py                            # route, modifierOutcome fields
├── tests/
│   ├── fakes/                                # Coach Agent pins, artifacts, tool outcomes
│   ├── scripts/                              # Coach Agent scenarios
│   ├── test_coach_artifacts.py
│   ├── test_profile_block.py
│   ├── test_modifiers.py
│   └── test_coach_execution.py
└── README.md                                 # Coach Agent execution

src/clients/web/
├── src/platform/models/agents.ts             # group, disclosure, applied profile
├── src/platform/adapters/agentConversationAdapter.ts
├── src/platform/adapters/agentCatalogAdapter.ts
├── src/i18n/en/triage.json, agentCatalog.json
├── src/features/triage/
│   ├── ParticipantsPanel.tsx                 # Coach and Specialist groups with disclosure
│   ├── QuestionComposer.tsx                  # Coach Agent label
│   ├── AdviceCard.tsx                        # disclosure statement
│   ├── PhilosophyEmphasisPanel.tsx           # new
│   └── LineagePanel.tsx                      # philosophy pins and profile checkpoints
├── src/features/agent-catalog/
│   ├── ChangeEnablementDialog.tsx            # no Coach Agent warning
│   ├── offer.ts                              # no "not available in conversations yet"
│   └── outcomeText.ts                        # no coach-agent-enablement-unsupported message
└── tests/
    ├── unit/                                 # adapters, triage and agent-catalog views
    └── e2e/
        ├── support/simulatedApi.ts           # Coach Agents, disclosure, applied profile
        ├── simulated/triage-coach.spec.ts    # new
        ├── simulated/agent-catalog*.spec.ts  # Coach Agent enablement without warning
        └── platform/agent-catalog*.spec.ts   # seeded Coach Agents enabled and offered

docs/architecture/                            # documentation task
README.md, AGENTS.md, src/agents/README.md    # current state
```

**Structure Decision**: Every change lands in the folders that own the behavior today (after the dependent features
merge): Agent Orchestration and its `Catalog` folder in each platform layer, the simulated workload and test projects
under `src/platform/Tests`, the Hermes project, and the Web UI's `triage` and `agent-catalog` feature folders. The
validation rules of the applied profile live in the Domain so Application handlers stay thin and testable. The OpenAPI
snapshot is refreshed with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
during implementation, never in the verification environment, and the Web UI's Kiota client is regenerated from it.
This feature is implemented after the Hermes Agent Runtime, the Agent Catalog Registry, the Web UI Agent Catalog
Administration, the Agent Evidence Tools API, the MCP Server, Agent Orchestration Direct Invocations, and Web UI Match
Triage Conversations have merged.

## Complexity Tracking

No violations.
