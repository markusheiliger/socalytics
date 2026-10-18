---

description: "Task list for Agent Orchestration Direct Invocations"
---

# Tasks: Agent Orchestration Direct Invocations

**Input**: Design documents from `specs/20261017-204332-agent-orchestration-invocations/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (FR-037, SC-002 to SC-006), so every story has test tasks. Test tasks follow the implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Contracts, schema, domain rules, options, stores, transport topology, and workload authentication that every story needs

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Contracts, schema, and domain

- [x] T001 [P] Add the JSON Schema 2020-12 contract `contracts/agent-orchestration/work-notification/v1/work-notification.schema.json` with `releases/1.0.0.schema.json` and golden fixtures under `examples/valid/` (a ready notification) and `examples/invalid/` (carries `question`, carries `advice`, missing `invocationId`, with `expectations.json`), following `contracts/analysis/analyst-job/v1`: `{ schemaVersion: "1.0.0", invocationId, route: "user-to-specialist", agentId, readyOccurrence, correlationId }` and "additional properties are refused, so no content can be added" (FR-028, FR-036; contracts/agent-orchestration-api.md)
- [x] T002 [P] Add the contract `contracts/agent-orchestration/execution-envelope/v1/execution-envelope.schema.json` with `releases/1.0.0.schema.json` and valid and invalid fixtures (valid with empty and with bounded `history`; invalid without `fencingToken`, with an unknown property, with a `history` entry missing `agentId`): `{ schemaVersion, invocationId, conversationId, attemptId, attemptNumber, fencingToken (string), leaseExpiresAt, leaseSeconds, deadlineAt, correlationId, route, agentId, question, history: [{ invocationId, agentId, question, advice }], scope: { teamId, matchId, analysisRunId }, limits: { maxAdviceCharacters, maxCheckpointNoteCharacters } }` (FR-015, FR-016, FR-036)
- [x] T003 [P] Add the contract `contracts/agent-orchestration/attempt-report/v1/attempt-report.schema.json` with `releases/1.0.0.schema.json` and valid and invalid fixtures for its three shapes: `checkpoint { operationId, fencingToken, kind, note }`, `completion { operationId, fencingToken, advice, executionReferences { runtime, modelProvider, model, deployment, promptRef, toolPolicyRef, safetyPolicyRef } }` (each reference "opaque strings, each 1 to 200 characters"), and `failure { operationId, fencingToken, reasonCode, retryable }`, with `operationId` matching `^[A-Za-z0-9._\-]{1,128}$`; invalid fixtures include a completion without `executionReferences` and a failure without `retryable` (FR-018–FR-020, FR-036)
- [x] T004 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_agentorchestration_conversations_and_invocations.sql`, numbered as the next free number on an up-to-date default branch, with every table and column of data-model.md: versioned `socalytics.agent_conversations` (`state` check "`active` only", unique (`owner_member_id`, `idempotency_key`)) with `socalytics.attach_version_trigger`; `socalytics.agent_conversation_participants` (primary key (`conversation_id`, `agent_id`)) with `socalytics.attach_aggregate_child_triggers`; versioned `socalytics.agent_invocations` (`route` check `user-to-specialist`, `state` check `queued`, `running`, `succeeded`, `failed`, `failure_reason` check `attempts-exhausted`, `execution-failed`, `scope-lost`, unique (`owner_member_id`, `conversation_id`, `idempotency_key`), the indexes "(`owner_member_id`) where `state IN ('queued','running')`", "(`state`, `last_notified_at`)", and "(`conversation_id`, `submitted_at`)"); versioned `socalytics.agent_attempts` (`state` check `claimed`, `active`, `completed`, `failed`, `expired`, `fenced`; `end_reason` check of data-model.md; unique (`invocation_id`, `attempt_number`)); immutable `socalytics.agent_lineage_entries` (primary key (`invocation_id`, `sequence`), `kind` check of data-model.md) and `socalytics.agent_accepted_advice` (primary key `invocation_id`) and `socalytics.agent_workload_operations` (primary key (`workload_client_id`, `operation_id`), `kind` check `claim`, `checkpoint`, `complete`, `fail`), each protected by `socalytics.reject_immutable_change()`; unversioned `socalytics.agent_workload_access_tokens`; and replace `ck_security_audit_event_actor_kind` with `CHECK (actor_kind IN ('member', 'system', 'anonymous', 'analyst-manager', 'agent-workload'))`; never edit an applied migration; classify `agent_conversation_participants` as `ChildOf("agent_conversations", "conversation_id")` and the lineage, advice, operation, and token tables as unversioned with their reason in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs` (FR-028, FR-030; data-model.md)
- [x] T005 [P] Add the domain types in `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/`: `AgentInvocationState` and `AgentAttemptState` with wire names (`accepted/queued`, `claimed/running`, `succeeded`, `failed`; `claimed`, `active`, `completed`, `failed`, `expired`, `fenced`) and the allowed transitions of data-model.md "Invocation states" (`AgentStateTransitions.cs`), `AgentFailureReason`, `AgentStaleDisposition` (`attempt-not-current`, `lease-expired`, `attempt-fenced`, `invocation-ended`, `scope-lost`), `ExecutionPolicy` (version `direct-invocation/1` and its values, with validation), `AgentRoute`, and `AgentCatalogIds` with the six catalog Specialist Agent identifiers and their domains and the three Coach Agent identifiers from `docs/architecture/intelligence-agent-catalog.md` (FR-004, FR-007, FR-021, FR-025)

### Options, audit constants, and metrics

- [x] T006 Add `AgentOrchestrationOptions` (sections `AgentOrchestration:Agents:Enabled`, `AgentOrchestration:Execution` with the research R5 development values "3 attempts, 30-second lease, 300-second attempt maximum, 10 history turns, 4,000 question characters, 16,000 advice characters, 1,000 note characters, 5 unfinished invocations, 2-second polling", `AgentOrchestration:Workloads` with `TokenLifetime` and `Clients[]` (`ClientId`, `PublicJwk`, `Enabled`), and `AgentOrchestration:Monitor` with "5 seconds (monitor), 30 seconds (reconciliation), republish bound 60 seconds"), with `IsValid` refusing non-positive values and enabled identifiers that are not catalog Specialist Agents, in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/AgentOrchestrationOptions.cs`; the audit event-type constants and builders of research R12 in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/AgentOrchestrationAuditEvents.cs` (identifiers only, never question, advice, or note text); and the meter `AgentOrchestrationMetrics` (counters of submissions, claims, completions, failures, lease expiries, and scope-loss outcomes, and an observable gauge of queued invocations) in `src/platform/SocAlytics.Platform.Application/Telemetry/AgentOrchestrationMetrics.cs` (FR-005, FR-025, FR-034, FR-034a)

### Contract index

- [x] T007 Add index rows for the three `contracts/agent-orchestration/*/v1` contracts to `contracts/README.md` following the existing `$id` convention, so that `ContractCatalog`, `ContractIndexTests`, and `SchemaMetaValidationTests` in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests` load and validate them and their fixtures; run `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` (FR-036)

### Stores, scope, and transport

- [x] T008 [P] Add `IAgentConversationStore` with its records in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/IAgentConversationStore.cs` and its Dapper implementation in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/AgentConversationStore.cs`: find by owner and idempotency key, insert a conversation, find and lock by id with participants, add a participant conditional on the conversation version, list an owner's conversations of a match newest first, and read whether the conversation's run is still the match's current run (newest run of the latest finalized recording-set version, as the run reads define it) (FR-001–FR-004, FR-010, FR-011)
- [x] T009 [P] Add `IAgentInvocationStore` and `IAgentAttemptStore` with their records in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/IAgentInvocationStores.cs` and their Dapper implementations in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/AgentInvocationStore.cs` and `AgentAttemptStore.cs`: find by owner, conversation, and idempotency key; insert an invocation; lock an invocation; count an owner's invocations "where `state IN ('queued','running')`"; transition invocation state conditional on the prior state and version and increment `progress_version`; set `current_attempt_id`, `ready_occurrence`, and `last_notified_at`; insert an attempt with the next `fencing_token` "strictly increasing per invocation"; lock an attempt; transition an attempt; list active attempts whose lease or deadline passed; list active attempts for the scope check; list queued invocations whose notification is older than a bound; append lineage entries with the next gap-free `sequence`; insert the accepted advice; read a conversation's turns in submission order with a cursor and an invocation's lineage with its advice (FR-007, FR-010, FR-015–FR-024, FR-028–FR-030)
- [X] T010 [P] Add `IAgentWorkloadOperationStore` and `IAgentWorkloadTokenStore` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Workloads/IAgentWorkloadStores.cs` and their Dapper implementations in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/AgentWorkloadStores.cs`: find and insert operation records by (`workload_client_id`, `operation_id`) with `kind`, `request_hash` "SHA-256 of the canonical request", and `outcome`; insert, find by token hash, and delete expired access tokens (FR-014, FR-022; research R2, R4)
- [x] T011 [P] Add the team scope sources for resource kinds `agent-conversation` and `agent-invocation` in `src/platform/SocAlytics.Platform.Infrastructure/AgentOrchestration/AgentScopeSources.cs`, and `IInvocationScopeEvaluator` with `InvocationScopeEvaluator` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/InvocationScopeEvaluator.cs`, which loads the stored owner's access snapshot through `IMemberAccountStore` and grants execution only to "an active owner without a pending password change who is Club Admin or Coach of the team" (FR-013, FR-026; research R6)
- [x] T012 [P] Provision the work-queue stream `AGENT_WORK` on subject `agents.invocations.ready` with the configured duplicate window and the durable pull consumer `agent-workload` in `src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/NatsTopology.cs` and `NatsOptions.cs`; publish outbox messages of type `agent-invocation-ready` to that subject with message id `<invocationId>:<readyOccurrence>` in the existing outbox publication path (`src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/NatsJetStreamTransport.cs`); and let `src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/NatsQueueCredentialIssuer.cs` and its interface `src/platform/SocAlytics.Platform.Application/Registry/QueueCredentials/IQueueCredentialIssuer.cs` issue a user JWT limited to pulling and acknowledging from `agent-workload` (research R3)
- [x] T013 [P] Allow-list the detail keys of `AgentOrchestrationAuditEvents` (`conversationId`, `invocationId`, `attemptId`, `attemptNumber`, `agentId`, `workloadClientId`, `disposition`, `reasonCode`) and the actor kind `agent-workload` in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/PostgresAuditTrail.cs` (FR-034)

### Registration

- [ ] T014 Register the stores, scope sources, `IInvocationScopeEvaluator`, and `AgentOrchestrationMetrics` in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` and `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; bind and validate `AgentOrchestrationOptions`; set the development values explicitly in an `AgentOrchestration` section of `src/platform/SocAlytics.Platform.Api/appsettings.Development.json`, with `Agents:Enabled` "at least `specialist-tactics` and `specialist-video-analysis`" and no workload client (the AppHost and tests configure clients) (FR-005, FR-025)

### Workload authentication

- [ ] T015 Add `IssueAgentWorkloadToken` and `AuthenticateAgentWorkloadRequest` handlers in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Workloads/`: the client credentials grant with a `private_key_jwt` assertion whose audience is the workload token URI, a DPoP proof whose key thumbprint matches the configured client's `PublicJwk`, scope `agent-workload`, an enabled configured client, and the stamp, reusing `IManagerProofValidator`; hashed tokens with `TokenLifetime`; request authentication that checks the token hash, expiry, DPoP binding, and the client's current `Enabled` state on every request; audit credential issuance and refusal with actor kind `agent-workload`; register both handlers in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (FR-014; research R2)
- [ ] T016 Add the authentication scheme `AgentWorkloadDPoP` in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentWorkloadDPoPAuthenticationHandler.cs` (HTTPS required, `DPoP` challenge as for Analyst Managers), the policy `AuthorizationPolicyNames.AgentWorkload` in `src/platform/SocAlytics.Platform.Api/Security/AuthorizationPolicyNames.cs`, the token operation `issueAgentWorkloadToken` (`POST /api/v1/agent-orchestration/workload/token`, form body, OAuth error answers) in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentWorkloadEndpoints.cs` with a `MapAgentWorkloadEndpoints` method, and register the scheme, policy, and mapping in `src/platform/SocAlytics.Platform.Api/Program.cs`, so that member sessions and Analyst Manager tokens never satisfy `AgentWorkload` and workload tokens never satisfy `ActiveMember` or `AnalystManager`; refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` (FR-014, FR-035; contracts/agent-orchestration-api.md)
- [ ] T017 Add the test support `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Support/TestAgentWorkload.cs` (an in-memory ECDSA P-256 workload key that builds client assertions and DPoP proofs, obtains tokens, and calls every workload operation, with overridable claims for negative cases) and `AgentOrchestrationFixture.cs` (a `PlatformApiFactory` configuration with enabled agents, a configured workload client, short test lease and monitor values, and helpers to build a match with a `Completed`, `PartiallyCompleted`, `Failed`, or `Running` run through `ClubHierarchyBuilder`), and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/WorkloadAuthenticationTests.cs`: tokens are issued for a valid assertion and proof; an invalid assertion, a foreign DPoP key, a replayed proof, an unknown client, and a disabled client are refused and audited; disabling a client stops its issued tokens at once; member sessions and Analyst Manager tokens get `401` from workload operations; workload tokens get `401` from member and Analyst Manager operations (FR-014; SC-005)

**Checkpoint**: Contracts, schema, stores, transport, and workload authentication exist; user stories can begin

---

## Phase 2: User Story 1 - Ask a specialist about an analysed match and read the advice (Priority: P1) 🎯 MVP

**Goal**: A Club Admin or Coach creates a conversation for a terminal successful run, adds a specialist, submits a question, the workload claims and completes it, and the owner reads the advice with conditional polling

**Independent Test**: As a Coach of the owning team, create a conversation for a completed run, add `specialist-tactics`, submit a question, let the simulated agent workload execute it, and confirm that polling reports `succeeded` and the turns show the question followed by exactly one accepted advice

### Handlers for User Story 1

- [ ] T018 [P] [US1] Add the conversation handlers in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/`: `CreateAgentConversation` (`TeamPermission.Write` on the match, run in `Completed` or `PartiallyCompleted` of the same match or `409 analysis-run-not-eligible`, idempotency key per the shared `idempotency-key-missing` and `idempotency-key-reused` codes, audit `agent.conversation.created`), `AddAgentConversationParticipant` (owner only, enabled catalog Specialist Agent or `409 agent-not-available`, duplicate `409 participant-exists`, conditional on the conversation version, audit `agent.participant.added`), `ListAvailableAgents` (any active member with Club Admin or a team role), `GetAgentConversation` (owner with `TeamPermission.Read`, every other caller the same not-found answer, `analysisRunIsCurrent`), and `ListAgentConversations` (the caller's own conversations of a match, newest first) (FR-001–FR-005, FR-010, FR-011, FR-013)
- [ ] T019 [P] [US1] Add the invocation handlers in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/`: `SubmitAgentInvocation` (owner with `TeamPermission.Write`; the agent a participant and still enabled; question "1 to `maxQuestionCharacters`" plain text; the owner's unfinished invocations below `maxUnfinishedInvocations` or `429 too-many-unfinished-invocations`; equal request hash replays and a different hash `409 idempotency-key-reused`; in one `IUnitOfWork` scope insert the invocation in `queued` with route, correlation id, `policy_version` `direct-invocation/1` and its resolved values, append the `submitted` lineage entry, and write one `agent-invocation-ready` outbox message per `work-notification/v1`; audit accepted, replayed, and refused submissions; count submissions), `GetAgentInvocation` (owner only, state, attempt count, failure reason, advice once succeeded, `retryAfterSeconds` while unfinished, `progress_version`), and `ListAgentConversationTurns` (owner only, submission order, cursor and limit, advice once succeeded) (FR-006–FR-010, FR-012, FR-013, FR-028)
- [ ] T020 [P] [US1] Add `ClaimAgentInvocation` and `CompleteAgentAttempt` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/`: the claim locks the invocation, records the operation in `agent_workload_operations` (equal hash replays the recorded grant, different hash `409 operation-id-reused`), refuses with `409 claim-obsolete` when the invocation is not queued, has an unexpired active attempt, or has no attempts left (auditing the refused claim), and otherwise in one unit of work creates the attempt (`claimed` then `active`, next fencing token, `lease_expires_at`, `deadline_at = claimed_at + maxAttemptSeconds`), moves the invocation to `running`, appends `claimed`, audits and counts the claim, and returns the `execution-envelope/v1` envelope with the earlier succeeded turns of all participants "newest first up to the configured bound"; completion requires the current attempt, `active`, the presented token, an unexpired lease, and an unreached deadline, otherwise `409 agent-attempt-stale` with its `disposition`, validates advice "1 to `maxAdviceCharacters`" and the execution references, and in one unit of work inserts the single accepted advice, ends the attempt `completed` and the invocation `succeeded`, appends `completed`, records the operation, audits, and counts (FR-015–FR-017, FR-019, FR-021, FR-022, FR-030)
- [ ] T021 [P] [US1] Add `IssueAgentWorkloadQueueCredentials` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Workloads/IssueAgentWorkloadQueueCredentials.cs`: validates the user public key, issues through `IQueueCredentialIssuer` a short-lived JWT limited to the `agent-workload` consumer when `Nats:Authentication:Mode` is `Jwt`, and answers `409 queue-credentials-unavailable` in mode `None` (research R3)

### Registration and endpoints for User Story 1

- [ ] T022 [US1] Register the handlers of T018–T021 in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (FR-035)
- [ ] T023 [US1] Add the member operations `listAvailableAgents`, `createAgentConversation`, `listAgentConversations`, `getAgentConversation`, `addAgentConversationParticipant`, `submitAgentInvocation` (`202`, `Location`, `Retry-After`), `listAgentConversationTurns`, and `getAgentInvocation` in the new `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentConversationEndpoints.cs` with `MapMemberApi()`, `IfMatchHeader`, `ProblemResults`, strong `ETag` and `If-None-Match` answering `304` as in `AnalysisRunEndpoints`, and typed OpenAPI schemas per contracts/agent-orchestration-api.md; map them in `src/platform/SocAlytics.Platform.Api/Program.cs`; refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` as in T016 (FR-008, FR-010–FR-012, FR-035)
- [ ] T024 [US1] Add the workload operations `issueAgentWorkloadQueueCredentials`, `claimAgentInvocation` (`201`, replay `200`), and `completeAgentAttempt` with `RequireAuthorization(AgentWorkload)`, strict JSON bodies, and the stale problem with its `disposition` to `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentWorkloadEndpoints.cs`; refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` as in T016 (FR-015, FR-019, FR-021, FR-035)

### Simulated agent workload

- [ ] T025 [US1] Create the worker project `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SocAlytics.Platform.SimulatedAgentWorkload.csproj` (references no production project) with `Program.cs` that refuses to start unless `DOTNET_ENVIRONMENT` is `Development` or `Test`, `SimulatedAgentWorkloadOptions.cs` (API base address, client id, private key, NATS URL), and `SimulatedAgentWorkloadRunner.cs`, a reusable class that obtains workload tokens and, in `Jwt` mode, queue credentials, pulls `agents.invocations.ready` from the `agent-workload` consumer, claims with a fresh operation id, and completes with the deterministic advice "Simulated advice from `<agentId>`" and the declared references `simulated`, acknowledging each notification after a claim, an obsolete claim, or a completion; add the project to `src/platform/SocAlytics.Platform.slnx` (FR-031)
- [ ] T026 [US1] In `src/platform/SocAlytics.Platform.AppHost/Program.cs` and `SocAlytics.Platform.AppHost.csproj`, generate an ephemeral ECDSA P-256 workload key at start, pass its public JWK as `AgentOrchestration__Workloads__Clients__0__*` with `Enabled=true` to the API, and start `simulated-agent-workload` with `DOTNET_ENVIRONMENT=Development`, the private key, the API reference, and the NATS reference after the API in normal and `e2e` mode (FR-033)

### Tests for User Story 1

- [ ] T027 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/ConversationTests.cs`: Club Admins and Coaches create conversations for `Completed` and `PartiallyCompleted` runs; Viewers get `403`, members without a team role `404`; `Running`, `Failed`, and `Cancelled` runs and a run of another match get `409 analysis-run-not-eligible`; key replay returns the same conversation and key reuse with another run `409`; participants: enabled specialist added, Coach Agent, unknown, and disabled identifiers `409 agent-not-available`, duplicate `409 participant-exists`, stale `If-Match` refused; the agent list shows exactly the enabled specialists; a conversation whose run was superseded stays usable and reports `analysisRunIsCurrent` false (FR-001–FR-005, FR-010, FR-011)
- [ ] T028 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SubmissionTests.cs`: a submission answers `202` with conversation and invocation ids, `Location`, and `Retry-After`, stores a `queued` invocation, and writes exactly one outbox message that validates against `work-notification/v1` and contains no question text; an equal repeat returns the same invocation and a different repeat with the key `409`; non-participant, disabled agent, empty and too long questions, a Viewer owner, and the sixth unfinished invocation are refused without change (FR-006–FR-009, FR-028; SC-002)
- [ ] T029 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/DirectInvocationFlowTests.cs` using `TestAgentWorkload`: claim returns `201` with an envelope that validates against `execution-envelope/v1` and carries earlier succeeded turns of all participants up to the bound; a duplicate claim with a new operation id gets `409 claim-obsolete` and a repeated operation id the same grant; completion makes the invocation `succeeded` with one accepted advice; the invocation and turns reads show it; unchanged reads with `If-None-Match` answer `304`; polling creates no invocation or attempt (FR-010, FR-012, FR-015–FR-017, FR-019; SC-002, SC-003)
- [ ] T030 [US1] Reference `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload` from `src/platform/Tests/SocAlytics.Platform.Integration.Tests/SocAlytics.Platform.Integration.Tests.csproj` and add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadTests.cs`: the runner hosted in-process against the test API and the NATS test container claims and completes a submitted invocation, so the owner reads simulated advice within 30 seconds; the executable refuses to start in `Production` (FR-031, FR-033; SC-001)

**Checkpoint**: User Story 1 is fully functional and testable on its own (MVP)

---

## Phase 3: User Story 2 - Reliable execution through claims, leases, fencing, and retries (Priority: P2)

**Goal**: Leases are renewed, checkpoints recorded, failures retried or ended, expired and overlong attempts fenced and retried within the policy, stale and duplicate calls answered without change, and missing notifications republished

**Independent Test**: Let an attempt's lease expire and confirm expiry, a successful second attempt, and stale answers for the first token; exhaust the retry limit; repeat operations with the same ids and confirm the original outcomes

### Handlers for User Story 2

- [ ] T031 [P] [US2] Add `RenewAgentAttemptLease` and `RecordAgentCheckpoint` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/`: both require the current `active` attempt, the presented token, an unexpired lease, and an unreached deadline, otherwise `409 agent-attempt-stale` with its `disposition`; renewal extends `lease_expires_at` by the policy's lease without passing `deadline_at`; a checkpoint records its operation (replay and `operation-id-reused` as for claims), validates `kind` and a note "1 to `maxCheckpointNoteCharacters`", appends a `checkpoint` lineage entry in order, and increments `progress_version` (FR-018, FR-021, FR-022)
- [ ] T032 [P] [US2] Add `FailAgentAttempt` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/FailAgentAttempt.cs`: requires the current attempt and token as above; records the operation; ends the attempt `failed` with `failed-retryable` or `failed-permanent`; appends `failed`; when retryable and attempts remain, returns the invocation to `queued`, increments `ready_occurrence`, and writes a new `agent-invocation-ready` outbox message in the same unit of work; otherwise ends the invocation `failed` with `execution-failed`, or `attempts-exhausted` and an `exhausted` entry when retryable but no attempt remains; audits and counts (FR-020, FR-021, FR-022)
- [ ] T033 [P] [US2] Add `MonitorAgentAttempts` and `ReconcileAgentWork` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/`: the monitor ends each `active` attempt whose lease expired or whose deadline passed as `expired` (`lease-expired` or `deadline-reached`), fences its token, keeps its lineage, appends the entry, and requeues the invocation with a new outbox message while attempts remain or fails it with `attempts-exhausted`, auditing and counting each outcome, and deletes expired workload access tokens; reconciliation writes a new `agent-invocation-ready` outbox message for every `queued` invocation whose `last_notified_at` is older than the republish bound, idempotently (FR-023, FR-024, FR-029)

### Workers, registration, and endpoints for User Story 2

- [ ] T034 [US2] Add `src/platform/SocAlytics.Platform.Infrastructure/Workers/AgentAttemptMonitorWorker.cs` and `AgentWorkReconciliationWorker.cs` on the existing `WorkerLoop` with the `AgentOrchestration:Monitor` intervals; register the handlers of T031–T033 in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` and the workers in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` (FR-023, FR-029)
- [ ] T035 [US2] Add the workload operations `renewAgentAttemptLease`, `recordAgentCheckpoint`, and `failAgentAttempt` to `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentWorkloadEndpoints.cs`; refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` as in T016 (FR-018, FR-020, FR-035)
- [ ] T036 [US2] Extend `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs` to renew the lease while working, record at least one checkpoint before completing, and honour the question markers of research R10 (`[simulate:fail-once]`, `[simulate:fail-retryable]`, `[simulate:fail-permanent]`, `[simulate:stall]`) (FR-031, FR-032)

### Tests for User Story 2

- [ ] T037 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/AttemptLifecycleTests.cs` using `TestAgentWorkload`: renewals extend the lease but never past the deadline; checkpoints appear in order; retryable failures requeue with a new notification while attempts remain and then fail with `attempts-exhausted`; non-retryable failures fail with `execution-failed`; renewals, checkpoints, completions, and failures with an old token, of a replaced attempt, after expiry, and after the invocation ended get `409 agent-attempt-stale` with `attempt-not-current`, `lease-expired`, `attempt-fenced`, or `invocation-ended` and change nothing; every workload operation repeated with its operation id returns the original outcome and with other content `409 operation-id-reused`; concurrent claims create one active attempt; a completion racing the monitor's expiry leaves exactly one outcome (FR-017–FR-022, FR-030; SC-002, SC-003)
- [ ] T038 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/MonitorAndReconciliationTests.cs` with a `FakeTimeProvider`: an attempt without renewals expires within one lease plus one monitor interval and the invocation is claimed again by a new attempt that completes; an attempt that reaches its deadline is ended like an expired lease; the third expiry fails the invocation with `attempts-exhausted`; a late completion of the expired attempt is stale; a queued invocation whose notification was lost is republished by reconciliation within the republish bound plus one reconciliation interval and a duplicate notification never creates a second active attempt; the published messages contain no question or advice text (FR-023, FR-024, FR-028, FR-029; SC-004)
- [ ] T039 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs`: with the in-process runner, `[simulate:fail-once]` succeeds on the second attempt, `[simulate:fail-retryable]` ends with `attempts-exhausted`, `[simulate:fail-permanent]` ends with `execution-failed`, and `[simulate:stall]` expires the first attempt and succeeds on the second; every succeeded invocation has a checkpoint in its lineage (FR-031, FR-032)

**Checkpoint**: User Stories 1 and 2 work independently

---

## Phase 4: User Story 3 - Lineage, privacy, and fail-closed authorization (Priority: P3)

**Goal**: The owner reads ordered lineage; every other member is refused; scope loss fences active work and fails queued work; audit and telemetry carry no conversation content

**Independent Test**: Read a lineage with checkpoints and a retry; read the conversation as other members; remove the owner's Coach role during an active attempt and confirm fencing, `scope-lost`, stale workload calls, and the audit event

### Implementation for User Story 3

- [ ] T040 [P] [US3] Add `GetAgentInvocationLineage` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/GetAgentInvocationLineage.cs`: owner only, entries in `sequence` order with kind, attempt id and number, time, and bounded details (checkpoint kind and note, failure reason and retryability, declared execution references), the policy version, and the accepted advice (FR-010, FR-013; research R9)
- [ ] T041 [P] [US3] Apply `IInvocationScopeEvaluator` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/ClaimAgentInvocation.cs`, `RenewAgentAttemptLease.cs`, `RecordAgentCheckpoint.cs`, `CompleteAgentAttempt.cs`, `FailAgentAttempt.cs`, and `MonitorAgentAttempts.cs`: a claim for an owner without execution scope answers `409 agent-scope-lost`, starts no attempt, and fails the invocation with `scope-lost`; any later operation or the monitor's check of an active attempt ends it `fenced` with `scope-lost`, rejects its output, fails the invocation with `scope-lost` (never retried), appends the `scope-lost` entry, and answers the caller `409 agent-attempt-stale` with `scope-lost`; audit `agent.attempt.scope-lost` and count it (FR-026, FR-027)

### Registration and endpoints for User Story 3

- [ ] T042 [US3] Register `GetAgentInvocationLineage` in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` and add `getAgentInvocationLineage` with `ETag` and `304` to `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentConversationEndpoints.cs`; refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` as in T016 (FR-010, FR-012, FR-035)

### Tests for User Story 3

- [ ] T043 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/LineageTests.cs`: an invocation with a retryable failure, a retry, checkpoints, and a completion has a gap-free lineage of `submitted`, `claimed`, `checkpoint`, `failed`, `claimed`, `checkpoint`, `completed` with the declared references and exactly one accepted advice; lineage rows and advice cannot be updated or deleted; an unchanged lineage read answers `304` (FR-010, FR-019; SC-003)
- [ ] T044 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/PrivacyTests.cs`: another Coach, a Viewer, and another Club Admin of the team, and members of other teams, get the same `404` for the conversation, its list entry, turns, invocation, and lineage; an owner who keeps only a Viewer role still reads but cannot submit; an owner without a team role or deactivated gets `404` (FR-009, FR-013; SC-005)
- [ ] T045 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/ScopeLossTests.cs`: removing the owner's Coach role or deactivating the owner before the claim makes the claim `409 agent-scope-lost` and the invocation `failed` with `scope-lost`; during an active attempt, the next renewal, checkpoint, completion, or failure, or the monitor's check without any call, fences the attempt, rejects the advice, fails the invocation with `scope-lost` without retry, and later calls get `agent-attempt-stale` with `scope-lost` (FR-026, FR-027; SC-005)
- [ ] T046 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/AuditAndTelemetryTests.cs` with `CapturingLoggerProvider` and a `MeterListener`: one audit event per conversation creation, participant addition, accepted, replayed, and refused submission, granted and refused claim, completion, failure, lease expiry, exhaustion, and scope-loss outcome, with the member or `agent-workload` actor, ids, and outcome; the counters and the queued gauge move as expected; no audit event, captured log, metric tag, or outbox message contains a question, advice text, or checkpoint note used in the test (FR-034, FR-034a; SC-006)

**Checkpoint**: All user stories are independently functional and tested

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Layering evidence, architecture truth for the merged state, and end-to-end validation

- [ ] T047 Add rules to `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/` that the `AgentOrchestration` namespaces follow the existing layer dependencies, that no other functional area references Agent Orchestration persistence types, and that `SocAlytics.Platform.SimulatedAgentWorkload` references no production project (constitution Principles II and III; research R1, R10)
- [ ] T048 Make `docs/architecture/` describe the implemented state: re-read `docs/architecture/intelligence-and-agents.md` (Agent Orchestration Authority, Durable State And Transaction Boundaries, Client, Transport, And Runtime Protocol, Authorization, Lineage, And Lifecycle, Post-Analysis Match Triage), `intelligence-agent-catalog.md` (introduction, Implementation Waves), `platform-implementation.md` (Agent Runtime, API And Identity), `security-and-data-governance.md` (Audit Events, `GOV-CRED-003`, `DAT-008`), `client-applications.md`, `overview.md`, `tenancy-and-technology.md`, and `docs/architecture/README.md`, and correct every statement this feature makes false, including the planned, future-tense, "will", "decided and not yet implemented", and "planned but not implemented" wording about Agent Orchestration (the plan's Architecture Updates list examples, not the whole job), while keeping Hermes, consultations, Coach Agents, MCP tools, lifecycle operations, the Web UI, and production values explicitly planned or unresolved; update `AGENTS.md` "Current State" and `src/platform/README.md` so they name the Agent Orchestration direct-invocation evidence and the Development and Test simulated agent workload (constitution Principle I)
- [ ] T049 Run the validation in `specs/20261017-204332-agent-orchestration-invocations/quickstart.md`: `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, `dotnet test src/platform/SocAlytics.Platform.slnx --no-build`, and `node .github/scripts/check-markdown.mjs`, and walk through the local steps 1 to 10 with the AppHost and the simulated agent workload; fix any failure in the files this feature changed (FR-033; SC-001–SC-006)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: No dependencies; blocks all user stories.
- **User Story 1 (Phase 2)**: Depends on Phase 1.
- **User Story 2 (Phase 3)**: Depends on Phase 1 and on the claim, completion, workload endpoints, and simulated workload of User Story 1 (T020, T024, T025, T030).
- **User Story 3 (Phase 4)**: Depends on Phase 1 and on the handlers and endpoints of User Stories 1 and 2 (T018–T024, T031–T035).
- **Polish (Phase 5)**: Depends on all user stories.

### Within Each Phase

- T001–T005 run in parallel; T006 follows because its options validation uses the catalog identifiers of T005; T007 indexes the contracts; T008–T013 run in parallel; T014 registers what they created; T015, T016, and T017 run in order (handlers, endpoints, tests).
- T018–T021 run in parallel; T022 registers them; T023 and T024 run in order because both refresh the OpenAPI snapshot; T025 and T026 build and start the simulated workload; T027–T029 run in parallel; T030 changes the test project file and runs the simulated workload.
- T031–T033 run in parallel; T034 adds the workers and registers the handlers; T035 exposes the operations; T036 extends the simulated workload; T037–T039 run in parallel.
- T040 and T041 run in parallel; T042 registers and exposes the lineage read; T043–T046 run in parallel.
- T047, T048, and T049 run in order.

### Parallel Opportunities

```text
Phase 1, group 1: T001 notification contract | T002 envelope contract | T003 attempt-report contract | T004 migration | T005 domain
Phase 1, group 2: T008 conversation store | T009 invocation and attempt stores | T010 workload stores | T011 scope sources and evaluator | T012 NATS topology and credentials | T013 audit allow-list
Phase 2, group 1: T018 conversation handlers | T019 invocation handlers | T020 claim and completion | T021 queue credentials
Phase 2, group 2: T027 conversation tests | T028 submission tests | T029 direct flow tests
Phase 3, group 1: T031 renew and checkpoint | T032 failure | T033 monitor and reconciliation
Phase 3, group 2: T037 attempt lifecycle tests | T038 monitor and reconciliation tests | T039 simulated behavior tests
Phase 4, group 1: T040 lineage read | T041 scope loss
Phase 4, group 2: T043 lineage tests | T044 privacy tests | T045 scope-loss tests | T046 audit and telemetry tests
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Foundational.
2. Complete Phase 2: User Story 1.
3. Validate with the US1 tests and quickstart steps 1 to 8.

### Incremental Delivery

1. Foundational → User Story 1 (MVP: submit, execute, and read advice end to end).
2. User Story 2 → leases, checkpoints, failures, expiry, retries, stale dispositions, and reconciliation.
3. User Story 3 → lineage read, privacy, scope loss, audit, and telemetry evidence.
4. Polish → layering rules, architecture and `AGENTS.md` truth for the merged state, full validation.
