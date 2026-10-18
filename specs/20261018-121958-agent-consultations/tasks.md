# Tasks: Delegated Coach-to-Specialist Consultations

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md),
[consultation interfaces](contracts/consultation-interfaces.md), and [quickstart.md](quickstart.md).

**Tests**: Required by FR-038 and FR-039 and the validation guide. Each implementation task includes its directly
related tests (platform tests in the named test project, Hermes tests in `src/agents/hermes/tests/`, Web UI tests in
`src/clients/web/tests/`), so a task never lands deliberately failing tests. Hermes tests use only the scripted
provider and the fakes; no test calls a model service or needs a secret.

**Organization**: Contract releases first, then the shared Domain rules, migration, Hermes contract copies, and the
catalog kind, then one phase per user story in priority order, then cross-process evidence, documentation, and final
acceptance.

## Format And Boundaries

- Tasks use `- [ ] Tnnn [P?] [USn?] Description`.
- Consecutive unchecked `[P]` tasks under one heading run in parallel, each from the same commit; their predicted file
  changes must be disjoint and neither may depend on the other.
- This feature changes code that the dependent features create: Agent Orchestration and its `Catalog` folder in the
  platform layers, `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/`, `src/agents/hermes/`, and the Web
  UI's `triage` and `agent-catalog` feature folders. The MCP server and the agent tool operations stay unchanged.
- One forward-only migration `NNNN_agentorchestration_consultations.sql`, numbered at implementation time as the next
  free number on an up-to-date default branch. The new table `agent_consultations` is versioned, so
  `PersistedTableClassifications` is unchanged.
- The OpenAPI snapshot is refreshed only by T022 with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`,
  never in the verification environment; the Web UI's Kiota client is generated from it by the Web UI build.
- Run Hermes checks from `src/agents/hermes/` with the environment's locked sequence:
  `uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`,
  `uv build --wheel --no-build-isolation`, `uv run --no-sync pytest`. No Hermes dependency changes.
- `src/agents/hermes/tests/conftest.py` and `src/agents/hermes/tests/fakes/` are changed only by T023; other Hermes
  tasks keep their fixtures in their own test files or in new files under `src/agents/hermes/tests/scripts/`.
- `src/clients/web/tests/e2e/support/simulatedApi.ts` is changed only by T033.
- Consultation questions, decision support, responses, influence notes, and advice text never appear in logs, audit
  events, telemetry, or work notifications (FR-036, SC-006); every task that adds a log field, audit detail, or metric
  tag asserts this in its tests.

## Phase 1: Setup (contract releases)

**Purpose**: Publish the new and backward-compatible contract releases every later task builds on.

### Contract releases

- [ ] T001 [P] Add the new contract `contracts/agent-catalog/consultation-policy/v1/` with
  `consultation-policy.schema.json` (`x-socalytics-version` `1.0.0`), `releases/1.0.0.schema.json`, and fixtures: a
  closed object with `schemaVersion` `1.0.0`, `kind` `consultation-policy`, kebab-case `id` (3–100), SemVer `version`,
  `allowedSpecialists` "1–6 unique Specialist Agent identifiers", `maxConsultationsPerInvocation` "Integer 1–6",
  `maxQuestionCharacters` "Integer 1–2,000", `maxDecisionSupportCharacters` "Integer 1–1,000", and `onUnavailable`
  "`continue` or `fail`"; valid fixtures `examples/valid/consultation-policy-continue.json` and
  `consultation-policy-fail.json`, invalid fixtures `examples/invalid/consultation-policy-unknown-behavior.json`,
  `consultation-policy-bound-7.json`, `consultation-policy-duplicate-specialist.json`, and
  `consultation-policy-extra-property.json` (FR-001, research R1).
- [ ] T002 [P] Add release 1.1.0 of `contracts/agent-catalog/agent-version/v1/`: the optional
  `artifacts.consultationPolicy { id, version, digest }` with the existing reference shape, `x-socalytics-version`
  `1.1.0`, `releases/1.1.0.schema.json`, a valid fixture `examples/valid/agent-version-coach-consulting.json`, and an
  invalid fixture `examples/invalid/agent-version-consultation-policy-without-digest.json`; release 1.0.0 documents
  stay valid (FR-002, research R1).
- [ ] T003 [P] Add release 1.2.0 of `contracts/agent-orchestration/work-notification/v1/`: widen `route` to
  `user-to-specialist`, `user-to-coach`, `coach-to-specialist`, set `x-socalytics-version` `1.2.0`, add
  `releases/1.2.0.schema.json`, a valid fixture `examples/valid/notification-coach-to-specialist.json`, and an invalid
  fixture `examples/invalid/notification-route-specialist-to-specialist.json` (FR-005, research R2).
- [ ] T004 [P] Add release 1.3.0 of `contracts/agent-orchestration/execution-envelope/v1/` as in
  [contracts/consultation-interfaces.md](contracts/consultation-interfaces.md): `route` adds `coach-to-specialist`;
  `agent.artifacts[].kind` adds `consultation-policy`; optional `consultations` ("0–6, sequence order" of
  `consultationId`, `sequence`, `agentId`, `agentVersion`, `question`, `decisionSupport`, `state` `pending` |
  `answered` | `unavailable` | `ended`, `response` for answered only, `unavailableReason` for unavailable only) and
  optional `consultation` (`consultationId`, `parentInvocationId`, `requestingAgentId`, `decisionSupport`,
  `initiatingQuestion`, `knownEvidence` of `{ callId, toolId, outcome, evidenceReferences }`); set
  `x-socalytics-version` `1.3.0`, add `releases/1.3.0.schema.json`, valid fixtures
  `examples/valid/envelope-consulting-parent.json` and `envelope-coach-to-specialist-child.json`, and an invalid fixture
  `examples/invalid/envelope-consultation-state-unknown.json` (FR-017, research R2 and R7).
- [ ] T005 [P] Add release 1.3.0 of `contracts/agent-orchestration/attempt-report/v1/`: the optional
  `completion.consultationInfluence` array "0–6" of `{ consultationId, use: adopted | partially-adopted | not-adopted |
  unavailable, agreement: agrees | conflicts | not-applicable, note: string 1–300 }`, `x-socalytics-version` `1.3.0`,
  `releases/1.3.0.schema.json`, a valid fixture `examples/valid/completion-consultation-influence.json`, a still-valid
  `examples/valid/completion-1.2.0-shape.json`, and invalid fixtures `examples/invalid/influence-use-ignored.json` and
  `influence-note-too-long.json` (FR-019, research R8).
- [ ] T006 [P] Add the new contract `contracts/agent-orchestration/consultation-request/v1/` with
  `consultation-request.schema.json` (`x-socalytics-version` `1.0.0`), `releases/1.0.0.schema.json`, and fixtures:
  closed object with `operationId` `^[A-Za-z0-9._\-]{1,128}$`, `fencingToken` string, `agentId` kebab-case 3–100,
  `question` "string 1–2000", `decisionSupport` "string 1–1000", `knownEvidenceCallIds` "array 0–10 unique of
  `^[A-Za-z0-9._\-]{1,128}$`"; valid `examples/valid/request-tactics.json`, invalid
  `examples/invalid/request-extra-route.json` and `request-eleven-call-ids.json` (FR-004, research R2).

### Contract index

- [ ] T007 Update `contracts/README.md` index rows for the two new contracts and the four new releases, and any contract
  list or catalog the `src/platform/Tests/SocAlytics.Platform.Contracts.Tests` project keeps, then run
  `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` (FR-037).

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared Domain rules, persistence, Hermes contract copies, and the catalog kind that every story needs.

### Domain rules, migration, and Hermes contract copies

- [ ] T008 [P] Add the Domain rules in `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/AgentRoute.cs`
  (`coach-to-specialist` with wire name) and the new folder
  `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/Consultations/`: consultation states `pending`,
  `answered`, `unavailable`, `ended` with "`pending → answered | unavailable | ended`; terminal states never change";
  the parent-state rules of [data-model.md](data-model.md#invocation-states-parents-with-consultations); the request
  refusal order and reason codes of research R4 (`route-not-supported`, `not-allowed`, `agent-not-available`,
  `not-consultable`, `tool-policy-not-inherited`, `limit-reached`, and the `consultation-request-invalid` violations);
  and the influence rules of research R8 (violations `missing`, `duplicate`, `unknown-consultation`,
  `unavailable-mismatch`, `not-a-parent`); with unit tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/ConsultationRulesTests.cs`
  (FR-006, FR-010, FR-019, research R4, R5, R8).
- [ ] T009 [P] Add the migration
  `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_agentorchestration_consultations.sql`
  exactly as [data-model.md](data-model.md): table `agent_consultations` (`sequence` "1..`maxConsultationsPerInvocation`
  of the pinned policy, unique per parent", `child_invocation_id` unique, `state` check `pending`, `answered`,
  `unavailable`, `ended`, `outcome_reason` null, `known_evidence` jsonb, index (`parent_invocation_id`, `state`),
  `socalytics.attach_version_trigger`); `agent_invocations` columns `parent_invocation_id`, `consultation_id` (unique),
  `inherited_tool_policy_digest`, the all-or-none check with route `coach-to-specialist`, widened `route`, `state`
  (`waiting-for-consultations`, `synthesizing`), and `failure_reason` (`consultation-unavailable`, `parent-ended`)
  checks, and index (`parent_invocation_id`); `agent_attempts.end_reason` adds `parent-ended` and
  `consultation-unavailable`; `agent_invocation_pins.kind` adds `consultation-policy`; `agent_lineage_entries.kind`
  adds `consultation-requested`, `consultation-answered`, `consultation-unavailable`, `consultation-ended`,
  `consultation-created`; `agent_workload_operations.kind` adds `consult`; `agent_accepted_advice` adds
  `consultation_influence jsonb null`; `agent_catalog_artifacts.kind` adds `consultation-policy`; with a structure test
  in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/ConsultationSchemaTests.cs`
  (FR-005, FR-010, research R3).
- [ ] T010 [P] Copy the released schemas `consultation-policy` 1.0.0, `agent-version` 1.1.0, `work-notification`
  1.2.0, `execution-envelope` 1.3.0, `attempt-report` 1.3.0, and `consultation-request` 1.0.0 into
  `src/agents/hermes/src/socalytics_hermes/contracts/` with `src/agents/hermes/tools/sync_contracts.py`, and extend
  `src/agents/hermes/tests/test_contract_copies.py` to assert they equal the repository releases (research R10).

### Catalog kind

- [ ] T011 Add the `consultation-policy` kind to the catalog: `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/Catalog/AgentCatalogRules.cs`
  (violation codes `consultation-target-invalid`, `consultation-policy-not-allowed`, `consultation-route-missing`; the
  safety-policy route rule accepts `coach-to-specialist` in both groups), the kind's registration, reads, and pins in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Catalog/` and its Dapper store in
  `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/Catalog/`, the embedded schema
  validation of `agent-version` 1.1.0, the document schema in
  `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentCatalogDocumentContracts.cs`, the pinned-artifact read
  returning the policy (kind `consultation-policy`), and the audit of its registration; with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/ConsultationPolicyTests.cs` for
  registration, every new violation, idempotent and conflicting registration, and the pinned-artifact read (FR-001 to
  FR-003, research R1).

**Checkpoint**: contracts, Domain rules, schema, Hermes copies, and the catalog kind exist.

---

## Phase 3: User Story 1 - A Coach Agent consults a Specialist and synthesizes the answer (Priority: P1) 🎯 MVP

**Goal**: A consulting Coach Agent version requests a consultation, the child runs as a Specialist invocation, its
answer resumes the parent, and the parent completes with a checked influence declaration visible in the owner's reads.

**Independent Test**: The User Story 1 independent test of [spec.md](spec.md) with Hermes' scripted provider, and the
same flow with the simulated agent workload's `[simulate:consult:specialist-tactics]` marker in the platform tests.

### Consulting submission and Hermes artifacts and rendering

- [ ] T012 [P] [US1] Make `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/SubmitAgentInvocation.cs`
  pin a Coach Agent version's consultation policy as `agent_invocation_pins` kind `consultation-policy` and record
  `consulting-invocation/1` for such versions (as `direct-invocation/1` with a 900-second attempt maximum), add the
  options `AgentOrchestration:Execution:ConsultingParent` and `AgentOrchestration:Execution:Consultation`
  (`consultation-invocation/1`: "3 attempts, 30-second lease, 300-second attempt maximum, 10 history turns, 16,000
  advice and 1,000 note characters") in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/AgentOrchestrationOptions.cs`
  and `src/platform/SocAlytics.Platform.Api/appsettings.Development.json`, and count only invocations with
  `parent_invocation_id IS NULL` toward the owner's unfinished-invocation limit; with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/ConsultingSubmissionTests.cs`
  (FR-002, FR-011a, research R1, R6).
- [ ] T013 [P] [US1] Extend `src/agents/hermes/src/socalytics_hermes/artifacts.py` for the `consultation-policy` kind
  and the `coach-to-specialist` route as research R10 (consulting Coach Agent versions require the safety policy route
  `coach-to-specialist`; children require a Specialist version without a consultation policy, a safety policy listing
  `coach-to-specialist`, and the envelope's `consultation`; violations end the attempt as `incompatible-artifact`, other
  routes stay `unsupported-route`), with tests in `src/agents/hermes/tests/test_consultation_artifacts.py` (FR-022,
  FR-024).
- [ ] T014 [P] [US1] Extend `src/agents/hermes/src/socalytics_hermes/rendering.py` with `hermes-coach-section/2`
  (allowed Specialists, remaining bound, bounded questions, synthesis without hiding disagreement, the influence
  declaration, never consulting a Coach Agent), `hermes-consultation-section/1` (requesting Coach Agent, answer returns
  only to it, decision support, known evidence references, the member's question as context, no consultation or address
  of another agent, the consultation question as the user message), attributed messages for the envelope's earlier
  consultations, and the declared runtime strings of research R10; it reads the consultation policy as the validated
  pinned document (a mapping from the pinned-artifact read), not through types added by T013; with tests in
  `src/agents/hermes/tests/test_consultation_rendering.py` (FR-024, FR-025, FR-027).

### Platform: request, claim, outcomes, completion, reads, audit

- [ ] T015 [US1] Add `requestAgentConsultation` (`POST /api/v1/agent-orchestration/attempts/{attemptId}/consultations`,
  policy `AgentWorkload`) in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Consultations/RequestAgentConsultation.cs`,
  its store in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/`, and its mapping in
  `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentWorkloadEndpoints.cs` with every response of
  [contracts/consultation-interfaces.md](contracts/consultation-interfaces.md) in the endpoint metadata: validate the
  `consultation-request` 1.0.0 body; apply the operation-identifier replay and conflict rules (kind `consult`) and the
  current-attempt, token, lease, and deadline checks; and, for a valid request from a consulting `user-to-coach`
  attempt, in one unit of work with the parent row locked, insert the consultation (next `sequence`, `known_evidence`
  from the attempt's `tool-call` lineage entries), the child invocation (route `coach-to-specialist`, the parent's
  `conversation_id`, `team_id`, `match_id`, `analysis_run_id`, `owner_member_id`, `correlation_id`, the parent's
  pinned tool-policy digest as `inherited_tool_policy_digest`, `consultation-invocation/1`, and the pins of the
  Specialist's enabled version and artifacts), lineage entries `consultation-requested` (parent) and
  `consultation-created` (child), the `agent-invocation-ready` outbox message (`work-notification` 1.2.0), set the parent
  to `waiting-for-consultations`, increment its `progress_version`, and answer `201` (replay `200`); with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/RequestConsultationTests.cs`
  for creation, inheritance, pins, outbox content (identifiers only), idempotent replay, and conflicting reuse
  (FR-004, FR-005, FR-007, FR-009, SC-003, research R4).
- [ ] T016 [US1] Extend the claim in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/` to
  return `execution-envelope` 1.3.0 with `consultations` for parents and `consultation` (with `initiatingQuestion` and
  `knownEvidence`) for children, the child's consultation question as `question` and the conversation's earlier
  succeeded top-level turns as `history`, and to move a claimed parent whose consultations are all resolved to
  `synthesizing`; add `readAgentConsultationOutcomes`
  (`POST /api/v1/agent-orchestration/attempts/{attemptId}/consultation-outcomes`) in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Consultations/ReadAgentConsultationOutcomes.cs` and
  `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentWorkloadEndpoints.cs` with the pinned-artifact read's
  stale rules; with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/ConsultationEnvelopeTests.cs`
  (FR-017, FR-018, research R7).
- [ ] T017 [US1] Add child outcome delivery in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Consultations/ConsultationOutcomeDelivery.cs`, called
  by the child's completion in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/`: in the
  child's completion unit of work, lock the parent before the child, mark the consultation `answered`, append
  `consultation-answered` to the parent's lineage, and when no consultation is `pending` move the parent to
  `synthesizing` (attempt active) or `queued` with a new `ready_occurrence` and outbox message (no attempt, attempts
  left); make `completeAgentAttempt` accept `attempt-report` 1.3.0, refuse with `409 consultations-pending` and
  `409 consultation-influence-invalid` (`violations`) using the Domain rules of T008, and store a valid declaration in
  `agent_accepted_advice.consultation_influence` and the `completed` lineage entry; with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/ConsultationCompletionTests.cs`
  for answered delivery, concurrent child completions, synthesizing, every influence refusal with nothing recorded and
  the attempt active, and acceptance (FR-012, FR-019, FR-020, SC-005, research R5, R8).
- [ ] T018 [US1] Extend the owner reads in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/`
  and their API schemas in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/`: `listAgentConversationTurns`
  lists only invocations with `parent_invocation_id IS NULL` and adds `consultations`; `getAgentInvocation` and
  `getAgentInvocationLineage` add `consultations` and the states `waiting-for-consultations` and `synthesizing`; before
  the parent has ended each consultation shows only `consultationId`, `sequence`, `agentId`, `agentDisplayName`,
  `agentVersion`, `question`, `decisionSupport`, `knownEvidence`, `state`, `childInvocationId`, and `childState`, and
  once it has succeeded or failed also `response`, `evidence` (the child's `tool-call` entries), `executionReferences`,
  `unavailableReason`, and `influence`; child invocation and lineage reads answer `404` before the parent ended and then
  add `parentInvocationId` and `consultationId`; every read re-evaluates authorization and keeps `404` for every other
  member; with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/ConsultationReadTests.cs`
  (FR-009, FR-020, FR-021, SC-003, research R9).
- [ ] T019 [US1] Add the audit events `agent.consultation.requested` (`created`, `replayed`, `refused` with the reason
  code) and `agent.consultation.resolved` (`answered`, `unavailable`, `ended`) in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/AgentOrchestrationAuditEvents.cs` and emit them from
  the handlers of T015 and T017; make the existing claim, completion, and failure audit events of children carry route
  `coach-to-specialist` with the parent invocation and consultation identifiers; add the counter
  `agent_orchestration.consultations` (tags `outcome`, `agent_id`) and
  the gauge `agent_orchestration.waiting_parents` in
  `src/platform/SocAlytics.Platform.Application/Telemetry/AgentOrchestrationMetrics.cs`; with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/ConsultationAuditTests.cs`
  asserting identifiers and reason codes only and no question, decision support, response, note, or advice text
  (FR-036, SC-006, research R14).

### Simulated workload and seed

- [ ] T020 [P] [US1] Extend `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/` to execute
  `coach-to-specialist` children like direct Specialist invocations (honoring the existing `[simulate:*]` markers at
  the start of the consultation question) and to interpret `[simulate:consult:<specialistId>]` and
  `[simulate:consult:<specialistId>:<behavior>]` on Coach Agent questions (profile checkpoint, one consultation whose
  question starts with `[simulate:<behavior>]` when given, outcome polling, completion with a valid influence
  declaration); with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/SimulatedConsultationTests.cs`
  that register their own consulting Coach Agent version and prove the end-to-end flow with the simulated workload
  (FR-030, research R12).
- [ ] T021 [P] [US1] Add the consultation step to
  `src/platform/SocAlytics.Platform.Infrastructure/AgentOrchestration/Catalog/AgentCatalogSeeder.cs` with records under
  `src/platform/SocAlytics.Platform.Infrastructure/AgentOrchestration/Catalog/Seed/consultations/` exactly as research
  R11 (three consultation policies 0.1.0 with the allowed Specialists of the "Typical SA consultations" column,
  `maxConsultationsPerInvocation` 3, `maxQuestionCharacters` 1,000, `maxDecisionSupportCharacters` 500, `onUnavailable`
  `continue`; next minor safety-policy versions with `coach-to-specialist`; Coach prompt templates 0.2.0 with the
  drafts' consultation paragraph restored verbatim; agent versions 0.2.0 of the three Coach Agents and the two seeded
  Specialists, registered, approved, and enabled), applied in Development and Test only when no `consultation-policy`
  artifact exists; with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/ConsultationSeedTests.cs` for
  the records, the restored paragraph, enablement, the skip when a policy exists, and no seed outside Development and
  Test (FR-029, research R11).

### OpenAPI snapshot

- [ ] T022 [US1] Refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
  and extend the Host tests in `src/platform/Tests/SocAlytics.Platform.Host.Tests/` to assert the two workload
  operations, their typed request and problem responses, the consultation fields of the member reads, and the
  `consultation-policy` document schema (FR-037).

### Hermes: platform client

- [ ] T023 [US1] Add `requestAgentConsultation` and `readAgentConsultationOutcomes` to
  `src/agents/hermes/src/socalytics_hermes/platform/client.py` (path allowlist), the settings
  `SOCALYTICS_HERMES_MAX_WAITING_ATTEMPTS` "8 (1–64)" and `SOCALYTICS_HERMES_CONSULTATION_POLL_SECONDS` "2 (1–30)" to
  `src/agents/hermes/src/socalytics_hermes/configuration.py`, and consultation support (request, outcomes, child
  envelopes, influence checks) to `src/agents/hermes/tests/conftest.py` and `src/agents/hermes/tests/fakes/`; with
  tests in `src/agents/hermes/tests/test_platform_client.py` and `test_configuration.py` (FR-022, FR-023, research R10).

### Hermes: consultation execution

- [ ] T024 [US1] Add `src/agents/hermes/src/socalytics_hermes/consultations.py` (Hermes-local function tools
  `request_consultation` and `declare_consultation_influence`, offered only to Coach Agent attempts with a pinned
  consultation policy; local refusals for a target outside the list or the bound counting the envelope's consultations;
  operation identifiers `hermes-<attemptId>-consult-<n>`; `consultation-requested` and `consultation-refused`
  checkpoints with content-free notes) and the influence check in `src/agents/hermes/src/socalytics_hermes/advice.py`
  (one extra model turn for a missing or invalid declaration, then `advice-invalid`, retryable), with tests in
  `src/agents/hermes/tests/test_consultation_function.py` and `test_consultation_influence.py` (FR-022, FR-026,
  FR-028).
- [ ] T025 [US1] Extend `src/agents/hermes/src/socalytics_hermes/execution.py`, `runner.py`, and `logging.py`: send all
  consultation requests of one model turn, move the attempt to the waiting pool (separate from the execution slots,
  bounded by `SOCALYTICS_HERMES_MAX_WAITING_ATTEMPTS`, no further work fetched while full), renew the lease and poll
  `readAgentConsultationOutcomes` within the deadline margin, record `consultations-resolved`, reacquire an execution
  slot, return attributed results or unavailable notices to the model, dispatch `coach-to-specialist` children with
  the consultation rendering, and log `consultationId` and `consultationOutcome` only; with tests in
  `src/agents/hermes/tests/test_consultation_waiting.py` and `test_consultation_child.py`, including a single-slot
  instance executing a child while its parent waits and log records without consultation content (FR-023, FR-024,
  FR-028, research R10).

**Checkpoint**: User Story 1 works end to end with the simulated workload and with Hermes against the fakes.

---

## Phase 4: User Story 2 - Only allowed, bounded, non-nested consultations are created (Priority: P2)

**Goal**: Every disallowed, looping, over-bound, or stale request is refused and creates nothing.

**Independent Test**: The User Story 2 independent test of [spec.md](spec.md).

### Refusals

- [ ] T026 [P] [US2] Complete the refusal checks of `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Consultations/RequestAgentConsultation.cs`
  in the order of research R4 (stale disposition; `route-not-supported` for direct Specialist attempts, children, and
  non-Specialist targets including Coach Agent identifiers; scope loss fencing the parent attempt and failing the parent
  `scope-lost` with no child; `not-allowed`; `agent-not-available`; `not-consultable`; `tool-policy-not-inherited`;
  `limit-reached`; `400 consultation-request-invalid` for empty or too long question or decision support against the
  pinned policy and known-evidence identifiers that are not answered `tool-call` entries of the same attempt), each
  audited as refused; with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/ConsultationRefusalTests.cs`
  asserting for every refusal that no consultation, child invocation, waiting edge, lineage entry, or outbox message
  exists (FR-006, FR-008, FR-009, SC-002, research R4).
- [ ] T027 [P] [US2] Add Hermes refusal tests in `src/agents/hermes/tests/test_consultation_refusals.py`: local refusals
  make no platform call; every platform refusal reason returns to the model as the function result with a
  `consultation-refused` checkpoint and the attempt continues; a stale answer to the request abandons the attempt
  without a report; Specialist and child attempts are never offered the function (FR-022, research R10).

**Checkpoint**: User Stories 1 and 2 work; loop prevention and least privilege are proven.

---

## Phase 5: User Story 3 - Consultations survive restarts and fail explicitly (Priority: P3)

**Goal**: Waiting edges and accepted child outcomes survive restarts; unavailable consultations follow the pinned
policy; parents that end take their children with them; scope loss fails the tree.

**Independent Test**: The User Story 3 independent test of [spec.md](spec.md).

### Recovery and explicit failure

- [ ] T028 [P] [US3] Implement the remaining transitions of research R5 in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/` (lease expiry, deadline, failure, and
  monitor handling) and `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Consultations/ConsultationOutcomeDelivery.cs`:
  a parent attempt ending retryably with a pending consultation leaves the parent `waiting-for-consultations` without
  attempt and notification and queues it once all are resolved, or fails it `attempts-exhausted` when none are left; a
  child failing `attempts-exhausted` or non-retryably (including `agent-not-available` at claim) marks the consultation
  `unavailable` with the reason and applies `onUnavailable` (`continue` resolves; `fail` fails the parent
  `consultation-unavailable` and fences its attempt with end reason `consultation-unavailable`); a failing parent ends
  every unresolved child (`parent-ended`, attempt fenced, consultation `ended`, never retried, later calls stale
  `invocation-ended`); scope loss at any parent or child operation, claim, or monitor check fences every active attempt
  of the tree and fails the parent and unresolved children `scope-lost`; every unavailable or ended consultation emits
  `agent.consultation.resolved` and every parent failure with `consultation-unavailable` and child failure with
  `parent-ended` emits the existing invocation failure audit event with that reason; with tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Consultations/ConsultationRecoveryTests.cs`,
  `ConsultationUnavailableTests.cs`, and `ConsultationScopeLossTests.cs` using the simulated workload's markers, proving
  restart recovery without repeated consultations and both policy behaviors (FR-013 to FR-016, FR-036, SC-004, research R5).
- [ ] T029 [P] [US3] Add Hermes recovery tests in `src/agents/hermes/tests/test_consultation_recovery.py`: a resumed
  parent attempt receives earlier consultations from the envelope as attributed results, counts them against the
  bound, and completes without a second request; a stale answer while waiting abandons the attempt; stopping Hermes
  while an attempt waits reports `runtime-stopping`; an unavailable consultation reaches the model as unavailable and
  must be declared `unavailable` (FR-023, FR-026, FR-027).

**Checkpoint**: User Stories 1 to 3 work; restarts and failures are proven.

---

## Phase 6: User Story 4 - Members see consultations and their influence in the Web UI (Priority: P4)

**Goal**: The triage views show consulting and synthesizing turns and each consultation with attribution, response,
evidence, and influence; the Agent Catalog area administers consultation policies.

**Independent Test**: The User Story 4 independent test of [spec.md](spec.md).

### Web UI adapters and catalog area

- [ ] T030 [P] [US4] Extend `src/clients/web/src/platform/models/agents.ts` and
  `src/clients/web/src/platform/adapters/agentConversationAdapter.ts` with the states `waiting-for-consultations` and
  `synthesizing`, the `Consultation` model of research R9, child references, and the consultation lineage kinds
  (unknown values stay raw), with tests in
  `src/clients/web/tests/unit/platform/adapters/agentConversationAdapter.test.ts` (FR-031 to FR-033, FR-035).
- [ ] T031 [P] [US4] Add consultation policies to the Agent Catalog area: `src/clients/web/src/platform/models/agentCatalog.ts`,
  `src/clients/web/src/platform/adapters/agentCatalogAdapter.ts`, `src/clients/web/src/features/agent-catalog/catalogLimits.ts`
  and `outcomeText.ts` (the three new violation codes), new
  `src/clients/web/src/features/agent-catalog/artifacts/ConsultationPolicyView.tsx` and `ConsultationPolicyForm.tsx`,
  the kind in `ArtifactPage.tsx`, `ArtifactCreatePage.tsx`, and `ArtifactsPage.tsx`, the optional reference for Coach
  Agent versions in `AgentVersionPage.tsx` and `AgentVersionCreatePage.tsx`, the `coach-to-specialist` wording in
  `artifacts/SafetyPolicyView.tsx`, and keys in `src/clients/web/src/i18n/en/agentCatalog.json`; with unit tests under
  `src/clients/web/tests/unit/features/agent-catalog/` and
  `src/clients/web/tests/unit/platform/adapters/agentCatalogAdapter.test.ts` including keyboard operation and the
  automated accessibility checks (FR-034, FR-035).

### Triage views

- [ ] T032 [US4] Extend `src/clients/web/src/features/triage/turnPresentation.ts` (worded `waiting-for-consultations`
  with each requested Specialist and its state, worded `synthesizing`, polling continues), add
  `src/clients/web/src/features/triage/ConsultationList.tsx` rendered below the advice by `AdviceCard.tsx` (attribution
  by name and identifier, question, response with evidence references or worded unavailable or ended reason, adoption,
  agreement, and note, a conflict marked as a disagreement, all platform text rendered as text), and extend
  `LineagePanel.tsx` (worded consultation kinds, opening a child lineage) and
  `src/clients/web/src/i18n/en/triage.json`; with unit tests under `src/clients/web/tests/unit/features/triage/`
  including announcements, keyboard operation, and the automated accessibility checks (FR-031 to FR-033, FR-035,
  SC-005).

### Browser tests

- [ ] T033 [US4] Extend `src/clients/web/tests/e2e/support/simulatedApi.ts` with consultation states, consultations
  (adopted, conflicting, and unavailable), child lineages, and consultation policies; add
  `src/clients/web/tests/e2e/simulated/triage-consultations.spec.ts` and
  `src/clients/web/tests/e2e/simulated/agent-catalog-consultations.spec.ts`; and extend the platform agent catalog test
  under `src/clients/web/tests/e2e/platform/` to assert the seeded consultation policies referenced by the enabled Coach
  Agent versions, without a test-only platform hook (FR-038, SC-005).

**Checkpoint**: All user stories work.

---

## Phase 7: Polish & Cross-Cutting Concerns

### Cross-process evidence and documentation

- [ ] T034 [P] Add the consultation case to
  `src/platform/Tests/SocAlytics.Platform.Images.Tests/AgentRuntimeStackTests.cs` with its scripted-provider scripts:
  `coach-jurgen-klopp` calls `get_match`, requests a consultation of `specialist-tactics`, declares its influence, and
  answers; `specialist-tactics` with the consultation section calls `find_pressing_phases` and answers; assert within
  120 seconds the succeeded parent, the answered consultation with the child's tool call through the MCP server, the
  declared influence, and the child absent from the turns, and that the earlier cases still pass (FR-039, SC-001,
  research R15).
- [ ] T035 [P] Make `docs/architecture/` describe the implemented state: re-read
  `docs/architecture/intelligence-and-agents.md` (Agent Groups And Interaction, Post-Analysis Match Triage, Agent
  Orchestration Authority, Durable State And Transaction Boundaries, Client, Transport, And Runtime Protocol,
  Authorization, Lineage, And Lifecycle, Supported Interactions, Match Debrief And Practice Planning),
  `intelligence-agent-catalog.md` (introduction, Catalog Rules, Prompt And Advice Governance, Coach Agent Catalog,
  Rendered CA Prompt Context, Interaction Model, First-Draft System Prompts, Implementation Waves, Validation And
  Promotion Gates), `platform-implementation.md` (functional areas, Agent Runtime), `client-applications.md` (Web UI,
  Connected Agent Conversations), `tenancy-and-technology.md` (AI), `security-and-data-governance.md`,
  `production-operations.md`, `overview.md`, `terminology-and-principles.md`, and `README.md`, and correct every
  statement this feature makes false, including the design updates and status statements listed in
  [plan.md](plan.md#architecture-updates) as examples, not the whole job, and any other planned, future-tense, or "not
  implemented" wording about consultations, delegation, `CA -> SA`, child invocations, `waiting-for-consultations`,
  `synthesizing`, consulted-agent lineage, or the Coach Agents' consultation instruction; keep the Wave 4 gates,
  evaluation suites, lifecycle operations, consultation-policy values, and production values explicitly open; update
  `README.md` and `AGENTS.md` "Current State", `src/agents/README.md`, and `src/agents/hermes/README.md` to name
  delegated consultations as development evidence; run `node .github/scripts/check-markdown.mjs` (Principle I).

### Final acceptance

- [ ] T036 Run the validation guide [quickstart.md](quickstart.md) sections 1 to 4 and confirm every expected result;
  record nothing as production readiness, consultation quality, latency, or cost evidence (FR-038, FR-039, SC-001 to
  SC-006).

---

## Dependencies & Execution Order

- **Phase 1 (Setup)**: no dependencies; T001–T006 in parallel; T007 after them.
- **Phase 2 (Foundational)**: after Phase 1; T008, T009, and T010 in parallel; T011 after T009 (kind check constraint).
- **User Story 1 (Phase 3)**: after Phase 2. T012, T013, and T014 in parallel (platform submission, Hermes artifacts,
  Hermes rendering); T015 → T016 → T017 → T018 → T019 run alone after T012 (shared Agent Orchestration handlers,
  endpoints, and stores); T020 and T021 in parallel after T019; T022 after T020 and T021; T023 after T013 and T014
  (shared Hermes fixtures); T024 after T023; T025 after T024.
- **User Story 2 (Phase 4)**: after User Story 1 (T026 extends T015's handler; T027 uses T024's functions and T023's
  fakes); T026 and T027 in parallel.
- **User Story 3 (Phase 5)**: after User Story 1 (T028 extends T017's delivery and the execution handlers; T029 uses
  T025's waiting and T014's rendering); T028 and T029 in parallel; independent of User Story 2.
- **User Story 4 (Phase 6)**: after T022 (refreshed snapshot); T030 and T031 in parallel; T032 after T030; T033 after
  T031 and T032 (shared `simulatedApi.ts`).
- **Polish (Phase 7)**: T034 after User Stories 1 and 3; T035 after all stories; T034 and T035 in parallel; T036 last.

## Parallel Example

```text
Phase 1, group 1: T001 consultation-policy | T002 agent-version 1.1.0 | T003 work-notification 1.2.0 | T004 execution-envelope 1.3.0 | T005 attempt-report 1.3.0 | T006 consultation-request
Phase 2, group 1: T008 Domain rules | T009 migration | T010 Hermes contract copies
Phase 3, group 1: T012 consulting submission | T013 Hermes artifacts | T014 Hermes rendering
Phase 3, group 2: T020 simulated workload | T021 consultation seed
Phase 4, group 1: T026 platform refusals | T027 Hermes refusal tests
Phase 5, group 1: T028 platform recovery and failures | T029 Hermes recovery tests
Phase 6, group 1: T030 conversation adapter | T031 Agent Catalog area
Phase 7, group 1: T034 cross-process consultation case | T035 documentation
```

## Implementation Strategy

1. **MVP**: Phases 1–3 (User Story 1): consultation policies pinned, consultations requested, children executed,
   parents resumed and completed with influence, and consultations read by the owner.
2. **Increment 2**: User Story 2 proves every refusal and loop prevention.
3. **Increment 3**: User Story 3 proves restart recovery, unavailable consultations, parent ending, and scope loss.
4. **Increment 4**: User Story 4 brings consultations and consultation policies to the Web UI.
5. **Finish**: cross-process evidence, documentation, and acceptance.
