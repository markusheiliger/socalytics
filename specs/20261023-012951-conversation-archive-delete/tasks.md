---

description: "Task list for Match Conversation Archive and Delete"
---

# Tasks: Match Conversation Archive and Delete

**Input**: Design documents from `specs/20261023-012951-conversation-archive-delete/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/README.md), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (FR-019, SC-001 to SC-008), so every story has test tasks. Test tasks follow the implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Contract, schema, domain types, options, audit constants, and store members that every story needs

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Contract, schema, and domain

- [x] T001 [P] Add the JSON Schema 2020-12 contract `contracts/agent-orchestration/purge-event/v1/purge-event.schema.json` (`$id` `https://socalytics.invalid/contracts/agent-orchestration/purge-event/v1/purge-event.schema.json`, `x-socalytics-version` `1.0.0`, properties `schemaVersion` (`"1.0.0"`), `conversationId` (uuid), and `requestedAt` (date-time), all required, `additionalProperties: false`) with `releases/1.0.0.schema.json`, `examples/valid/purge.json`, and `examples/invalid/` fixtures `carries-question.json`, `carries-advice.json`, `missing-conversation-id.json` with `expectations.json`, following `contracts/agent-orchestration/work-notification/v1/`, and add its index row to `contracts/README.md` following the existing `$id` convention so that `ContractCatalog`, `ContractIndexTests`, and `SchemaMetaValidationTests` in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/` stay green
- [x] T002 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/0032_agentorchestration_conversation_lifecycle.sql` (renumber to the next free number on an up-to-date default branch): widen the `agent_conversations.state` CHECK to `active`, `archived`, `deleted` and add `archived_at timestamptz NULL`; create `agent_conversation_tombstones` (`conversation_id` PK and FK `agent_conversations(id)`, `team_id`, `deleted_by_member_id` FK `member_account(id)`, `deleted_at timestamptz`, `invocations_suppressed` integer; immutable: reject update and delete); create `agent_conversation_purges` (PK `conversation_id` + `copy` with `copy` in `primary`, `index`; `state` in `pending`, `completed`, `unresolved`; `outcome` in `purged`, `not-present`, or null while pending; `attempts` integer; `next_attempt_at`, `completed_at`, `updated_at`; `socalytics.attach_version_trigger`); add `lifecycle-suppressed` to the `agent_invocations.state` CHECK, `purged_at timestamptz NULL`, and the attempt `end_reason` value `lifecycle-suppressed`; add `agent-conversation-purge` to the `outbox_messages.message_type` CHECK; change the immutability triggers and `REVOKE`s of `agent_accepted_advice`, `agent_lineage_entries`, and `agent_workload_operations` to permit deletion only for a conversation that has a tombstone, and permit blanking `agent_invocations.question` (kept `NOT NULL`, set to the empty string) only with `purged_at` stamped
- [x] T003 [P] Add the domain types in `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/`: the new `AgentConversationState.cs` (`Active`, `Archived`, `Deleted`) and, in `AgentInvocationState.cs`, `AgentStateTransitions.cs`, and `AgentPersistedNames.cs`, the terminal invocation state `LifecycleSuppressed` (persisted and wire name `lifecycle-suppressed`, reachable from `queued` and `running`), the attempt end reason `lifecycle-suppressed`, and the persisted names `active`, `archived`, `deleted`, `primary`, `index`, `pending`, `completed`, `unresolved`, `purged`, `not-present`
- [x] T004 [P] Add the `Purge` options (`PollInterval`, `BatchSize`, `MaxAttempts`, `RetryDelay`, development values) to `src/platform/SocAlytics.Platform.Application/AgentOrchestration/AgentOrchestrationOptions.cs` under `AgentOrchestration:Purge` with validation
- [x] T005 [P] Add the audit event constants `agent.conversation.archived`, `agent.conversation.unarchived`, `agent.conversation.deleted`, `agent.conversation.purge-completed`, and `agent.conversation.purge-unresolved` and allow-list their detail keys (conversation id, team id, invocation count, tombstone id, copy, reason code; never text) in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/AgentOrchestrationAuditEvents.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Persistence/PostgresAuditTrail.cs`
- [x] T006 [P] Classify `agent_conversation_tombstones` as immutable and `agent_conversation_purges` as versioned in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`

### Store members

- [x] T007 Extend `IAgentConversationStore` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/IAgentConversationStore.cs` and its Dapper implementation in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/AgentConversationStore.cs` with the conversation `State` and `ArchivedAt` on its records, a `state` filter on the list query (default `active`, `archived`), version-guarded `Archive` and `Unarchive` writes, the `Delete` write (state `deleted`, tombstone insert, `agent-conversation-purge` outbox message, and the two purge records `primary` and `index` in `pending`, in the caller's unit of work), a tombstone lookup by conversation id, purge-record claim (`FOR UPDATE SKIP LOCKED`), completion, and unresolved marking members, the `Primary` purge (delete the conversation's `agent_accepted_advice`, `agent_lineage_entries`, and `agent_workload_operations` rows and blank `agent_invocations.question` with `purged_at`, in one transaction), and a query for tombstones whose conversation is not `deleted` or still holds nonterminal invocations or unfenced work
- [x] T008 Extend `IAgentInvocationStore` and `IAgentAttemptStore` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/IAgentInvocationStores.cs` and their implementations in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/AgentInvocationStore.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/AgentAttemptStore.cs` with the suppression of a conversation's nonterminal invocations to `lifecycle-suppressed` (leaving `attempts_used` and `ready_occurrence` unchanged, returning the suppressed count) and the fencing of their `claimed` and `active` attempts with end reason `lifecycle-suppressed`

**Checkpoint**: Contract, schema, domain, options, audit constants, and stores exist; user stories can begin

---

## Phase 2: User Story 1 - Archive and unarchive my conversation (Priority: P1) 🎯 MVP

**Goal**: The initiating member archives an Active conversation and unarchives it; archived conversations stay readable, refuse new questions and specialists, and let in-flight work finish.

**Independent Test**: Start a conversation with an answered question, archive it, check the active list and the archived filter, attempt a question while it is archived, then unarchive it and ask again.

### Conversation views

- [x] T009 [US1] Expose `State` (`active` or `archived`) on the conversation views in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/AgentConversationViews.cs`, pass the `state` filter through `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/ListAgentConversations.cs`, and keep `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/GetAgentConversation.cs` returning the state

### Archive handlers and refusal

- [x] T010 [P] [US1] Add the `ArchiveAgentConversation` handler in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/ArchiveAgentConversation.cs`: only the conversation's `owner_member_id` still holding Coach or Club Admin scope on the owning team (others `404`, Viewers `403`), version-guarded (stale version `412` with the current version), a request already satisfied by the current state succeeds without change, new version, or audit event whatever the version is, in an `IUnitOfWork` scope writing `agent.conversation.archived`
- [x] T011 [P] [US1] Add the `UnarchiveAgentConversation` handler in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/UnarchiveAgentConversation.cs` with the same authorization, idempotency, and version rules, clearing `archived_at` and writing `agent.conversation.unarchived`
- [x] T012 [P] [US1] Refuse a submission to an archived conversation with `409` reason `conversation-archived` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/SubmitAgentInvocation.cs`, and refuse a specialist addition the same way in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/AddAgentConversationParticipant.cs`; claim, completion, and monitor paths stay unchanged so in-flight invocations finish normally (FR-004, FR-005)
- [x] T013 [US1] Register the archive and unarchive handlers in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`
- [x] T014 [US1] Add `archiveAgentConversation` (`POST /api/v1/agent-conversations/{conversationId}/archive`, `If-Match`, `200` `AgentConversationDto` with `state: "archived"`, `401`, `403`, `404`, `412`, `428`) and `unarchiveAgentConversation` (`POST .../unarchive`, `200` with `state: "active"`) through `MapMemberApi()`, `IfMatchHeader`, and `ProblemResults`, the `state` query (`active` default, `archived`) of `listAgentConversations`, the `state` field of the DTO, the `ETag` on responses, and the `conversation-archived` problem for the changed submission and participant operations, in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentConversationEndpoints.cs`
- [x] T015 [US1] Refresh the committed OpenAPI snapshot locally with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` (the snapshot file under `src/platform/Tests/SocAlytics.Platform.Host.Tests/` that `OpenApiContractTests.cs` compares), then regenerate the Kiota client under `src/clients/web/src/generated/platform-api/` with `npm run generate` in `src/clients/web`
- [x] T016 [P] [US1] Add integration tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/LifecycleArchiveTests.cs`: archive and unarchive round trip with the active list and archived filter, history, advice attribution, and lineage unchanged, refusal of questions and specialists with `conversation-archived`, in-flight invocation finishing on an archived conversation, `412` stale version, `428` missing version, idempotent repeats without a second audit event, Viewer `403` and non-owner `404`, and concurrent archive and unarchive yielding one success (SC-007, SC-005)
- [x] T017 [P] [US1] Add the Domain tests of the conversation and invocation state rules (including `lifecycle-suppressed` transitions) to `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Domain/AgentDomainTests.cs`
- [x] T018 [P] [US1] Add the purge-event contract tests (valid example accepted, each invalid example rejected, released baseline compatibility) in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/PurgeEventContractTests.cs`

**Checkpoint**: Archive and unarchive work through the API and are verified independently (MVP)

---

## Phase 3: User Story 2 - Delete my conversation (Priority: P1)

**Goal**: The initiating member deletes a conversation: authority is denied first, a tombstone and a purge event commit in one transaction, nonterminal invocations are suppressed, and the purge worker removes the content from the Primary copy and records the Index outcome.

**Independent Test**: Delete a conversation with a completed and an in-flight invocation; verify it is unreadable at once, the in-flight work is suppressed without consuming budget, the purge completes, content is gone from the Primary copy, and the audit holds identifiers only.

### Implementation for User Story 2

- [x] T019 [P] [US2] Add the `DeleteAgentConversation` handler in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/DeleteAgentConversation.cs`: same owner-only authorization as archive, version-guarded (`412`, `428`), in one `IUnitOfWork` transaction set state `deleted`, commit the tombstone, suppress nonterminal invocations without budget use, fence their attempts, and enqueue the `agent-conversation-purge` outbox message and purge records, writing `agent.conversation.deleted`; a repeat delete by the tombstone's `deleted_by_member_id` still holding Coach or Club Admin scope on the tombstone's `team_id` succeeds without a second tombstone, every other caller gets `404`; the repeat path is authorized from the tombstone (the caller equals `deleted_by_member_id` and holds Coach or Club Admin scope on `tombstone.team_id` through the team-scope check by team id), not through the `agent-conversation` resource kind (which answers not found for a deleted conversation), and this lookup runs before the not-found answer
- [x] T020 [P] [US2] Resolve a `deleted` conversation as no scope for the `agent-conversation` and `agent-invocation` resource kinds in `src/platform/SocAlytics.Platform.Infrastructure/AgentOrchestration/AgentScopeSources.cs`, and make the conversation, turn, invocation, and lineage reads and the workload operations answer not found for a deleted conversation in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/GetAgentInvocation.cs`, `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/GetAgentInvocationLineage.cs`, `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/ListAgentConversationTurns.cs`, and `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/AgentAttemptGuard.cs` (SC-001, SC-003)
- [x] T021 [P] [US2] Add the `PurgeAgentConversation` handler in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/PurgeAgentConversation.cs`: claim due pending purge records, purge the `primary` copy through the store, record `index` as `completed` with outcome `not-present`, count attempts against `Purge:MaxAttempts`, mark a copy `unresolved` with `agent.conversation.purge-unresolved` after the last attempt, write `agent.conversation.purge-completed` with identifiers only, idempotent on retry
- [x] T022 [P] [US2] Handle the message type `agent-conversation-purge` in `src/platform/SocAlytics.Platform.Application/Messaging/PublishOutboxBatch.cs` and publish it on the explicit subject `agents.conversations.purge`: add that subject to the events stream's subjects (`EventsSubjects` default in `NatsOptions` and the stream configuration in `NatsTopology`) in `src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/`; no consumer is added (the `AgentWorkStream` consumer filter and the per-Manager queue permissions stay unchanged)
- [x] T023 [US2] Add the tombstone reapplication pass to `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/ReconcileAgentWork.cs`: before the ready-notification republish, reassert state `deleted`, suppression of nonterminal invocations, and fencing of unfenced attempts for every tombstone whose conversation is inconsistent (each under row locks), recreate missing purge records, and retry `unresolved` purge records (flipping them to `completed` on success)
- [x] T024 [US2] Add the hosted `AgentConversationPurgeWorker` (the `WorkerLoop` pattern of `AgentWorkReconciliationWorker`) in `src/platform/SocAlytics.Platform.Infrastructure/Workers/AgentConversationPurgeWorker.cs`, and register it, the new handlers, and the options in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` and `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`
- [x] T025 [US2] Add `deleteAgentConversation` (`DELETE /api/v1/agent-conversations/{conversationId}`, `If-Match`, `204`, `401`, `403`, `404`, `412`, `428`) through `MapMemberApi()`, `IfMatchHeader`, and `ProblemResults` in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentConversationEndpoints.cs`, then refresh the OpenAPI snapshot and the Kiota client as in T015
- [x] T026 [P] [US2] Add integration tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/LifecycleDeleteTests.cs`: delete of Active and Archived conversations, not found for every reader and every workload operation from the commit (including during purge), suppression of queued and running invocations with unchanged `attempts_used` and late claim, checkpoint, completion, and failure rejected, completion racing the tombstone creating no visible advice, repeat delete `204` for the initiator in scope (authorized from the tombstone although the `agent-conversation` resource no longer resolves) and `404` for everyone else including a former initiator who lost scope, `412`, `428`, Viewer `403`, and concurrent delete yielding one success (SC-001, SC-003, SC-005, SC-007)
- [x] T027 [P] [US2] Add integration tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/ConversationPurgeTests.cs`: the purge removes questions, advice, evidence references, and lineage text, `index` ends `completed` with outcome `not-present`, an interrupted purge is retried idempotently, a purge failing past `MaxAttempts` becomes `unresolved` and completes on a later reconciliation with no member-facing read, and no question or advice text appears in the audit, logs, problem bodies, or the outbox payload, and the `agent-conversation-purge` outbox message is published successfully on `agents.conversations.purge` (SC-002)
- [x] T028 [P] [US2] Add reconciliation tests for a deliberately inconsistent tombstoned conversation (state not `deleted`, nonterminal invocation, active attempt) in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/MonitorAndReconciliationTests.cs` (SC-006)
- [x] T029 [P] [US2] Extend the Agent Orchestration architecture tests for the new handlers, stores, worker, and sole-writer rule in `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/AgentOrchestrationArchitectureTests.cs`

**Checkpoint**: Delete, purge, and reconciliation work through the API and are verified independently

---

## Phase 4: User Story 3 - Manage conversation lifecycle in the Web UI (Priority: P2)

**Goal**: On the connected match conversation views, the initiating member filters active and archived conversations and archives, unarchives, or deletes through confirmed actions with explicit states, online only, with no conversation content persisted client-side.

**Independent Test**: In the simulated and platform-triage browser projects, archive, filter, unarchive, and delete a conversation, and verify the actions are absent for other members and Viewers and that no content is persisted client-side.

### Implementation for User Story 3

- [x] T030 [P] [US3] Extend the models and the adapter in `src/clients/web/src/platform/models/agents.ts` and `src/clients/web/src/platform/adapters/agentConversationAdapter.ts` with the conversation `state`, the `state` list filter, and the archive, unarchive, and delete calls (with `If-Match`, mapping `404`, `409`, `412`, `428`, `403`, unavailable, and offline to the existing failure model)
- [x] T031 [P] [US3] Add the lifecycle strings (filter labels, state badge, confirmation texts naming the effect, delete stating that it is permanent and removes questions and advice, success, refusal, conflict with reload offer, unavailable, and offline messages, and the empty state of each filter) to `src/clients/web/src/i18n/en/triage.json`
- [ ] T032 [US3] Add the active and archived filter, the state badge, and the explicit empty state of each filter to `src/clients/web/src/features/triage/TriageSection.tsx`, and add the `state` dimension to the query keys in `src/clients/web/src/features/triage/triageKeys.ts`
- [ ] T033 [US3] Add the confirmed archive, unarchive, and delete actions (confirmation dialog, loading, success, refusal, conflict with reload, unavailable, and offline states, no optimistic change, queries invalidated after success, for every conversation the session member can open (conversation reads are initiator-only, so a non-initiator gets the unavailable view) and only when the role is Coach or Club Admin, with no owner comparison, the unavailable view for a deleted conversation address) to `src/clients/web/src/features/triage/ConversationPage.tsx`, with the confirmation dialog in `src/clients/web/src/features/triage/ConversationLifecycleDialog.tsx`
- [ ] T034 [P] [US3] Add Vitest unit tests for the filter, state badge, and empty states in `src/clients/web/tests/unit/features/triage/TriageSection.test.tsx`, for the actions, confirmations, per-outcome states, and their absence for Viewers and the unavailable view for a non-initiator in `src/clients/web/tests/unit/features/triage/ConversationPage.test.tsx`, and for the adapter in `src/clients/web/tests/unit/platform/adapters/agentConversationAdapter.test.ts`
- [ ] T035 [US3] Extend the simulated platform in `src/clients/web/tests/e2e/support/simulatedApi.ts` with the archive, unarchive, and delete operations, the `state` list filter, and the refusal, conflict, and unavailable responses, and add the `simulated` browser tests (archive, filter, unarchive, delete, refusal, conflict, unavailable, offline, actions absent for Viewers, a non-initiator's conversation answered `404` by the simulated platform, and no conversation content in `localStorage`, `sessionStorage`, IndexedDB, or Cache Storage) in `src/clients/web/tests/e2e/simulated/triage-lifecycle.spec.ts`
- [ ] T036 [US3] Add the `platform-triage` browser tests for the same flows against the simulated agent workload in `src/clients/web/tests/e2e/platform/triage-lifecycle.spec.ts`, and widen the `platform-triage` project's `testMatch` in `src/clients/web/playwright.config.ts` to `/triage(-lifecycle)?\.spec\.ts$/` so the file runs in the same chained, non-parallel project and the later projects (`platform` with `manager-fleet`, `platform-registry`, and the rest) still depend on `platform-triage` (SC-004, SC-008)

**Checkpoint**: All three user stories are independently functional

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Documentation and final validation

- [ ] T037 Update `docs/architecture/` so every architecture statement this feature makes false is corrected for the merged state, re-reading the documents at implementation time and following the plan's `## Architecture Updates` section: `docs/architecture/intelligence-and-agents.md` (Agent Orchestration Authority, Durable State And Transaction Boundaries, the conversation lifecycle paragraph, the data-copy and deletion-order paragraph, and the added design decisions in present tense), `docs/architecture/client-applications.md` (Web UI triage paragraph), `docs/architecture/platform-implementation.md` (Agent Orchestration persistence), `docs/architecture/production-operations.md` (stamp state and restore steps), and `docs/architecture/security-and-data-governance.md` (`POL-008` and `GOV-THR-009` development evidence, status stays Open / Blocking); keep restore-time tombstone reapplication, backup, Hermes cache, telemetry, and replica disposition, `POL-008` values, and Coach Agent and consultation lifecycle stated as unresolved or later
- [ ] T038 Update the current-state text of `src/platform/README.md`, `README.md`, and `AGENTS.md` (remove "lifecycle operations" from the deferred lists and name the lifecycle operations, tombstones, purge worker, and Web UI actions as development evidence), and list the purge-event contract in `contracts/README.md` if T018 did not
- [ ] T039 Run the validation of [quickstart.md](quickstart.md): `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, the Integration (`--filter "FullyQualifiedName~AgentOrchestration"`), Architecture, Contracts, and Host tests, `npm run build`, `npm test`, and the `simulated` and `platform-triage` Playwright projects in `src/clients/web`, and `node .github/scripts/check-markdown.mjs`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: No dependencies; BLOCKS all user stories
- **User Story 1 (Phase 2)**: Depends on Phase 1
- **User Story 2 (Phase 3)**: Depends on Phase 1; shares `AgentConversationEndpoints.cs` and the registration files with US1, so it follows US1 when run sequentially
- **User Story 3 (Phase 4)**: Depends on the regenerated client (T015 for archive, T025 for delete) and so on US1 and US2
- **Polish (Phase 5)**: Depends on all stories

### Within Each Story

- Handlers and stores before registration, registration before endpoints, endpoints before OpenAPI refresh and tests

### Parallel Opportunities

- T001 to T006 are disjoint; T010 to T012, T016 to T018, T019 to T022, T026 to T029, and T030 and T031 are disjoint within their groups

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1, then Phase 2, and validate archive and unarchive independently
2. Add User Story 2 (delete, purge, reconciliation) and validate
3. Add User Story 3 (Web UI) and validate in both browser projects
4. Finish with the documentation and the quickstart validation
