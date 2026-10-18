# Tasks: Coach Agent Direct Conversations

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md),
[attempt report 1.2.0](contracts/attempt-report-1.2.0.md), [Coach Agent interfaces](contracts/coach-agent-interfaces.md),
and [quickstart.md](quickstart.md).

**Tests**: Required by FR-029 and FR-030 and the validation guide. Each implementation task includes its directly
related tests (platform tests in the named test project, Hermes tests in `src/agents/hermes/tests/`, Web UI tests in
`src/clients/web/tests/`), so a task never lands deliberately failing tests. Hermes tests use only the scripted
provider and the fakes; no test calls a model service or needs a secret.

**Organization**: Contract releases first, then the shared Domain rules, migration, and Hermes contract copies, then
one phase per user story in priority order, then cross-process evidence, documentation, and final acceptance.

## Format And Boundaries

- Tasks use `- [ ] Tnnn [P?] [USn?] Description`.
- Consecutive unchecked `[P]` tasks under one heading run in parallel, each from the same commit; their predicted file
  changes must be disjoint and neither may depend on the other.
- This feature changes code that the dependent features create: Agent Orchestration and its `Catalog` folder in the
  platform layers, `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/`, `src/agents/hermes/`, and the Web
  UI's `triage` and `agent-catalog` feature folders. The MCP server and the agent tool operations stay unchanged.
- One forward-only migration `NNNN_agentorchestration_coach_agents.sql`, numbered at implementation time as the next
  free number on an up-to-date default branch; no new table, so `PersistedTableClassifications` is unchanged.
- The OpenAPI snapshot is refreshed only by T016 with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`,
  never in the verification environment; the Web UI's Kiota client is regenerated only by T024.
- Run Hermes checks from `src/agents/hermes/` with the environment's locked sequence:
  `uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`,
  `uv build --wheel --no-build-isolation`, `uv run --no-sync pytest`. No Hermes dependency changes.
- `src/agents/hermes/tests/conftest.py` and `src/agents/hermes/tests/fakes/` are changed only by T019; other Hermes
  tasks keep their fixtures in their own test files or in new files under `src/agents/hermes/tests/scripts/`.
- Profile weights, modifier reasons, prompt text, question text, and advice text never appear in logs, audit events, or
  telemetry (FR-027); every task that adds a log, audit detail, or metric tag asserts this in its tests.

## Phase 1: Setup (contract releases)

**Purpose**: Publish the backward-compatible contract releases every later task builds on.

### Contract releases

- [ ] T001 [P] Add release 1.1.0 of `contracts/agent-orchestration/work-notification/v1/`: copy the current schema to
  `releases/1.0.0.schema.json` if not yet present, set `x-socalytics-version` to `1.1.0`, widen `route` to the enum
  `user-to-specialist`, `user-to-coach`, add `releases/1.1.0.schema.json`, a valid fixture
  `examples/valid/notification-user-to-coach.json`, and an invalid fixture
  `examples/invalid/notification-route-coach-to-coach.json` (FR-004, research R1).
- [ ] T002 [P] Add release 1.2.0 of `contracts/agent-orchestration/execution-envelope/v1/`: widen `route` to
  `user-to-specialist`, `user-to-coach`, allow `agent.artifacts[].kind` `dimension-set`, set `x-socalytics-version`
  `1.2.0`, add `releases/1.2.0.schema.json`, a valid fixture `examples/valid/envelope-user-to-coach.json` with eight
  pins (agent version excluded: prompt template, model selection, tool policy, safety policy, philosophy profile,
  modifier policy, dimension set), and an invalid fixture `examples/invalid/envelope-route-coach-to-coach.json`; earlier
  releases stay unchanged (FR-004, FR-005, research R1 and R3).
- [ ] T003 [P] Add release 1.2.0 of `contracts/agent-orchestration/attempt-report/v1/` exactly as
  [contracts/attempt-report-1.2.0.md](contracts/attempt-report-1.2.0.md): the closed definition
  `appliedPhilosophyProfile` (`renderingVersion` pattern `^[a-z][a-z0-9-]{0,62}/[0-9]{1,4}$`; `dimensionSet`,
  `profile`, `modifierPolicy` as `{ id, version, digest }` with digest `^sha-256:[0-9a-f]{64}$`; `baseWeights` and
  `appliedWeights` objects of 1–32 properties keyed `^[a-z0-9]+(-[a-z0-9]+)*$` with integer values 0–100; `modifiers`
  array 0–5 of `{ category, deltas, reason, evidenceCallIds }` with `category` one of `score-state`, `match-period`,
  `player-number-state`, `opponent-phase-behavior`, `evidence-availability`, `deltas` 1–32 properties of non-zero
  integers −100–100, `reason` 1–300 characters, `evidenceCallIds` 1–10 unique of `^[A-Za-z0-9._\-]{1,128}$`), optional
  on `checkpoint` (required when `kind` is `profile-rendered`) and `completion`; `releases/1.2.0.schema.json`; and the
  valid and invalid fixtures the contract lists (FR-015, FR-016a, research R7).

### Contract index

- [ ] T004 Update `contracts/README.md` so the index rows of `work-notification`, `execution-envelope`, and
  `attempt-report` name versions `1.1.0`, `1.2.0`, and `1.2.0` and their released baselines, and confirm with
  `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` that every new fixture validates or fails as
  named (FR-028).

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared rules, storage, and Hermes contract copies that every user story needs.

### Domain rules, migration, and Hermes contract copies

- [ ] T005 [P] Add the route wire value `user-to-coach` to `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/AgentRoute.cs`
  (mapping from the catalog baseline group: `specialist` → `user-to-specialist`, `coach` → `user-to-coach`) and the pure
  validator `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/Catalog/AppliedPhilosophyProfileRules.cs` with
  the rules and violation codes of [data-model.md](data-model.md#applied-philosophy-profile-attempt-report-120): per
  dimension `|Σ deltas[d]| ≤ perDimensionBound`; `Σ_d Σ deltas[d] = 0`; `Σ_d max(0, Σ deltas[d]) ≤ maxPositiveMovement`;
  `appliedWeights[d] ≥ 0` and `Σ appliedWeights = 100`; at most one modifier per category; categories within the pinned
  policy; references equal to the pins; base weights equal to the pinned profile;
  `appliedWeights[d] = baseWeights[d] + Σ deltas[d]`; codes `applied-profile-missing`, `applied-profile-not-allowed`,
  `reference-mismatch`, `base-weights-mismatch`, `category-not-permitted`, `duplicate-category`, `unknown-dimension`,
  `delta-out-of-bounds`, `not-budget-neutral`, `positive-movement-exceeded`, `negative-weight`,
  `applied-weights-mismatch`, `evidence-call-unknown`, `evidence-not-answered`, `last-checkpoint-mismatch` (the evidence
  and last-checkpoint rules take the attempt's tool-call outcomes and last snapshot as inputs). Cover every rule and
  code with database-free tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/AppliedPhilosophyProfileRulesTests.cs`
  (FR-012, FR-015, FR-016, research R6 and R7).
- [ ] T006 [P] Add the forward-only migration
  `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_agentorchestration_coach_agents.sql`
  (numbered at implementation time): replace the `agent_invocations.route` check constraint with one allowing
  `user-to-specialist` and `user-to-coach`, and add `agent_accepted_advice.applied_philosophy_profile jsonb null`
  ("set only for `user-to-coach` advice"); prove with
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentMigrationTests.cs`
  that both routes insert, another value is refused, and the column exists and is nullable (FR-004, FR-017,
  data-model.md).
- [ ] T007 [P] Extend `src/agents/hermes/tools/sync_contracts.py` and the package data under
  `src/agents/hermes/src/socalytics_hermes/contracts/` with the released schemas `dimension-set`, `philosophy-profile`,
  `modifier-policy` 1.0.0, `work-notification` 1.1.0, `execution-envelope` 1.2.0, and `attempt-report` 1.2.0, switch the
  validators in `src/agents/hermes/src/socalytics_hermes/contract_validation.py` to the new envelope, notification, and
  attempt-report releases, and extend `src/agents/hermes/tests/test_contract_copies.py` so every copy equals its
  repository `releases/` file (research R4).

---

## Phase 3: User Story 1 - A Coach asks a Coach Agent for a whole-match perspective (Priority: P1) 🎯 MVP

**Goal**: Enabled Coach Agents are participants; `user-to-coach` invocations are pinned, executed by Hermes (and the
simulated workload) with the rendered profile block, and accepted with the applied profile and the disclosure in the
owner's reads.

**Independent Test**: With the Development and Test seed, enable a Coach Agent, add it, submit a question, run Hermes
with the scripted provider (`get_match`, then answer), and confirm the route, the eight pins, the profile block in the
system context, and the owner's reads with disclosure and applied profile (spec User Story 1).

### Platform: enablement, submission, reports, reads, audit, seed

- [ ] T008 [US1] Enable Coach Agents and list them: remove the `409 coach-agent-enablement-unsupported` refusal from
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Catalog/SetAgentEnablement.cs` (all other refusals
  unchanged for both groups), let `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Conversations/`
  accept enabled Coach Agents as participants (several per conversation) and return `disclosure` (string; null for
  Specialist Agents) from the enabled agent version's document in `listAvailableAgents`, update the response schemas in
  `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentCatalogEndpoints.cs` and
  `AgentConversationEndpoints.cs`, and add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentEnablementTests.cs`
  (enable, replace, disable with `If-Match`, refusals for draft, retired, and simulated provider outside Development
  and Test, audit event, available-agents read with disclosure, participants of both groups, `agent-not-available`
  after disabling) (FR-001 to FR-003, research R2).
- [ ] T009 [US1] Route and pin Coach Agent invocations: in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/SubmitAgentInvocation.cs` derive the
  route from the agent's group and, for Coach Agents, insert the `dimension-set` pin of the pinned profile's dimension
  set in the same unit of work; publish `work-notification` 1.1.0 and return `execution-envelope` 1.2.0 from the claim
  in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/`; make
  `Execution/ReadPinnedAgentArtifacts.cs` return the dimension set from its pin; add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentSubmissionTests.cs`
  (route `user-to-coach`, eight artifact pins in invocation, envelope, and owner read, envelope and notification valid
  against the new releases, pinned read returns the dimension set, pins unchanged after enablement changes, Specialist
  Agent submissions unchanged) (FR-004 to FR-006, research R1 and R3).
- [ ] T010 [US1] Validate and store the applied profile: in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/RecordAgentCheckpoint.cs` and
  `CompleteAgentAttempt.cs` apply `AppliedPhilosophyProfileRules` with the invocation's pins, the pinned profile and
  modifier policy, the attempt's `agent_tool_calls` outcomes, and (for completions) the attempt's last
  `profile-rendered` checkpoint; refuse checkpoints with `400 applied-philosophy-profile-invalid` and completions with
  `409 applied-philosophy-profile-invalid` (`violations: [{ code, pointer }]`, nothing recorded, the attempt stays
  active; stale and operation-identifier rules first); store valid snapshots in the lineage payloads and in
  `agent_accepted_advice.applied_philosophy_profile` through the stores in
  `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/` (including the tool-call outcome
  lookup); update the workload request schemas in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentWorkloadEndpoints.cs`;
  add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentReportTests.cs`
  (valid `profile-rendered` checkpoint and completion without modifiers; refusals for missing snapshot on a Coach
  Agent completion, snapshot on a Specialist Agent attempt, reference mismatch, base-weights mismatch, applied-weights
  mismatch, last-checkpoint mismatch; replay with the same operation identifier) (FR-015, FR-016, FR-016a, FR-017,
  research R7).
- [ ] T011 [US1] Return disclosure and applied profile in owner reads: add `disclosure` (from the pinned agent
  version's document) and `appliedPhilosophyProfile` (object or null) to `listAgentConversationTurns`,
  `getAgentInvocation`, and `getAgentInvocationLineage` in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Invocations/` and their queries in
  `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/`, list the `dimension-set` pin and the
  `profile-rendered` and `modifiers-omitted` checkpoint details in the lineage, update the schemas in
  `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentConversationEndpoints.cs`, and add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentReadTests.cs`
  (disclosure on every Coach Agent advice in all three reads, advice text unchanged, null disclosure and profile for
  Specialist Agents, conditional reads unchanged, owner-only authorization unchanged) (FR-017, FR-018, research R8).
- [ ] T012 [US1] Distinguish the route in audit and telemetry: add the detail `route` to the submission, claim,
  completion, failure, expiry, and scope-loss audit events in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/AgentOrchestrationAuditEvents.cs` and their call
  sites, audit refused applied profiles with violation codes only, add the tag `route` to the submission, claim,
  completion, and failure counters in `src/platform/SocAlytics.Platform.Application/Telemetry/AgentOrchestrationMetrics.cs`,
  keep the audit detail allow-list in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/PostgresAuditTrail.cs`
  free of weights, reasons, and texts, and add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentAuditTests.cs`
  (route in every listed event and counter; sentinel weights, reasons, prompt, question, and advice text absent from
  audit events and telemetry) (FR-027, research R12).
- [ ] T013 [US1] Seed the Coach Agents: add the Coach Agent step to
  `src/platform/SocAlytics.Platform.Infrastructure/AgentOrchestration/Catalog/AgentCatalogSeeder.cs` (Development and
  Test only; applies when no artifact with group `coach` and no `dimension-set`, `philosophy-profile`, or
  `modifier-policy` exists, independently of the Specialist Agent step; same validator and stores; actor
  `system:seed`; audited) and the records under
  `src/platform/SocAlytics.Platform.Infrastructure/AgentOrchestration/Catalog/Seed/coach/` of research R11: dimension set
  `coach-philosophy-dimensions` 1.0.0, profiles `coach-jurgen-klopp`, `coach-pep-guardiola`, `coach-jose-mourinho`
  1.0.0 with the Provisional weights of Base Philosophy Profiles, modifier policy `coach-context-modifiers` 1.0.0
  (major 1, bound 3, movement 10, all five categories), tool policy `coach-tools` 0.1.0 (the "All CAs" baseline),
  safety policy `coach-safety` 0.1.0 (route `user-to-coach`, the three Coach Agent rules), the seed's `simulated` model
  selection (equal content replays), prompt templates 0.1.0 derived from the First-Draft System Prompts with the
  weight table and modifier sentence replaced by `{{philosophy_profile}}` (`dimensionSetMajor` 1) and the consultation
  paragraph replaced by suggestions for the human coach, and agent versions 0.1.0 with disclosure statements,
  approved and enabled; add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentSeedTests.cs`
  (records valid and approved, all three enabled, distances 0.25, 0.25, 0.31, no weight table in prompt text, skipped
  when Coach Agent artifacts exist, not applied outside Development and Test, Specialist seed unaffected) (FR-026).

### Simulated workload and Coach Agent tool calls

- [ ] T014 [P] [US1] Execute `user-to-coach` invocations in
  `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/`: read the pinned artifacts, record a
  `profile-rendered` checkpoint with `renderingVersion` `simulated-profile/1`, the base vector, and no modifiers, call
  the tools it already calls, and complete with simulated five-section advice that names itself as simulated and the
  same snapshot; add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/SimulatedCoachAgentWorkloadTests.cs`
  (a seeded Coach Agent invocation succeeds with disclosure and applied profile; Specialist Agent behavior unchanged)
  (FR-020, research R10).
- [ ] T015 [P] [US1] Prove the agent tool operations for Coach Agent attempts in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentToolCallTests.cs`:
  attempt binding with every stale disposition, the pinned Coach Agent tool policy, call limits, scope loss, and
  tool-call lineage behave exactly as for Specialist Agent attempts, using the existing integration test support
  unchanged (FR-019, research R9).

### OpenAPI snapshot

- [ ] T016 [US1] Refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
  and confirm that `coach-agent-enablement-unsupported` no longer appears, that `disclosure`, `appliedPhilosophyProfile`,
  `user-to-coach`, and `applied-philosophy-profile-invalid` appear with typed schemas, and that the Host tests pass
  (FR-028).

### Hermes: profile block and Coach Agent validation

- [ ] T017 [P] [US1] Add `src/agents/hermes/src/socalytics_hermes/philosophy.py` (the applied-profile snapshot type with
  `baseWeights`, `modifiers`, `appliedWeights`, and the pinned references, and its serialization to the
  `attempt-report` 1.2.0 shape) and render the profile block `hermes-profile-block/1` and the Coach Agent section
  `hermes-coach-section/1` in `src/agents/hermes/src/socalytics_hermes/rendering.py` exactly as research R5 orders them
  (dimension set, profile with digest, table `Dimension | Meaning | Base | Modifier | Applied` in dimension-set order
  with a total of 100, modifier policy constraints and categories, applied modifiers or `none`, the fixed instruction;
  disclosure statement, non-impersonation and non-fabrication rules, no consultation, modifier proposal, comparison
  table columns), with numbers only from the pinned profile and modifiers; add
  `src/agents/hermes/tests/test_profile_block.py` (every element present, weights equal to the pinned profile,
  re-render with modifiers, no handwritten numbers, Specialist Agent rendering unchanged) (FR-009, FR-010).
- [ ] T018 [P] [US1] Validate pinned Coach Agent artifacts in `src/agents/hermes/src/socalytics_hermes/artifacts.py`
  per research R4: exactly the eight kinds; schema and RFC 8785 digest per kind (`invalid-artifact`); group `coach`,
  route `user-to-coach`, the three rules, disclosure, exactly one `philosophy_profile` slot, equal dimension-set
  majors, weights keyed by exactly the pinned dimension set's ids with integers in `[0,100]` totalling 100, and modifier
  policy `perDimensionBound` ≤ 3, `maxPositiveMovement` ≤ 10, permitted categories only (`incompatible-artifact`); add
  `src/agents/hermes/tests/test_coach_artifacts.py` (valid seed-shaped artifacts pass; one case per rule with its
  reason) (FR-008).

### Hermes: Coach Agent execution

- [ ] T019 [US1] Execute `user-to-coach` attempts in `src/agents/hermes/src/socalytics_hermes/execution.py`: dispatch by
  route (`user-to-specialist` unchanged, `user-to-coach` with T018's validation and T017's rendering, anything else
  `unsupported-route`, not retryable), record the `profile-rendered` checkpoint (note `block <renderingVersion>;
  modifiers <n>`) before the first model call, offer only the session's MCP tools (no consultation), complete with the
  last snapshot and the runtime
  `hermes/<version>+hermes-runtime-section/1+hermes-coach-section/1+hermes-profile-block/1`, treat
  `400 applied-philosophy-profile-invalid` on a `profile-rendered` checkpoint and
  `409 applied-philosophy-profile-invalid` on the completion as `runtime-error`, and log `route` in
  `src/agents/hermes/src/socalytics_hermes/logging.py`; extend `src/agents/hermes/tests/conftest.py` and
  `src/agents/hermes/tests/fakes/` with Coach Agent pins, artifacts, and tool outcomes, add Coach Agent scenarios under
  `src/agents/hermes/tests/scripts/`, and add `src/agents/hermes/tests/test_coach_execution.py` (claim to completion
  with `get_match`, profile block in the system message sent to the scripted provider, checkpoint and completion
  content, no consultation tool offered, log records without weights or texts) (FR-007, FR-011, FR-015, FR-016a).

**Checkpoint**: User Story 1 is complete and testable on its own.

---

## Phase 4: User Story 2 - Philosophy context adapts only to evidence, within bounds (Priority: P2)

**Goal**: One evidence-backed modifier set per attempt is applied and recorded, or omitted with its rule; the platform
refuses snapshots that break any rule.

**Independent Test**: Scripted attempts propose valid and invalid sets; only the valid set is applied, re-rendered, and
recorded, and the platform refuses every invalid snapshot (spec User Story 2).

### Modifier evaluation and platform modifier checks

- [ ] T020 [P] [US2] Add the Hermes-local function tool `propose_context_modifiers` for Coach Agent attempts per research
  R6: record `callId` and platform `outcome` of every MCP tool result in
  `src/agents/hermes/src/socalytics_hermes/evidence.py`; validate the first proposal in
  `src/agents/hermes/src/socalytics_hermes/philosophy.py` (`modifiers` 1–5 items of `{ category, deltas, reason (1–300
  characters), evidenceCallIds (1–10 call identifiers) }`; rule codes `category-not-permitted`, `delta-out-of-bounds`,
  `not-budget-neutral`, `positive-movement-exceeded`, `negative-weight`, `evidence-missing`, `evidence-not-answered`,
  `unknown-dimension`, `reason-invalid`); on success record a new `profile-rendered` checkpoint and re-render the system
  message before the next model turn in `src/agents/hermes/src/socalytics_hermes/execution.py`; on failure, and for
  every later proposal (`already-evaluated`), record a `modifiers-omitted` checkpoint whose note is the rule code and
  continue; log `modifierOutcome` in `src/agents/hermes/src/socalytics_hermes/logging.py`; allow `{{callIds}}` in
  scripted tool-call arguments in `src/agents/hermes/src/socalytics_hermes/providers/scripted.py`; add
  `src/agents/hermes/tests/test_modifiers.py` and scripts under `src/agents/hermes/tests/scripts/` (one valid set; one
  case per rule; "analysis unavailable" and refused calls never back a modifier; repeated proposals; retry attempts
  start without modifiers; base vector unchanged when omitted; no reason text in logs) (FR-012 to FR-014, FR-016a).
- [ ] T021 [P] [US2] Prove the platform's modifier and evidence checks in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentModifierTests.cs`
  for checkpoints and completions: an accepted set referencing an answered `get_match` call; refusals for every
  modifier code of T005 (category not permitted or duplicated, unknown dimension, delta out of bounds, not
  budget-neutral, positive movement exceeded, negative weight, evidence call unknown, of another attempt, or not
  answered); nothing recorded and the attempt active after each refusal; correct the rules in
  `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/Catalog/AppliedPhilosophyProfileRules.cs` or the handlers
  of T010 only where a test shows a gap (FR-012, FR-013, FR-016).

**Checkpoint**: User Stories 1 and 2 work together.

---

## Phase 5: User Story 3 - Invalid or incompatible philosophy artifacts fail explicitly (Priority: P3)

**Goal**: Every invalid or incompatible Coach Agent artifact and every unsupported route ends the attempt explicitly,
before any model or tool call, without fallback.

**Independent Test**: A fake platform serves each corrupted artifact once; each attempt fails with the documented
reason and zero model and tool calls (spec User Story 3).

### Failure evidence

- [ ] T022 [P] [US3] Add `src/agents/hermes/tests/test_coach_failures.py`: for a missing, digest-mismatched, or
  schema-invalid dimension set, profile, or modifier policy (`invalid-artifact`); a profile missing or adding a
  dimension, with a non-integer or out-of-range weight, or not totalling 100; mismatched dimension-set majors; a
  modifier policy beyond the bounds; a safety policy without `user-to-coach` or a Coach Agent rule; an agent version
  without disclosure (`incompatible-artifact`); and envelopes with route `coach-to-specialist` or an unknown value
  (`unsupported-route`), assert one not-retryable failure report with that reason, zero scripted-provider calls, zero
  MCP sessions, and no fallback; correct `src/agents/hermes/src/socalytics_hermes/artifacts.py` or
  `src/agents/hermes/src/socalytics_hermes/execution.py` only where a test shows a gap (FR-007, FR-008, SC-003).
- [ ] T023 [P] [US3] Add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/CoachAgents/CoachAgentRouteTests.cs`:
  the route always follows the addressed agent's group, no member input can choose a route, and invocations submitted
  before this feature keep `user-to-specialist` and their pins (FR-004, FR-006).

**Checkpoint**: User Stories 1 to 3 are complete on the platform and in Hermes.

---

## Phase 6: User Story 4 - Members and Club Admins see Coach Agents for what they are (Priority: P4)

**Goal**: The Web UI offers Coach Agents with their disclosure, shows the disclosure and, on request, the applied
emphasis with every Coach Agent advice, and enables Coach Agent versions without the former warning.

**Independent Test**: Against the simulated API, add and ask a Coach Agent and check participants, advice, emphasis,
and lineage; enable a Coach Agent version in the Agent Catalog area; against the running platform, see the seeded Coach
Agents enabled and offered (spec User Story 4).

### Web UI adapters

- [ ] T024 [P] [US4] Regenerate the Kiota client from the refreshed OpenAPI snapshot, extend
  `src/clients/web/src/platform/models/agents.ts` per [data-model.md](data-model.md#web-ui-client-models-srcclientswebsrcplatformmodelsagentsts)
  (`AvailableAgent.group` `'coach' | 'specialist' | { raw: string }`, `disclosure: string | null`; `Turn`,
  `Invocation`, `Lineage` with `disclosure` and `appliedPhilosophyProfile` including derived rows
  `{ dimensionId, base, modifier, applied }` in `baseWeights` order; known checkpoint kinds `profile-rendered` and
  `modifiers-omitted`), map them in `src/clients/web/src/platform/adapters/agentConversationAdapter.ts`, and extend
  `src/clients/web/tests/unit/platform/adapters/agentConversationAdapter.test.ts` (FR-021 to FR-023, FR-028).
- [ ] T025 [P] [US4] Treat Coach Agent enablement like Specialist Agent enablement in the Agent Catalog area: remove the
  Coach Agent warning from `src/clients/web/src/features/agent-catalog/ChangeEnablementDialog.tsx`, the "not available
  in conversations yet" reason from `src/clients/web/src/features/agent-catalog/offer.ts`, the
  `coach-agent-enablement-unsupported` message from `src/clients/web/src/features/agent-catalog/outcomeText.ts` and its
  mapping from `src/clients/web/src/platform/adapters/agentCatalogAdapter.ts` (an unexpected occurrence shows as an
  unknown refusal with its raw code), remove the unused keys from `src/clients/web/src/i18n/en/agentCatalog.json`, and
  update the unit tests under `src/clients/web/tests/unit/features/agent-catalog/` and
  `src/clients/web/tests/unit/platform/adapters/agentCatalogAdapter.test.ts` (FR-024).

### Triage views

- [ ] T026 [US4] Show Coach Agents in the triage views: Coach Agent and Specialist Agent groups with display name,
  identifier, and disclosure in `src/clients/web/src/features/triage/ParticipantsPanel.tsx`, a Coach Agent label in
  `QuestionComposer.tsx`, the disclosure statement under the AI-generated disclosure in `AdviceCard.tsx` (advice text
  unaltered, rendered as text), the new collapsed-by-default `PhilosophyEmphasisPanel.tsx` (per-dimension base,
  modifier, applied, modifier reasons and evidence call identifiers, dimension-set, profile, and modifier-policy
  versions, the statement that weights change priority, not facts), and philosophy pins and `profile-rendered` and
  `modifiers-omitted` entries in `LineagePanel.tsx`; add the translation keys to
  `src/clients/web/src/i18n/en/triage.json`; extend the unit tests under `src/clients/web/tests/unit/features/triage/`
  (rendering as text, keyboard operation, announcements, axe rule checks, Specialist Agent views unchanged)
  (FR-021 to FR-023, FR-025).

### Browser tests

- [ ] T027 [US4] Add Coach Agents, disclosure, applied profiles, and Coach Agent lineage entries to the agent routes of
  `src/clients/web/tests/e2e/support/simulatedApi.ts` and add `src/clients/web/tests/e2e/simulated/triage-coach.spec.ts`
  (add a Coach Agent next to a specialist, ask it, see the disclosure, open the emphasis and the lineage, Viewer
  read-only view, axe checks) (FR-029).
- [ ] T028 [US4] Update `src/clients/web/tests/e2e/simulated/agent-catalog-coach.spec.ts` (enabling a Coach Agent
  version without a warning, offered once the available-agents read lists it) and
  `src/clients/web/tests/e2e/platform/agent-catalog.spec.ts` (against the running platform with the seed, the three
  Coach Agents are enabled and offered in conversations), adjusting the catalog routes of
  `src/clients/web/tests/e2e/support/simulatedApi.ts` where needed; no test-only platform hook (FR-024, FR-029).

**Checkpoint**: All user stories are independently functional.

---

## Phase 7: Polish & Cross-Cutting Concerns

### Cross-process evidence and documentation

- [ ] T029 [P] Add the Coach Agent case to `src/platform/Tests/SocAlytics.Platform.Images.Tests/AgentRuntimeStackTests.cs`
  with a scripted-provider scenario in the test's script resources for `coach-pep-guardiola` that calls `get_match`,
  proposes one `evidence-availability` modifier referencing that call, and answers with the five sections; assert that
  the invocation succeeds within 60 seconds, that the owner's reads show the disclosure, two `profile-rendered`
  checkpoints, the tool call, and the applied profile, and that the Specialist Agent case still passes (FR-030,
  SC-001, research R14).
- [ ] T030 [P] Make `docs/architecture/` describe the implemented state: re-read
  `docs/architecture/intelligence-agent-catalog.md` (introduction, Catalog Rules, Prompt And Advice Governance, Base
  Philosophy Profiles, Context Modifiers, Rendered CA Prompt Context, Implementation Waves, Validation And Promotion
  Gates), `intelligence-and-agents.md` (Agent Orchestration Authority, Client, Transport, And Runtime Protocol,
  Authorization, Lineage, And Lifecycle), `platform-implementation.md` (Agent Runtime), `client-applications.md` (Web UI,
  Connected Agent Conversations), `tenancy-and-technology.md` (AI), `security-and-data-governance.md`, `overview.md`,
  `terminology-and-principles.md`, and `README.md`, and correct every statement this feature makes false, including
  the design updates and status statements listed in [plan.md](plan.md#architecture-updates) as examples, not the
  whole job, and any other planned, future-tense, or "not implemented" wording about Coach Agents, philosophy profiles,
  context modifiers, profile rendering, the `User -> CA` route, or the agent seed; keep consultations, evaluation
  suites, promotion gates, profile values, and production values explicitly open; update `README.md` and `AGENTS.md`
  "Current State", `src/agents/README.md`, and `src/agents/hermes/README.md` to name direct Coach Agent conversations as
  development evidence; run `node .github/scripts/check-markdown.mjs` (Principle I).

### Final acceptance

- [ ] T031 Run the validation guide [quickstart.md](quickstart.md) sections 1 to 4 and confirm every expected result;
  record nothing as production readiness or model quality (FR-029, FR-030, SC-001 to SC-006).

---

## Dependencies & Execution Order

- **Phase 1 (Setup)**: no dependencies; T004 after T001–T003.
- **Phase 2 (Foundational)**: after Phase 1; T005, T006, and T007 in parallel.
- **User Story 1 (Phase 3)**: after Phase 2. T008 → T009 → T010 → T011 → T012 → T013 run alone (shared Agent
  Orchestration handlers, endpoints, and stores); T014 and T015 in parallel after T013; T016 after T014 and T015; T017
  and T018 in parallel; T019 after T017 and T018.
- **User Story 2 (Phase 4)**: after User Story 1 (T020 extends T017's and T019's modules; T021 uses T010's handlers);
  T020 and T021 in parallel.
- **User Story 3 (Phase 5)**: after User Story 1 (T022 uses T018 and T019; T023 uses T009); T022 and T023 in
  parallel; independent of User Story 2.
- **User Story 4 (Phase 6)**: after T016 (refreshed snapshot); T024 and T025 in parallel; T026 after T024; T027 after
  T026; T028 after T027 (shared `simulatedApi.ts`).
- **Polish (Phase 7)**: T029 after User Stories 1 and 2; T030 after all stories; T029 and T030 in parallel; T031 last.

## Parallel Example

```text
Phase 1, group 1: T001 work-notification 1.1.0 | T002 execution-envelope 1.2.0 | T003 attempt-report 1.2.0
Phase 2, group 1: T005 Domain rules | T006 migration | T007 Hermes contract copies
Phase 3, group 1: T014 simulated workload | T015 tool-call tests
Phase 3, group 2: T017 profile block | T018 Coach Agent artifact validation
Phase 4, group 1: T020 Hermes modifiers | T021 platform modifier tests
Phase 5, group 1: T022 Hermes failure tests | T023 route tests
Phase 6, group 1: T024 conversation adapter | T025 Agent Catalog area
Phase 7, group 1: T029 cross-process Coach Agent case | T030 documentation
```

## Implementation Strategy

1. **MVP**: Phases 1–3 (User Story 1): Coach Agents enabled, pinned, executed with the rendered profile, and read with
   disclosure and applied profile.
2. **Increment 2**: User Story 2 adds evidence-backed modifiers.
3. **Increment 3**: User Story 3 proves every failure path.
4. **Increment 4**: User Story 4 brings Coach Agents to the Web UI.
5. **Finish**: cross-process evidence, documentation, and acceptance.
