# Implementation Plan: Agent Orchestration Direct Invocations

**Branch**: `20261017-204332-agent-orchestration-invocations` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261017-204332-agent-orchestration-invocations/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

The platform gains the Agent Orchestration functional area for direct `User -> SA` triage conversations. Club Admins
and Coaches of a team create a conversation bound to a match and one of its `Completed` or `PartiallyCompleted` runs,
add enabled Specialist Agents as participants, and submit questions with an idempotency key; submission commits the
invocation (`accepted/queued`) with its versioned execution policy and an `agent-invocation-ready` outbox message in
one unit of work and answers `202`. The owner, and only the owner, reads the conversation, turns, invocation status,
advice, and lineage with conditional polling. A configured workload client authenticates with `private_key_jwt` and
DPoP-bound tokens, pulls notifications from a JetStream work queue, claims invocations (attempt, lease, opaque fencing
token, bounded envelope), renews, checkpoints, and completes or fails attempts with idempotent operation ids; stale
calls get explicit dispositions. A monitor expires leases and deadlines, re-evaluates the owner's scope, and requeues,
fails, or fences; reconciliation republishes missing notifications. A simulated agent workload under
`src/platform/Tests` runs only in Development and Test, is started by the AppHost, and proves the protocol end to end.
See [research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: ASP.NET Core minimal APIs and authentication handlers, Dapper and Npgsql, DbUp, NATS.Net and
NATS.Client.JetStream, NATS.Jwt and NATS.NKeys (queue credentials), Microsoft.IdentityModel.JsonWebTokens (assertions
and DPoP proofs), Microsoft.Extensions.Hosting (workers and the simulated workload), JsonSchema.Net (contract tests),
Aspire hosting. All are already pinned in `src/platform/Directory.Packages.props`; no new dependency.

**Storage**: PostgreSQL through the persistence foundation: new tables `agent_conversations` (versioned),
`agent_conversation_participants` (aggregate child), `agent_invocations` (versioned), `agent_attempts` (versioned),
`agent_lineage_entries` and `agent_accepted_advice` (immutable), `agent_workload_operations` (immutable), and
`agent_workload_access_tokens` (unversioned); the audit actor-kind check gains `agent-workload`; one forward-only
migration (see [data-model.md](data-model.md)). Existing `outbox_messages` carries notifications.

**Testing**: xUnit v3 integration tests with Docker-backed PostgreSQL and NATS (`SocAlytics.Platform.Integration.Tests`),
contract tests (`SocAlytics.Platform.Contracts.Tests`), Host tests (`OpenApiSnapshotTests`), architecture tests.

**Target Platform**: The API host on Linux containers and developer machines through the Aspire AppHost; the simulated
agent workload only in Development and Test.

**Project Type**: Web service (platform monolith) plus a Development and Test worker executable.

**Performance Goals**: Submission answers within normal API latency; with the simulated workload, advice is readable
within 30 seconds of submission (SC-001); an abandoned attempt is retried or failed within one lease plus one monitor
interval (SC-004).

**Constraints**: PostgreSQL is the only authority; notifications carry identifiers only; question, advice, and
checkpoint text never in transport, logs, telemetry, or audit; only the current attempt and token mutate; production
values stay open.

**Scale/Scope**: One club per stamp; tens of conversations per match; up to the configured unfinished invocations per
owner; nine member operations, seven workload operations, three machine-readable contracts, two workers, one Development
and Test executable.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | The feature implements [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority), [Durable State And Transaction Boundaries](../../docs/architecture/intelligence-and-agents.md#durable-state-and-transaction-boundaries), [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol), [Authorization, Lineage, And Lifecycle](../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle), and [Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime) for direct specialists; decisions not yet stated there (workload identity, role rules, execution policy, simulated workload) are applied now as decided but not implemented (see Architecture Updates). | Pass |
| II. Respect Source-Area Ownership | Production code lives in the existing platform layers; the simulated workload is platform-owned validation tooling under `src/platform/Tests`; `src/agents/` stays reserved for Hermes; no new first-level area. | Pass |
| III. API-First Control Plane | Members and workloads use only `/api/v1` operations described in the OpenAPI document; no runtime accesses the database; three JSON Schema 2020-12 contracts with golden fixtures are authoritative for the workload protocol. | Pass |
| IV. Evidence Over Claims | Integration, contract, Host, and architecture tests cover every requirement (FR-037); the simulated workload is development evidence only and is not presented as an agent runtime or production readiness. | Pass |
| V. Focused, Minimal Changes | Reuses the outbox, publisher, NATS topology, queue-credential issuer, proof validator, authorizer, audit trail, worker loop, and conditional-read pattern; one new Development and Test executable justified by FR-031 to FR-033; no new dependency. | Pass |
| Technology and Tooling | Needs only .NET 10 and Docker (PostgreSQL, NATS), which `.github/actions/environment-setup` and `environment-verify` provide; the platform verification scope covers `src/platform/` (including the new project under `Tests`) and `contracts/`; no environment spec is needed. | Pass |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Architecture Updates

**Narratives** (implemented or changed by this feature):

- [Intelligence and Agents → Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority):
  the functional area, its operations, and the first implementation slice.
- [Durable State And Transaction Boundaries](../../docs/architecture/intelligence-and-agents.md#durable-state-and-transaction-boundaries):
  invocation and attempt states and the submit, claim, checkpoint, complete, and expire-or-fail transitions for
  direct invocations.
- [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol):
  `202` submission, conditional polling, outbox notifications, the workload identity, claims, leases, fencing, stale
  dispositions, retries, and the execution policy.
- [Authorization, Lineage, And Lifecycle](../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle):
  private reads, role rules, scope-loss re-evaluation and fencing, lineage, and supersession of the bound run.
- [Post-Analysis Match Triage](../../docs/architecture/intelligence-and-agents.md#post-analysis-match-triage): the
  conversation bound to one match and one terminal successful run.
- [Intelligence Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md) introduction and
  [Implementation Waves](../../docs/architecture/intelligence-agent-catalog.md#implementation-waves): the platform side
  of direct specialist conversations.
- [Platform Implementation Profile → Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime)
  and [API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity): the implementation
  baseline and the workload token endpoint.
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md):
  [Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events), `GOV-CRED-003`, and `DAT-008`
  development evidence.

**Design updates** (applied now to the owning narratives, worded as decided but not yet implemented; the
implementation turns each into present tense)

1. `intelligence-and-agents.md` Agent Orchestration Authority: a paragraph that the first implementation (direct
   `User -> SA` conversations for enabled specialists, executed in Development and Test by a simulated agent workload)
   is decided and not yet implemented, with consultations, Coach Agents, lifecycle operations, and tombstones later.
2. `intelligence-and-agents.md` Client, Transport, And Runtime Protocol: a paragraph on the configured workload
   clients with `private_key_jwt` and DPoP-bound tokens from a minimal Agent Orchestration token endpoint, scoped
   queue credentials, the stored execution policy `direct-invocation/1`, the per-owner unfinished-invocation limit, and
   operation-id records.
3. `intelligence-and-agents.md` Authorization, Lineage, And Lifecycle: a paragraph on the role rules (Club Admin and
   Coach create and submit; owner reads with any team role; execution authority re-evaluated at every workload
   operation and in a periodic check; scope-lost invocations never retried; supersession of the run is not scope loss
   and the read shows that the run is no longer current).
4. `platform-implementation.md` Agent Runtime: a paragraph on the first slice (layer folders, tables, outbox
   notifications on a JetStream work queue, monitor and reconciliation workers, workload token endpoint, and the
   simulated agent workload under `src/platform/Tests` started by the AppHost).
5. `platform-implementation.md` API And Identity: agent workload clients use a second minimal token endpoint in the
   Agent Orchestration functional area.
6. `security-and-data-governance.md` Audit Events lists agent conversation, submission, claim, completion, failure,
   expiry, and scope-loss events (identifiers only); `GOV-CRED-003` notes configured agent workload clients with
   DPoP-bound tokens as decided development scope with production values unresolved.

**Status statements at merge** (each must become true for the merged state)

- `intelligence-and-agents.md` Agent Orchestration Authority: "The planned control-plane API exposes …" and "Agent
  Orchestration will use PostgreSQL, …" → the API exposes submission, private query, claim, lease, checkpoint, and
  completion operations for direct invocations (development evidence), with consultation and lifecycle operations
  still planned; the paragraph added now loses "decided and not yet implemented".
- `intelligence-and-agents.md` Client, Transport, And Runtime Protocol and Authorization, Lineage, And Lifecycle: the
  paragraphs added now lose "decided and not yet implemented".
- `intelligence-agent-catalog.md` introduction: "The durable Agent Orchestration authority and canonical control-plane
  API are planned but not implemented" → the authority and API exist for direct specialist invocations as development
  evidence; catalog entries, prompts, philosophy artifacts, model execution, MCP tools, Hermes, and user experiences
  remain design inputs.
- `platform-implementation.md` Agent Runtime: "Agent Orchestration will use the same Dapper, …" and "The planned
  control-plane surface includes …" → present tense for what exists, consultation, lifecycle suppression, and migration
  readiness still planned; the paragraph added now loses "decided and not yet implemented"; "Hermes is a planned
  separate Python OCI deployment" stays true.
- `platform-implementation.md` API And Identity: the workload token endpoint sentence loses "will" and "decided, not
  yet implemented".
- `security-and-data-governance.md` Audit Events and `GOV-CRED-003`: the agent wording loses "decided, not yet
  implemented" and records development evidence.
- `client-applications.md` "(analysis review and agent conversations are not implemented yet)" stays true for the Web
  UI (the batch's Web UI Match Triage Conversations feature changes it); confirm at merge.
- `AGENTS.md` "Current State" (outside `docs/architecture/`, kept current by the same documentation task): names the
  Agent Orchestration direct-invocation evidence and the simulated agent workload, and removes agents from what does
  not exist where it is listed.

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the listed
narratives and every status statement this feature affects in `docs/architecture/` true for the merged state,
re-reading the documents at implementation time (including `README.md`, `overview.md`, and any other planned,
future-tense, or "not implemented" wording about Agent Orchestration); the rewrites listed above are examples, not the
whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261017-204332-agent-orchestration-invocations/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── agent-orchestration-api.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/agent-orchestration/
├── work-notification/v1/          # schema, releases/1.0.0, valid and invalid examples
├── execution-envelope/v1/
└── attempt-report/v1/
contracts/README.md                # index rows

src/platform/
├── SocAlytics.Platform.Domain/AgentOrchestration/        # states, transitions, execution policy, fencing, agent ids
├── SocAlytics.Platform.Application/
│   ├── AgentOrchestration/                               # options, store interfaces, scope evaluator, audit events
│   ├── AgentOrchestration/Conversations/                 # create, add participant, list agents, reads
│   ├── AgentOrchestration/Invocations/                   # submit, reads, lineage
│   ├── AgentOrchestration/Execution/                     # claim, renew, checkpoint, complete, fail, monitor, reconcile
│   ├── AgentOrchestration/Workloads/                     # workload token, request authentication, queue credentials
│   ├── Telemetry/AgentOrchestrationMetrics.cs            # new meter
│   └── ApplicationServiceCollectionExtensions.cs         # register handlers and options
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Migrations/NNNN_agentorchestration_conversations_and_invocations.sql  # numbered at implementation time
│   ├── Persistence/AgentOrchestration/                   # Dapper stores and queries
│   ├── AgentOrchestration/                               # team scope sources
│   ├── Messaging/Nats/NatsTopology.cs                    # AGENT_WORK stream and agent-workload consumer
│   ├── Messaging/Nats/NatsQueueCredentialIssuer.cs       # agent consumer permissions
│   ├── Persistence/PostgresAuditTrail.cs                 # detail allow-list
│   ├── Workers/AgentAttemptMonitorWorker.cs              # new
│   ├── Workers/AgentWorkReconciliationWorker.cs          # new
│   └── InfrastructureServiceCollectionExtensions.cs      # register stores and workers
├── SocAlytics.Platform.Api/
│   ├── AgentOrchestration/AgentConversationEndpoints.cs  # member operations
│   ├── AgentOrchestration/AgentWorkloadEndpoints.cs      # workload operations
│   ├── AgentOrchestration/AgentWorkloadDPoPAuthenticationHandler.cs  # scheme AgentWorkloadDPoP
│   ├── Security/AuthorizationPolicyNames.cs              # AgentWorkload policy
│   ├── Program.cs                                        # map endpoints, scheme, policy
│   ├── appsettings.Development.json                      # AgentOrchestration section
│   └── openapi/v1.json                                   # refreshed OpenAPI snapshot
├── SocAlytics.Platform.AppHost/Program.cs                # ephemeral workload key, start the simulated workload
└── Tests/
    ├── SocAlytics.Platform.SimulatedAgentWorkload/       # new Development and Test worker executable
    ├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/  # new tests and support (TestAgentWorkload)
    ├── SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs
    ├── SocAlytics.Platform.Contracts.Tests/              # catalog picks up the new contracts
    └── SocAlytics.Platform.Architecture.Tests/           # layer rules cover the new folders
src/platform/SocAlytics.Platform.slnx                     # add the simulated workload project
```

**Structure Decision**: The feature adds the Agent Orchestration functional area as folders in the existing platform
layers, following the Analysis functional area's command, store, worker, and endpoint layout, and one Development and
Test worker project under `src/platform/Tests`, added to the solution and referenced by the AppHost. It references no
production project, so it exercises only the public HTTP and NATS protocol. `PersistedTableClassifications` classifies
the participant, lineage, advice, operation, and token tables. The OpenAPI snapshot is refreshed with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
during implementation, never in the verification environment.
