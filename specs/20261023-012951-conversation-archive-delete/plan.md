# Implementation Plan: Match Conversation Archive and Delete

**Branch**: `20261023-012951-conversation-archive-delete` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261023-012951-conversation-archive-delete/spec.md`

## Summary

The Agent Orchestration functional area gains the conversation states `archived` and `deleted`, three owner-only
member operations (archive, unarchive, delete) under `/api/v1`, a durable tombstone with purge records, the
`lifecycle-suppressed` terminal invocation state, a leased purge worker, and tombstone reapplication in the existing
reconciliation worker. The connected Web UI triage views gain an archived filter and confirmed lifecycle actions.
Deletion denies authority first (the conversation's team-scope source stops resolving it), then commits the tombstone,
the purge records, the suppression of nonterminal invocations, and the purge outbox event in one transaction. The
purge worker then removes question, advice, evidence-reference, and lineage text from the Primary copy and records
each copy's outcome; the Index copy is purged by the same worker (the stamp has no separate index structure for these
records yet, so its outcome is `not-present`, never claimed as purged content). Everything stays inside the stamp's
database; no retention, residency, approval, backup, or agent-catalog decision is made (`POL-008` stays open).

## Technical Context

**Language/Version**: C# on .NET 10 (platform, `src/platform/global.json`); TypeScript, React, Vite, Node.js 24 (Web UI)

**Primary Dependencies**: existing only: ASP.NET Core minimal APIs, Dapper, DbUp, Npgsql, NATS JetStream (outbox publication), Kiota-generated Web UI client, Vitest, Playwright. No new dependency.

**Storage**: PostgreSQL; forward-only migration `0032_agentorchestration_conversation_lifecycle.sql` (next free number after `0031`, renumbered at implementation time if taken)

**Testing**: xUnit v3 Host, Architecture, Integration (Docker, `AgentOrchestration/`), and Contracts tests; Vitest unit tests; Playwright `simulated` and `platform-triage` projects; OpenAPI snapshot refresh through the documented command

**Target Platform**: Linux container API host (development evidence only), browser Web UI

**Project Type**: web-service plus web client inside the polyglot monorepo (`src/platform`, `src/clients/web`)

**Performance Goals**: lifecycle commands are single-transaction operations (no SLO beyond SC-004's one-minute Web UI flow); purge bounded per batch

**Constraints**: authority denied before any content removal; no question or advice text in audit, logs, telemetry, or problem responses; Agent Orchestration remains the sole writer of its tables; online only Web UI with no client-side persistence of conversation content; development values only for purge options

**Scale/Scope**: per-member conversations of one match (tens), invocations per conversation (tens); one purge unit per deleted conversation

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Assessment |
| --- | --- |
| I. Architecture Is the Design Authority | Pass. The design applies `intelligence-and-agents.md` (lifecycle operations, tombstones, `lifecycle-suppressed`, archive rules) and `security-and-data-governance.md` (`POL-008`, deletion order); the `## Architecture Updates` section below and its documentation task keep `docs/architecture/` true for the merged state. Unresolved `POL-008` values, backups, and Coach Agent lifecycle stay stated as open. |
| II. Respect Source-Area Ownership | Pass. Code only in `src/platform` (Domain, Application, Infrastructure, Api, Tests), `src/clients/web`, and `contracts/`; no new first-level area or project. |
| III. API-First Control Plane | Pass. Lifecycle is exposed as versioned `/api/v1` operations through `MapMemberApi()`; the Web UI consumes them through the generated client; the purge event contract is JSON Schema 2020-12 in `contracts/agent-orchestration/`. |
| IV. Evidence Over Claims | Pass. Integration, architecture, contract, Vitest, and Playwright evidence is planned (see `quickstart.md`); nothing claims deployment support or production readiness. |
| V. Focused, Minimal Changes | Pass. No new projects, dependencies, or abstractions; catalog, Coach Agent, retention, export, and bulk deletion stay out. |
| Technology and Tooling | Pass. No new framework, SDK, or tool, so `environment-setup` and `environment-verify` need no environment spec. |

**Post-design re-check**: unchanged; Complexity Tracking has no entries.

## Architecture Updates

### Narratives

- [Intelligence and Agents → Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority): the owner of conversations, lifecycle tombstones, and the control-plane operations and recovery of direct invocations.
- [Intelligence and Agents → Durable State And Transaction Boundaries](../../docs/architecture/intelligence-and-agents.md#durable-state-and-transaction-boundaries): the `lifecycle-suppressed` state, the "Suppress lifecycle state" command, and the data-copy and deletion-order paragraph (`DAT-008`, `POL-008`).
- The conversation lifecycle paragraph of the same document ("Conversations remain `Active` until the user explicitly archives or deletes them") and the paragraph on triage bound to an analysis run.
- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): the connected match triage conversations.
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md) (`POL-008`, `GOV-THR-009`): the deletion order and the open values.
- [Platform Implementation](../../docs/architecture/platform-implementation.md) (Agent Orchestration persistence at line ~212) and [Production Operations](../../docs/architecture/production-operations.md) (tombstone state in stamp storage and the restore order).

### Design updates

The architecture already states the design this plan implements (states `Active` and `Archived`, deletion order, tombstone, purge outbox event, `lifecycle-suppressed`, restore reapplication), so no narrative changes now. The implementation must add the following decisions to the owning narrative in present tense, in `intelligence-and-agents.md`:

- A deletion commits the `deleted` conversation state, the tombstone, the per-copy purge records, the suppression of nonterminal invocations, and the purge outbox event in one transaction; the purge records are the durable purge work queue, the purge worker records an outcome per copy, an unresolved purge is operator-visible only and retried by reconciliation, and a repeated delete is reported only to the initiating member still in the owning team's scope.
- Conversation reads are initiator-only today (every other caller gets `404`), so the Web UI shows the lifecycle actions to the signed-in Coach or Club Admin for every conversation they can open, with no owner field or comparison; the non-initiator case is verified at API level and by a `404` in the simulated platform.
- The stamp has no separate Index copy for these records, so the Index outcome is `not-present`; backups, Hermes caches, telemetry, and replicas are not purged by this feature and `POL-008` values stay open.
- Archived conversations stay readable, refuse new questions and specialists, and let in-flight invocations finish.
- The reconciliation worker reapplies tombstones within the stamp's database only; restore-time reapplication beyond it stays described as a requirement, not an implemented behavior.

### Status statements at merge

| Document and section | Current statement | Must say at merge |
| --- | --- | --- |
| `intelligence-and-agents.md`, Agent Orchestration Authority | "consultation and lifecycle operations remain planned" | Archive, unarchive, and delete operations for direct triage conversations are implemented as development evidence; consultation operations remain planned. |
| same section | "tombstone reapplication remains planned" | Tombstone reapplication by the reconciliation worker is implemented inside the stamp's database; restore-time reapplication stays planned. |
| same section, first implementation paragraph | "Consultations, Coach Agents, lifecycle operations, and tombstones remain later work." | Only consultations and Coach Agents remain later work; lifecycle operations and tombstones are implemented for direct conversations, citing the archived spec of this feature. |
| same document, Durable State and Transaction Boundaries | `lifecycle-suppressed` and "Suppress lifecycle state" described as the architecture's model | Marked implemented for direct invocations (suppression of nonterminal invocations without budget use). |
| same document, conversation lifecycle paragraph | "The first implementation has no automatic inactivity archival or expiration." plus planned-sounding archive and delete | Archive, unarchive, and delete are implemented for the initiating member; no automatic archival or expiration remains true. |
| same document, data-copy paragraph | deletion order described in the general present tense | State what is implemented (Primary purge, Index `not-present`) and keep Hermes cache, telemetry, replica, and backup disposition as unresolved. |
| `client-applications.md`, Web UI triage paragraph | "Coach Agents, consultations, streaming, editing, deletion, sharing, and evidence clip playback are not part of them" | Archive, unarchive, and delete with the archived filter are part of them; the others stay out. |
| `platform-implementation.md`, Agent Orchestration persistence | "lifecycle tombstones" listed among owned state | Describe the tombstone and purge tables as implemented. |
| `production-operations.md`, stamp state and restore steps | tombstone state and reapplication as target statements | Keep restore-time reapplication and backup disposition as unresolved, state what exists in the stamp's database. |
| `security-and-data-governance.md`, `POL-008` and `GOV-THR-009` | deletion described as requirements | Add the development evidence for deletion order and per-copy outcomes; status stays Open / Blocking. |
| `src/platform/README.md` (line ~251), `README.md`, and `AGENTS.md` current-state text | "Hermes, consultations, Coach Agents, MCP tools, lifecycle operations, the Web UI, and production values remain deferred"; Web UI lists without lifecycle actions | Remove "lifecycle operations" from the deferred lists and name the lifecycle operations and Web UI actions as development evidence. |

The documentation task of `/speckit-tasks` MUST make the narratives above and every status statement this feature
affects true for the merged state, re-reading `docs/architecture/` at implementation time (the rewrites listed are
examples, not the whole job), and MUST reference `docs/architecture/`.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-012951-conversation-archive-delete/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── README.md
│   ├── lifecycle-operations.md
│   └── purge-event.md
├── checklists/
│   └── requirements.md
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
contracts/agent-orchestration/purge-event/v1/            # new purge-event schema, examples, release 1.0.0; index row in contracts/README.md

src/platform/
├── SocAlytics.Platform.Domain/AgentOrchestration/
│   ├── AgentInvocationState.cs, AgentStateTransitions.cs, AgentPersistedNames.cs   # LifecycleSuppressed
│   └── AgentConversationState.cs                                                   # Active, Archived, Deleted (new)
├── SocAlytics.Platform.Application/AgentOrchestration/
│   ├── Conversations/                      # ArchiveAgentConversation, UnarchiveAgentConversation, DeleteAgentConversation; list state filter; views
│   ├── Execution/                          # ReapplyConversationTombstones (reconciliation), PurgeAgentConversation (purge worker handler)
│   ├── Invocations/SubmitAgentInvocation.cs, Conversations/AddAgentConversationParticipant.cs   # archived refusal
│   ├── IAgentConversationStore.cs, IAgentInvocationStores.cs                      # lifecycle, tombstone, purge-record members
│   ├── AgentOrchestrationAuditEvents.cs, AgentOrchestrationOptions.cs             # events, Purge options
│   └── Messaging/PublishOutboxBatch.cs     # purge message type
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/AgentOrchestration/     # store implementations
│   ├── Persistence/Migrations/0032_agentorchestration_conversation_lifecycle.sql
│   ├── AgentOrchestration/AgentScopeSources.cs   # deleted conversations resolve to no scope
│   └── Workers/AgentConversationPurgeWorker.cs, AgentWorkReconciliationWorker.cs
├── SocAlytics.Platform.Api/AgentOrchestration/AgentConversationEndpoints.cs        # three operations, state filter, DTO state
└── Tests/                                  # Integration (AgentOrchestration/Lifecycle*), Architecture, Contracts, Host (OpenAPI snapshot)

src/clients/web/
├── src/platform/adapters/agentConversationAdapter.ts, models/agents.ts, generated Kiota client
├── src/features/triage/                    # TriageSection filter, ConversationPage actions, confirmation dialogs
├── src/i18n/en/triage.json
└── tests/unit/features/triage/, tests/e2e/simulated/, tests/e2e/platform/

docs/architecture/                          # see Architecture Updates
```

**Structure Decision**: extend the existing Agent Orchestration folders inside the layered monolith; no new project. The
purge worker follows the existing `AgentWorkReconciliationWorker` pattern (hosted in the API, `WorkerLoop`).
