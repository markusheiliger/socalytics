# Research: Coach Agent Direct Conversations

All decisions build on the published and batch-authored specs this feature depends on; their code does not exist yet
on the default branch, so paths below are the ones their plans define. No `NEEDS CLARIFICATION` remained after the
spec's clarifications.

## R1 - Route value and contract releases

- **Decision**: The route of a direct Coach Agent invocation is `user-to-coach` (wire value), next to the existing
  `user-to-specialist`. `contracts/agent-orchestration/work-notification/v1` gains release 1.1.0 and
  `execution-envelope/v1` release 1.2.0, both widening the `route` enum to `user-to-specialist` and `user-to-coach`;
  earlier releases stay unchanged. The submission chooses the route from the addressed agent's group in the catalog
  baseline (`specialist` → `user-to-specialist`, `coach` → `user-to-coach`); no member input names a route. The
  forward-only migration `NNNN_agentorchestration_coach_agents.sql` (numbered at implementation time) relaxes the
  `agent_invocations.route` check constraint to both values.
- **Rationale**: FR-004 and FR-006; [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md)
  treats widening an enum as additive within a major; the values follow the catalog's `safety-policy` route names
  (`user-to-coach`, `user-to-specialist`, `coach-to-specialist`).
- **Alternatives considered**: A `v2` envelope (needless break); a member-chosen route (the group already determines
  it, and a choice would allow unsupported routes).

## R2 - Coach Agent enablement and the available-agents read

- **Decision**: `SetAgentEnablement` drops the `409 coach-agent-enablement-unsupported` refusal; the other refusals
  (`not-approved`, `retired`, `simulated-provider`, stale `If-Match`) apply to both groups unchanged. Problem type
  `coach-agent-enablement-unsupported` is no longer produced (kept out of the OpenAPI document). `listAvailableAgents`
  returns `disclosure` (string, null for Specialist Agents) taken from the enabled agent version's document;
  `addAgentConversationParticipant` accepts every enabled agent of either group.
- **Rationale**: FR-001 to FR-003; the catalog registry's lifecycle and enablement rules already cover both groups.
- **Alternatives considered**: A separate Coach Agent enablement operation (duplicates the catalog's single rule).

## R3 - Pinning the dimension set

- **Decision**: For a Coach Agent version, `SubmitAgentInvocation` additionally inserts an `agent_invocation_pins` row
  of kind `dimension-set` for the dimension set its pinned philosophy profile references (`(invocation_id, kind)`
  stays unique). The envelope's `agent.artifacts` and the owner's reads therefore list eight references; the
  attempt-scoped artifact read returns the pinned dimension set from the pin instead of resolving it from the profile.
- **Rationale**: FR-005; the lineage must name the dimension-set identifier, version, and digest
  ([Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance)),
  and pins make later catalog changes irrelevant to running invocations.
- **Alternatives considered**: Resolving the dimension set from the profile at read time (correct while records are
  immutable, but the digest would not be pinned or visible).

## R4 - Hermes artifact validation for Coach Agents

- **Decision**: For `user-to-coach`, Hermes requires exactly the kinds agent version, prompt template, model selection,
  tool policy, safety policy, dimension set, philosophy profile, and modifier policy; validates each against its
  copied `contracts/agent-catalog/<kind>/v1` release and its pinned RFC 8785 digest (`invalid-artifact` otherwise);
  then checks group `coach` for agent version, prompt, and safety policy, the safety policy's `routes` containing
  `user-to-coach` and `rules` containing `simulation-disclosure`, `non-impersonation`, and `non-fabrication`, the agent
  version's `disclosure`, the prompt's slots (exactly one `philosophy_profile`, others within the catalog's slot set),
  equal dimension-set majors of prompt, profile, and modifier policy, the profile's weight keys equal to the pinned
  dimension set's ids with integers in `[0,100]` totalling 100, and the modifier policy's `perDimensionBound` ≤ 3,
  `maxPositiveMovement` ≤ 10, and categories within the five permitted ones (`incompatible-artifact` otherwise). Both
  are not retryable and precede any model or MCP call. `user-to-specialist` keeps the Hermes Agent Runtime's checks;
  any other route fails `unsupported-route`. Additional copied releases: `dimension-set`, `philosophy-profile`,
  `modifier-policy` 1.0.0, `work-notification` 1.1.0, `execution-envelope` 1.2.0, `attempt-report` 1.2.0.
- **Rationale**: FR-007 and FR-008; [Rendered CA Prompt Context](../../docs/architecture/intelligence-agent-catalog.md#rendered-ca-prompt-context)
  lists the failure conditions and forbids silent fallback; re-checking registration rules makes a corrupted or
  substituted read visible.
- **Alternatives considered**: Trusting the platform's registration validation (no evidence that the executed profile
  is valid); falling back to the base prompt without a profile (forbidden by the architecture).

## R5 - Profile block rendering (`hermes-profile-block/1`)

- **Decision**: The `{{philosophy_profile}}` slot is replaced by a deterministic plain-text block, versioned
  `hermes-profile-block/1`, in this order: a heading line; `Dimension set: <id> <version>`; `Philosophy profile: <id>
  <version> (<digest>)`; a table `Dimension | Meaning | Base | Modifier | Applied` with one row per dimension in the
  dimension set's order (modifier `0` and applied = base when none is applied) and a total row of 100; `Modifier
  policy: <id> <version>; per dimension -<b>..+<b>; at most <m> positive points; budget-neutral; categories: <list>`;
  `Applied modifiers: none` or one line per modifier with category, deltas, reason, and evidence call identifiers; and
  the fixed instruction that weights order investigation, consultation suggestions, recommendation priority, and
  tradeoff framing but never change facts, evidence availability, or confidence, that significant lower-weight
  evidence must be preserved, that missing dimension evidence is unavailable and not zero, and that weights are shown
  only when the user asks to compare philosophies or inspect prioritization. All numbers come from the pinned profile
  and the applied modifiers. A Coach Agent section, `hermes-coach-section/1`, follows the existing runtime section and
  states the agent version's disclosure statement, the non-impersonation and non-fabrication rules, that no other
  agent may be consulted or addressed (suggestions only under "Next questions or consultations"), how modifiers may
  be proposed, and the comparison table columns of [Context Modifiers](../../docs/architecture/intelligence-agent-catalog.md#context-modifiers).
  The declared runtime of a Coach Agent completion is
  `hermes/<package version>+hermes-runtime-section/1+hermes-coach-section/1+hermes-profile-block/1`.
- **Rationale**: FR-009 and FR-010; the prompt artifact holds the rendering contract (the slot) and the profile is the
  only source of numbers, so no second handwritten matrix exists.
- **Alternatives considered**: Rendering JSON (harder to read for the model and the reviewers of evaluation suites);
  putting the disclosure into the prompt template only (the agent version's statement is the governed one).

## R6 - Context modifier proposal and validation

- **Decision**: For Coach Agent attempts only, Hermes adds one Hermes-local function tool,
  `propose_context_modifiers`, next to the MCP session tools. It is not an MCP tool, reads nothing, and is never sent to
  the platform. Arguments: `modifiers` (1–5 items) of `{ category, deltas: { <dimension id>: integer }, reason (1–300
  characters), evidenceCallIds (1–10 call identifiers) }`. Hermes evaluates only the first proposal of an attempt:
  categories within the pinned policy's `contextCategories`; each delta an integer within `±perDimensionBound` and keyed
  by a dimension of the pinned set; at most one modifier per category; per dimension, the summed deltas within
  `±perDimensionBound`; the total of all deltas 0; the sum of positive summed deltas ≤ `maxPositiveMovement`; every
  applied weight ≥ 0; every evidence call identifier one of this attempt's MCP tool results whose platform `outcome`
  is `answered` (Hermes records `callId` and `outcome` of every result it passes to the model). A valid set is
  applied, recorded (R7), and the system message is rendered again before the next model turn; an invalid set is
  omitted with the first violated rule code (`category-not-permitted`, `delta-out-of-bounds`, `not-budget-neutral`,
  `positive-movement-exceeded`, `negative-weight`, `evidence-missing`, `evidence-not-answered`, `unknown-dimension`,
  `reason-invalid`), recorded as a `modifiers-omitted` checkpoint whose note is the rule code, and the tool result tells
  the model the set was not applied. Later proposals return "already evaluated" and record `modifiers-omitted` with
  `already-evaluated`. The proposal counts toward the model-turn bound like any tool call.
- **Rationale**: FR-011 to FR-014 and the clarification; the modifier policy defines bounds, not rules, so the model
  proposes and Hermes and the platform enforce the architecture's invariants deterministically; "analysis
  unavailable" results cannot back a modifier, so missing evidence never redistributes weights.
- **Alternatives considered**: Deterministic context rules in Hermes (a second, unversioned authority outside the
  catalog); no modifiers at all (contradicts the feature scope); letting the platform evaluate proposals (an extra
  workload operation without additional safety, since the platform re-checks at checkpoint and completion).

## R7 - Applied profile snapshot in `attempt-report` 1.2.0

- **Decision**: `attempt-report/v1` gains release 1.2.0 with one optional closed object, `appliedPhilosophyProfile`,
  allowed on a checkpoint whose `kind` is `profile-rendered` (required there) and on a completion; its shape is in
  [contracts/attempt-report-1.2.0.md](contracts/attempt-report-1.2.0.md). Hermes records `profile-rendered` before the
  first model call (no modifiers) and again after applied modifiers; the completion repeats the last one. The platform
  validates both with `AppliedPhilosophyProfileRules` (Domain): present only for `user-to-coach` attempts and required
  on their completions; references equal to the pins; base weights equal to the pinned profile; modifiers within the
  pinned policy and R6's rules; every evidence call identifier an `agent_tool_calls` row of the same attempt with
  outcome `answered`; applied = base + summed deltas; and, for completions, equal to the attempt's last
  `profile-rendered` checkpoint. Refusals: checkpoint `400 applied-philosophy-profile-invalid`, completion
  `409 applied-philosophy-profile-invalid`, both with `violations` codes, nothing recorded, the attempt stays active.
  Valid snapshots are stored in the lineage entry payloads (existing `jsonb`) and on the accepted advice in a new
  nullable `jsonb` column `applied_philosophy_profile` of `agent_accepted_advice` (same migration as R1).
- **Rationale**: FR-015 to FR-017 and the clarification; the architecture requires the exact rendered profile and
  modifier snapshot for every successful or failed attempt, and checkpoints are the only workload record of attempts
  that never complete.
- **Alternatives considered**: Snapshot only at completion (failed attempts lose it); a new workload operation
  (checkpoints already have ordering, idempotency, fencing, and lineage).

## R8 - Disclosure in reads

- **Decision**: Every owner read that returns advice (`listAgentConversationTurns`, `getAgentInvocation`,
  `getAgentInvocationLineage`) adds `disclosure` (string or null) taken from the pinned agent version's document, and
  `appliedPhilosophyProfile` (object or null) from the accepted advice; invocation and lineage reads also list the
  `dimension-set` pin. The advice text is never changed.
- **Rationale**: FR-017 and FR-018 and the clarification; the pinned version's statement is the governed disclosure,
  independent of model output.
- **Alternatives considered**: Requiring the model to write the disclosure into the advice (unverifiable wording,
  duplicates in history).

## R9 - Tool operations and MCP server for Coach Agent attempts

- **Decision**: No code change: the agent tool operations bind calls to the attempt and the pinned tool policy
  regardless of route, and the MCP server lists and forwards the pinned policy's tools for any attempt. Integration
  tests and the cross-process test prove it for `user-to-coach` attempts.
- **Rationale**: FR-019; the "All CAs" baseline equals the tool allowlist, so pinned Coach Agent tool policies are
  ordinary tool policies.
- **Alternatives considered**: Route-specific tool checks (the pinned tool policy already is the authority).

## R10 - Simulated agent workload

- **Decision**: `SocAlytics.Platform.SimulatedAgentWorkload` handles `user-to-coach` by reading the pinned artifacts,
  recording a `profile-rendered` checkpoint with the base vector and no modifiers, calling the tools it already calls,
  and completing with simulated advice (marked simulated, five sections) and the same snapshot.
- **Rationale**: FR-020; the AppHost and the Web UI's platform tests keep working with Coach Agents enabled by the seed.
- **Alternatives considered**: Leaving Coach Agent invocations to Hermes only (local AppHost runs do not include
  Hermes).

## R11 - Development and Test Coach Agent seed

- **Decision**: `AgentCatalogSeeder` gains a Coach Agent step that runs in Development and Test when no artifact with
  group `coach` and no `dimension-set`, `philosophy-profile`, or `modifier-policy` exists, independently of the
  Specialist Agent step, using the same validator and stores (actor `system:seed`, audited). Records under
  `Infrastructure/AgentOrchestration/Catalog/Seed/coach/`: dimension set `coach-philosophy-dimensions` 1.0.0 (the eight
  dimensions of [Dimension Set](../../docs/architecture/intelligence-agent-catalog.md#dimension-set) with their accepted
  evidence tools); profiles `coach-jurgen-klopp`, `coach-pep-guardiola`, `coach-jose-mourinho` 1.0.0 with the
  Provisional weights, summarized rationale and provenance, author and reviewers marked as Development seed values;
  modifier policy `coach-context-modifiers` 1.0.0 (major 1, bound 3, movement 10, all five categories); tool policy
  `coach-tools` 0.1.0 (the "All CAs" baseline); safety policy `coach-safety` 0.1.0 (group `coach`, route
  `user-to-coach`, the three Coach Agent rules, hard boundaries from the Coach Agent drafts); the seed's existing
  `simulated` model selection (registered again with equal content, which replays); and prompt templates
  `coach-jurgen-klopp`, `coach-pep-guardiola`, `coach-jose-mourinho` 0.1.0 copied from the
  [First-Draft System Prompts](../../docs/architecture/intelligence-agent-catalog.md#first-draft-system-prompts) with
  the "Hermes resolved …" paragraph, weight table, and modifier sentence replaced by `{{philosophy_profile}}`
  (`dimensionSetMajor` 1) and the consultation paragraph replaced by one that suggests specialist questions to the
  human coach, because consultations do not exist yet. Agent versions 0.1.0 of the three Coach Agents carry a
  disclosure statement and are registered, approved (distances 0.25 to 0.31, no acknowledgement), and enabled.
- **Rationale**: FR-026 and the clarification; the Web UI's platform tests and local runs need enabled Coach Agents;
  replacing the weight table keeps the profile the single numeric source.
- **Alternatives considered**: Only registering without enabling (local conversations and catalog platform tests
  would need manual enablement); copying prompts verbatim (two weight sources, and an instruction to consult that
  Hermes cannot honor).

## R12 - Audit and telemetry

- **Decision**: Orchestration audit events for submission, claim, completion, failure, expiry, and scope loss add the
  detail `route`; `agent.catalog.enablement.changed` already covers Coach Agent versions. The orchestration meter's
  submission, claim, completion, and failure counters gain the tag `route`. A refused applied profile is audited as
  `agent.attempt.report.refused` with the violation codes only. The audit detail allow-list stays without weights,
  reasons, or texts.
- **Rationale**: FR-027.
- **Alternatives considered**: Separate Coach Agent counters (a tag keeps one series per operation).

## R13 - Web UI

- **Decision**: In `src/clients/web`: the agents model gains `group` (`coach`, `specialist`, or raw) and
  `disclosure`; turns and invocation and lineage models gain `disclosure` and `appliedPhilosophyProfile`; the adapter
  maps them from the regenerated Kiota client. `ParticipantsPanel` lists Coach Agents and Specialist Agents in two
  labelled groups, each Coach Agent with its disclosure; `QuestionComposer` labels Coach Agents; `AdviceCard` shows the
  disclosure statement under the AI-generated disclosure; a new `PhilosophyEmphasisPanel` (collapsed by default,
  opened on request) shows the per-dimension base, modifier, and applied weights, modifiers with reasons and evidence
  call identifiers, the dimension-set, profile, and modifier-policy versions, and the statement that weights change
  priority, not facts; `LineagePanel` shows the philosophy pins and `profile-rendered` and `modifiers-omitted`
  entries. In the Agent Catalog area, `ChangeEnablementDialog` drops the Coach Agent warning, `offer.ts` drops the
  "not available in conversations yet" reason, and `outcomeText.ts` drops the `coach-agent-enablement-unsupported`
  message. New text lives in the `triage` and `agentCatalog` namespaces. Tests: adapter and view unit tests, simulated
  e2e `triage-coach.spec.ts` with Coach Agent routes in `simulatedApi.ts`, an updated simulated agent catalog test, and
  the platform agent catalog test asserting the seeded Coach Agents are enabled and offered.
- **Rationale**: FR-021 to FR-025 and the browser-test clarification; adapters keep the Web UI behind the generated
  client.
- **Alternatives considered**: Showing the emphasis always (crowds every advice; the architecture shows weights when
  asked).

## R14 - Cross-process evidence and environment

- **Decision**: `AgentRuntimeStackTests` (from the Hermes Agent Runtime) gains a Coach Agent case: with the seed's
  Coach Agents enabled, the Hermes script for `coach-pep-guardiola` calls `get_match`, proposes one
  `evidence-availability` modifier referencing that call, and answers; the test asserts the succeeded invocation, the
  disclosure, the `profile-rendered` checkpoints, the tool call, and the applied profile in the owner's reads. No new
  framework, SDK, or tool is needed: Python projects under `src/agents/` are verified by the environment of
  [Agents Python Verification Environment](../20261018-110526-agents-python-environment/spec.md), and the platform,
  Web UI, Docker, and image tests use the existing verification.
- **Rationale**: FR-030; constitution Technology and Tooling.
- **Alternatives considered**: A separate stack test class (duplicates the stack fixture).
