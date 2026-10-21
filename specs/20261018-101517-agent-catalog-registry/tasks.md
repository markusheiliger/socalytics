---

description: "Task list for Agent Catalog Registry"
---

# Tasks: Agent Catalog Registry

**Input**: Design documents from `specs/20261018-101517-agent-catalog-registry/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (FR-028, SC-002 to SC-005), so every story has test tasks. Test tasks follow the implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story. This feature changes the code of `specs/archive/20261017-204332-agent-orchestration-invocations` in place and is implemented after that feature has merged.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Contracts, schema, domain rules, stores, validation, audit, and metrics that every story needs

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Contracts, schema, and domain

- [x] T001 [P] Add the JSON Schema 2020-12 contract `contracts/agent-catalog/catalog-baseline/v1/catalog-baseline.schema.json` with `releases/1.0.0.schema.json`, the valid fixture `examples/valid/baseline-1.0.0.json` (the nine agent identifiers of `docs/architecture/intelligence-agent-catalog.md` with `group`, `domain`, `displayName`, and the permitted-tool baseline of the MCP Tool Policy table, and the eleven-tool allowlist `get_match`, `get_player_stats`, `find_counter_attacks`, `find_turnovers`, `get_heatmap`, `find_ball_actions`, `find_set_plays`, `get_formations`, `find_pressing_phases`, `get_tactical_phases`, `get_video_clip`, written without `()`), and invalid fixtures (a tool id with `()`, an agent without `group`, an unknown property) with `expectations.json`, following `contracts/analysis/analyst-job/v1` (FR-001, FR-002; research R4)
- [x] T002 [P] Add the contracts `prompt-template`, `model-selection`, `tool-policy`, and `safety-policy` under `contracts/agent-catalog/<name>/v1/<name>.schema.json`, each with `releases/1.0.0.schema.json` and valid and invalid fixtures under `examples/`, with the fields and bounds of research R5: closed objects with `schemaVersion` `1.0.0`, `kind`, kebab-case `id` (3–100 characters), and SemVer `version`; prompt `text` "1–32,000 characters", `responseContract` `agent-response/1`, `slots` from `agent_id`, `display_name`, `philosophy_profile`, `tool_policy`, `evidence_context`, and `dimensionSetMajor` for `coach`; model `provider` `openai`, `github-models`, `simulated`, `model` and `deployment` matching `^[A-Za-z0-9._/:-]{1,128}$`, `parameters` `temperature` 0–2, `topP` 0–1, `maxOutputTokens` 1–32,768, `reasoningEffort` `low`, `medium`, `high`; `tools` "1–11 unique allowlisted identifiers"; safety `routes`, `hardBoundaries` "1–20 strings up to 500 characters", the escalation, refusal, and rule enumerations; invalid fixtures include a model selection with an extra `apiKey` field and a deployment with `@`, and a tool policy with `get_weather` (FR-001, FR-007–FR-009)
- [x] T003 [P] Add the contracts `dimension-set`, `philosophy-profile`, and `modifier-policy` under `contracts/agent-catalog/<name>/v1/<name>.schema.json`, each with `releases/1.0.0.schema.json` and fixtures, with the fields and bounds of research R5; valid fixtures are `coach-philosophy-dimensions` 1.0.0 (the eight dimensions of the Dimension Set table with their accepted evidence tools), the three provisional profiles `coach-jurgen-klopp`, `coach-pep-guardiola`, and `coach-jose-mourinho` 1.0.0 with the Base Philosophy Profiles weights keyed by dimension id, and one modifier policy (`perDimensionBound` 3, `maxPositiveMovement` 10, all five context categories, `budgetNeutral: true`); invalid fixtures include "a profile with a positional weight array", a weight of 101, and "a modifier policy with `perDimensionBound` 4" (FR-001, FR-010, FR-011, FR-013)
- [x] T004 [P] Add the contract `contracts/agent-catalog/agent-version/v1/agent-version.schema.json` with `releases/1.0.0.schema.json` and fixtures: `agentId`, `group`, `displayName`, `artifacts { promptTemplate, modelSelection, toolPolicy, safetyPolicy, philosophyProfile?, modifierPolicy? }` each `{ id, version, digest }` with digests `sha-256:<64 lowercase hex>`, and `disclosure` "20–1,000 characters" required for `coach`; valid fixtures for `specialist-tactics`, `specialist-video-analysis`, and `coach-jurgen-klopp`; invalid fixtures for a coach version without `disclosure` and a digest in another form (FR-001, FR-014)
- [x] T005 [P] Revise `contracts/agent-orchestration/execution-envelope/v1/execution-envelope.schema.json` to `x-socalytics-version` `1.1.0` with the optional property `agent { agentId, version, digest, artifacts: [{ kind, id, version, digest }] }`, add `releases/1.1.0.schema.json`, keep `releases/1.0.0.schema.json` unchanged, and add a valid fixture with `agent` and an invalid fixture whose `agent.artifacts` entry lacks `digest` (FR-021; research R10)
- [x] T006 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_agentorchestration_agent_catalog.sql`, numbered as the next free number on an up-to-date default branch, with every table and column of data-model.md: immutable `socalytics.agent_catalog_artifacts` (primary key (`kind`, `artifact_id`, `artifact_version`), unique `digest`, `kind` check of the seven kinds) protected by `socalytics.reject_immutable_change()`; versioned `socalytics.agent_versions` (primary key (`agent_id`, `agent_version`), unique `digest`, `state` check `draft`, `approved`, `retired`, `agent_id` check of the nine catalog identifiers) with `socalytics.attach_version_trigger` and a trigger that rejects changes to "`agent_id`, `agent_version`, `agent_group`, `digest`, `document`, `registered_by`, and `registered_at`, and deletes"; versioned `socalytics.agent_enablements` (primary key `agent_id`, nullable `agent_version` with a foreign key to `agent_versions`) with one inserted row per catalog identifier; nullable `agent_version` and `agent_version_digest` on `socalytics.agent_invocations`, and replace its `failure_reason` check so it also allows `agent-not-available`; immutable `socalytics.agent_invocation_pins` (primary key (`invocation_id`, `kind`)); never edit an applied migration; classify `agent_catalog_artifacts` and `agent_invocation_pins` as `Immutable` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs` (FR-003, FR-015, FR-016, FR-019; data-model.md)
- [x] T007 [P] Add the catalog domain in `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/`: extend `AgentCatalogIds.cs` into the catalog baseline 1.0.0 (display names, permitted-tool baselines, the tool allowlist, the "All CAs" baseline for Coach Agents), and add under `Catalog/` the record kinds (`AgentCatalogRecordKind`), `SemanticVersion` (SemVer 2.0.0 without build metadata, major extraction, ordering), `AgentVersionState` with the transitions "draft → approved → retired", `AgentCatalogRules` with every validation rule of data-model.md "Validation rules" returning all `AgentCatalogViolation { code, pointer, detail }` with the stable codes of research R3, `ProfileDistance` ("`sum(|a_i - b_i|) / 200`", review threshold below 0.10), and `InvocationPin` (FR-002, FR-005–FR-015; research R3, R4, R6, R7)

### Contract index

- [x] T008 Add index rows for the nine `contracts/agent-catalog/*/v1` contracts to `contracts/README.md` following the existing `$id` convention and update the `agent-orchestration/execution-envelope` row to version `1.1.0` with its released copies, so that `ContractCatalog`, `ContractIndexTests`, and `SchemaMetaValidationTests` in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests` load and validate them and their fixtures; run `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` (FR-001, FR-021)

### Application abstractions

- [x] T009 Add in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Catalog/`: the store interfaces `IAgentCatalogArtifactStore`, `IAgentVersionStore`, `IAgentEnablementStore`, and `IAgentInvocationPinStore`; `AgentCatalogRecordValidator` (structural validation through `IContractValidator` against the embedded `contracts/agent-catalog/<kind>/v1` schema, then `AgentCatalogRules` with referenced records loaded from the stores, collecting all violations, and `ContentDigest` over the canonical document); the problem codes of contracts/agent-catalog-api.md in `AgentCatalogProblems.cs`; the audit event types and builders of research R12 in `AgentCatalogAuditEvents.cs` (kind, id, version, digest, agent id, outcome, violation codes only; never prompt, rationale, or provenance text); and the meter `AgentCatalogMetrics` with the counters of research R12 in `src/platform/SocAlytics.Platform.Application/Telemetry/AgentCatalogMetrics.cs` (FR-003–FR-005, FR-025, FR-025a)

### Stores and domain tests

- [x] T010 [P] Add the Dapper stores for the four interfaces in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/Catalog/` (insert-once artifacts and agent versions returning the existing row on a `(kind, id, version)` conflict, version-checked lifecycle and enablement updates, enablement read with `FOR SHARE` for submission, pin insert and read), and add the catalog audit detail keys to the allow-list in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/PostgresAuditTrail.cs` (FR-003, FR-004, FR-015–FR-017, FR-019, FR-025)
- [x] T011 [P] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/Domain/AgentCatalogRulesTests.cs` and `CatalogBaselineTests.cs`: every rule and violation code of `AgentCatalogRules`, `SemanticVersion` parsing and ordering, `ProfileDistance` for the three provisional profiles ("Klopp–Guardiola 0.25, Klopp–Mourinho 0.25, Guardiola–Mourinho 0.31"), and equality of the domain baseline with `contracts/agent-catalog/catalog-baseline/v1/examples/valid/baseline-1.0.0.json` (FR-002, FR-006–FR-014, FR-028)

### Registration of foundations

- [x] T012 Register the stores, `AgentCatalogRecordValidator`, and `AgentCatalogMetrics` in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` and `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; run `dotnet build src/platform/SocAlytics.Platform.slnx` and the Integration tests of T011 (FR-003, FR-025a)

**Checkpoint**: Foundation ready - user story implementation can now begin

---

## Phase 2: User Story 1 - Register and approve a versioned Specialist Agent (Priority: P1) 🎯 MVP

**Goal**: Club Admins register validated, immutable, content-addressed artifacts and Specialist Agent versions, approve and retire them, and read the catalog.

**Independent Test**: As a Club Admin, register a prompt template, model selection, tool policy, and safety policy for `specialist-tactics`, register and approve an agent version, and confirm digests, states, and audit events; invalid records are refused with every violation named and store nothing.

### Implementation for User Story 1

- [x] T013 [US1] Add the handlers in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Catalog/Registration/` (`RegisterAgentCatalogArtifact`, `RegisterAgentVersion`: Club Admin through `IAccessAuthorizer.AuthorizeClubAsync(ClubPermission.Administer, …)`, validation, equal digest replays, different digest `409 agent-catalog-version-exists`, `400 agent-catalog-validation-failed` with every violation, audit accepted, replayed, and refused, count) and `Catalog/Reads/` (`GetAgentCatalogBaseline`, `ListAgentCatalogArtifacts`, `GetAgentCatalogArtifact`, `ListAgentVersions`, `GetAgentVersion` with `enabled`) and `Catalog/Lifecycle/` (`ApproveAgentVersion`: draft only or `409 agent-version-not-draft`, conditional on the version, `self_approval` marked; `RetireAgentVersion`: approved only or `409 agent-version-not-approved`, enabled version `409 agent-version-enabled`), each refusing non-Club Admins with `403` and an `agent.catalog.access.refused` audit event (FR-003–FR-006, FR-014, FR-015, FR-017, FR-024, FR-025)
- [x] T014 [US1] Add `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentCatalogEndpoints.cs` with the operations `getAgentCatalogBaseline`, `registerAgentCatalogArtifact`, `listAgentCatalogArtifacts`, `getAgentCatalogArtifact`, `registerAgentVersion`, `listAgentVersions`, `getAgentVersion`, `approveAgentVersion`, and `retireAgentVersion` of contracts/agent-catalog-api.md through `MapMemberApi()`, `IfMatchHeader`, conditional reads with `ETag`, and `ProblemResults` with typed OpenAPI schemas; map them in `src/platform/SocAlytics.Platform.Api/Program.cs` and register the T013 handlers in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (FR-024, FR-027)

### Test support for User Story 1

- [x] T015 [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/Support/AgentCatalogApi.cs` (typed calls for every catalog operation as a given member, and builders that load the `contracts/agent-catalog/*/v1/examples/valid/` fixtures and recompute their digests) (FR-028)

### Tests for User Story 1

- [x] T016 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/RegistrationTests.cs`: each artifact kind and a specialist agent version are stored with the digest of their canonical document; equal replays return `200` unchanged and different content `409`; refusals with every violation named and nothing stored for an unknown tool, a tool outside the baseline, a model selection with an extra field or a name outside the pattern, an unknown provider, an identifier outside the catalog, a missing artifact, a group mismatch, and a specialist referencing a profile; non-Club Admins get `403` and an audit event; list and read operations return each record with its digest, document, registering member, and time, and agent versions with state and `enabled`; the registration counter of `AgentCatalogMetrics` counts by kind and outcome; audit events, logs, and telemetry contain no prompt, rationale, or provenance text (FR-003–FR-009, FR-014, FR-024, FR-025, FR-025a; SC-002, SC-003, SC-005)
- [x] T017 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/LifecycleTests.cs`: approval by the creator marks self-approval; approving twice and approving a retired version are refused; stale and missing `If-Match` give `412` and `428`; concurrent approvals leave one winner; approved and retired documents and digests never change; retiring an approved version succeeds and audits, and retiring the enabled version is refused (FR-015, FR-017; SC-002, SC-005)

**Checkpoint**: User Story 1 is fully functional and testable independently

---

## Phase 3: User Story 2 - Enable an agent version and pin it into every invocation (Priority: P2)

**Goal**: One approved Specialist Agent version per identifier is enabled per stamp; submission pins it; the envelope, artifact read, completion, and reads use only the pin; the seed provides the two direct specialists.

**Independent Test**: Enable an approved version of `specialist-tactics`, submit a question, confirm the pins in the invocation, envelope, and lineage, read the pinned artifacts as the workload, enable a second version and confirm that a retry keeps the first pin, and disable the agent.

### Implementation for User Story 2

- [x] T018 [US2] Add `ListAgentEnablements` and `SetAgentEnablement` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Catalog/Enablement/` (Club Admin, conditional on the enablement version, approved Specialist Agent version of the same agent or `409 agent-version-not-enableable` with `reason` `not-approved`, `retired`, or `simulated-provider` outside Development and Test, Coach Agent versions `409 coach-agent-enablement-unsupported`, `null` disables, atomic replacement, audit with previous and new version, count), add `listAgentEnablements` and `setAgentEnablement` to `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentCatalogEndpoints.cs`, and register the handlers in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (FR-016, FR-025)
- [x] T019 [US2] Add the Development and Test seed `src/platform/SocAlytics.Platform.Infrastructure/AgentOrchestration/Catalog/AgentCatalogSeeder.cs` with the seed documents under `src/platform/SocAlytics.Platform.Infrastructure/AgentOrchestration/Catalog/Seed/` (prompt templates of `specialist-tactics` and `specialist-video-analysis` copied verbatim from First-Draft System Prompts, one `simulated` model selection, tool policies equal to the two baselines, safety policies with their hard boundaries from the Specialist Agent Catalog, agent versions `0.1.0`), embedded through `src/platform/SocAlytics.Platform.Infrastructure/SocAlytics.Platform.Infrastructure.csproj`; it runs only in Development and Test and only when `agent_catalog_artifacts` is empty, registers through the same `AgentCatalogRecordValidator` and stores as the T013 handlers (without member authorization) as actor `system:seed` with audit events, approves, and enables both, and registers no Coach Agent artifact; register it as a hosted service in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` (FR-026; research R13)
- [x] T020 [US2] Replace the configured enablement: remove `AgentOrchestration:Agents:Enabled` from `src/platform/SocAlytics.Platform.Application/AgentOrchestration/AgentOrchestrationOptions.cs` and `src/platform/SocAlytics.Platform.Api/appsettings.Development.json` (log once at start in Development and Test when a leftover value is configured); make `ListAvailableAgents` return `{ agentId, group, domain, displayName, enabledVersion }` from `IAgentEnablementStore` and `AddAgentConversationParticipant` refuse agents without an enabled version with `409 agent-not-available` in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/`, update their response schema in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentConversationEndpoints.cs`, and make `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Support/AgentOrchestrationFixture.cs` rely on the seed instead of configured enabled agents (FR-018; research R9)
- [x] T021 [US2] Pin at submission: in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/SubmitAgentInvocation.cs`, inside the existing unit of work, read the agent's enablement (`FOR SHARE`) and its agent version, refuse an agent without an enabled version with `409 agent-not-available`, store `agent_version` and `agent_version_digest` on the invocation, and insert one `agent_invocation_pins` row per artifact; add `agent: { version, digest, artifacts: [{ kind, id, version, digest }] }` (or `null` for unpinned invocations) to `GetAgentInvocation` and the lineage read in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/`, their store queries in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/`, and their response schemas in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentConversationEndpoints.cs`; never include artifact content (FR-019, FR-020, FR-023a)
- [x] T022 [US2] Use the pin in execution: in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/`, make the claim refuse an invocation without a pin with `409 claim-obsolete`, end it `failed` with reason `agent-not-available`, and audit it, and otherwise add `agent` to the `execution-envelope` 1.1.0 envelope; make completion compare `promptRef`, `toolPolicyRef`, and `safetyPolicyRef` with the pinned digests, refuse a mismatch with `409 execution-references-mismatch` leaving the attempt active, and store the pinned references in the `completed` lineage entry and accepted advice; add `ReadPinnedAgentArtifacts` (current attempt, token, lease, deadline, and scope checks as for lease renewal, `409 agent-attempt-stale` with its disposition otherwise, returns the stored documents of the pinned agent version and artifacts and, for a Coach Agent version, the profile's dimension set) and its operation `readPinnedAgentArtifacts` in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentWorkloadEndpoints.cs`, registering the handler in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; add `agent-not-available` to the failure reasons in `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/`; make `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/` read the pinned artifacts after each claim and declare their digests as `promptRef`, `toolPolicyRef`, and `safetyPolicyRef`, and give `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Support/TestAgentWorkload.cs` the artifact read and pinned references by default (FR-020–FR-023, FR-023b; research R10, R11)

### Tests for User Story 2

- [x] T023 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/EnablementTests.cs`: enabling an approved specialist version replaces the previous one atomically and audits; draft, retired, Coach Agent, and (outside Development and Test configuration) simulated-provider versions are refused with their reasons; disabling removes the agent from `listAvailableAgents` and makes participant addition and submission answer `409 agent-not-available`; stale `If-Match` is refused; concurrent changes leave one winner; non-Club Admins get `403` (FR-016–FR-018; SC-001)
- [x] T024 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/PinningTests.cs`: a submission pins the enabled version and every artifact digest; the owner's invocation and lineage reads show the pins without artifact content; after enabling another version, disabling, or retiring, a queued invocation, its retry after lease expiry, its envelope, and its lineage keep byte-identical pins while a new submission pins the new version; an unpinned invocation inserted as before this feature is refused at claim and fails with reason `agent-not-available`, and ended unpinned invocations stay readable (FR-019–FR-021, FR-023a, FR-023b; SC-001, SC-004)
- [x] T025 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/PinnedArtifactReadTests.cs`: the claim's envelope validates against `execution-envelope` 1.1.0 with `agent`; the current attempt reads every pinned document and each digest matches; a fenced, expired, or replaced attempt gets `409 agent-attempt-stale`; member sessions and Analyst Manager tokens get `401`; a completion with a mismatching `promptRef`, `toolPolicyRef`, or `safetyPolicyRef` is refused and the attempt stays active, and a matching completion records the pins (FR-021–FR-023)
- [x] T026 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/SeedTests.cs`: in Test an empty catalog is seeded once with the two approved and enabled specialists whose prompt text equals the First-Draft System Prompts and whose tool policies equal their baselines, no Coach Agent artifact is seeded, a non-empty catalog is not seeded again, and the seed does not run in another environment; the simulated agent workload completes an invocation end to end with the pinned references (FR-026; SC-001)

**Checkpoint**: User Stories 1 and 2 work independently

---

## Phase 4: User Story 3 - Register and validate Coach Agent philosophy artifacts (Priority: P3)

**Goal**: Dimension sets, base philosophy profiles, modifier policies, and Coach Agent versions are registered with every architectural invariant enforced, distances are reported, and close profiles need an acknowledged review at approval.

**Independent Test**: Register `coach-philosophy-dimensions` 1.0.0, the three provisional profiles, a modifier policy, and a Coach Agent version, approve it, and confirm the reported distances; invalid profiles, dimension sets, modifier policies, and Coach Agent versions are refused with every violation named.

### Implementation for User Story 3

- [x] T027 [US3] Add profile distances and the differentiation acknowledgement in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Catalog/`: `RegisterAgentCatalogArtifact` and `GetAgentCatalogArtifact` return `distances` for philosophy profiles against the profiles of approved, non-retired Coach Agent versions over the same dimension-set id and major, `GetAgentVersion` returns them for Coach Agent versions, and `ApproveAgentVersion` requires `differentiationReviewAcknowledged: true` when any distance is below 0.10 (`409 differentiation-review-required` otherwise) and records it in `differentiation_acknowledged` and the audit event (FR-012, FR-015; research R7)

### Tests for User Story 3

- [x] T028 [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/CoachArtifactTests.cs`: the dimension set and three provisional profiles register and report "Klopp–Guardiola 0.25, Klopp–Mourinho 0.25, Guardiola–Mourinho 0.31"; a minor dimension-set version with changed dimension ids is refused; profiles totalling 99, missing or adding a dimension, with a non-integer or out-of-range weight, without rationale or provenance, with an unknown dimension set, or with an invalid superseded reference are refused naming every violation; modifier policies over their bounds or with an unpermitted category are refused; Coach Agent versions with mismatched dimension-set majors, without exactly one profile slot, without disclosure, or with a safety policy lacking the disclosure rules are refused; a synthetic fourth profile within 0.10 of an approved one needs the acknowledgement at approval; an approved Coach Agent version cannot be enabled (FR-008–FR-016; SC-003)

**Checkpoint**: All user stories are independently functional

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Interface snapshot, documentation, and full verification

- [ ] T029 Refresh the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` (never in the verification environment), then run the Host tests without the variable (FR-027)
- [ ] T030 Make `docs/architecture/` describe the implemented state: re-read `docs/architecture/intelligence-agent-catalog.md` (introduction, Catalog Rules, Prompt And Advice Governance, Base Philosophy Profiles, Adding Or Revising A Coach Agent, MCP Tool Policy, Implementation Waves, Validation And Promotion Gates), `intelligence-and-agents.md` (Agent Orchestration Authority, Client, Transport, And Runtime Protocol), `platform-implementation.md` (Agent Runtime, Data Organization), `tenancy-and-technology.md` (AI), `security-and-data-governance.md` (`DAT-007`, Audit Events), `overview.md`, `terminology-and-principles.md`, and `docs/architecture/README.md`, and correct every statement this feature makes false, including the "decided and not yet implemented", "planned", "future", and "design inputs rather than deployed behavior" wording about the agent catalog, its artifacts, and how versions are resolved (the plan's Architecture Updates list examples, not the whole job), while keeping the prompt drafts, profile values, model choices, evaluation and promotion gates, Coach Agent enablement, Hermes, MCP tools, the Web UI, and production values explicitly Provisional, planned, or unresolved; update `AGENTS.md` "Current State" and `src/platform/README.md` so they name the agent catalog registry, pinning, the attempt-scoped artifact read, and the Development and Test seed as development evidence (constitution Principle I)
- [ ] T031 Run the quickstart's automated checks: `dotnet restore src/platform/SocAlytics.Platform.slnx`, `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, `dotnet test src/platform/SocAlytics.Platform.slnx --no-build`, `npm ci` and `npm run build` in `src/clients/web` (the Kiota client regenerates from the refreshed snapshot), and `node .github/scripts/check-markdown.mjs`; fix what fails within this feature's files (FR-028; quickstart.md)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: starts once `specs/archive/20261017-204332-agent-orchestration-invocations` has merged; blocks every story.
- **User Story 1 (Phase 2)**: after Phase 1.
- **User Story 2 (Phase 3)**: after User Story 1 (enablement needs approved versions; the seed uses the registration handlers).
- **User Story 3 (Phase 4)**: after User Story 1 (registration and approval handlers); independent of User Story 2.
- **Polish (Phase 5)**: after all stories.

### Within Phases

- Phase 1: T001–T007 run in parallel; T008 indexes the contracts; T009 adds the abstractions that T010 implements; T010 and T011 run in parallel; T012 registers what they created.
- Phase 2: T013, T014, and T015 run in order (handlers, endpoints, test support); T016 and T017 run in parallel.
- Phase 3: T018–T022 run in order because they share `AgentCatalogEndpoints.cs`, `AgentConversationEndpoints.cs`, `ApplicationServiceCollectionExtensions.cs`, and the orchestration stores, and T020 needs the seed of T019; T023–T026 run in parallel.
- Phase 4: T027, then T028.
- Phase 5: T029, T030, T031 in order.

## Parallel Example: Phase 1

```text
Phase 1, group 1: T001 baseline contract | T002 artifact contracts | T003 philosophy contracts | T004 agent-version contract | T005 envelope 1.1.0 | T006 migration | T007 domain
Phase 1, group 2: T010 stores and audit allow-list | T011 domain rule tests
Phase 2, group 1: T016 registration tests | T017 lifecycle tests
Phase 3, group 1: T023 enablement tests | T024 pinning tests | T025 artifact read tests | T026 seed tests
```

## Implementation Strategy

### MVP First (User Story 1)

Complete Phase 1 and Phase 2: the catalog stores validated, immutable records and approves Specialist Agent versions, which already replaces the unversioned design inputs with governed records.

### Incremental Delivery

1. Phase 1 → foundation.
2. User Story 1 → registration and approval (MVP).
3. User Story 2 → enablement, seed, pinning, envelope, artifact read; orchestration resolves agents from the catalog.
4. User Story 3 → Coach Agent philosophy artifacts and distances.
5. Polish → OpenAPI snapshot, architecture and repository documentation, full verification.
