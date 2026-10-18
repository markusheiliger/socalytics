# Research: Delegated Coach-to-Specialist Consultations

All decisions build on the published and batch-authored specs this feature depends on; their code does not exist yet on
the default branch, so paths and names below are the ones their plans define. No `NEEDS CLARIFICATION` remained after
the spec's clarifications.

## R1 - Consultation policy artifact and where the allowed Specialists live

- **Decision**: A new catalog artifact kind `consultation-policy` with the contract
  `contracts/agent-catalog/consultation-policy/v1` (release 1.0.0; closed object): `schemaVersion`, `kind`, kebab-case
  `id` (3–100), SemVer `version`, `allowedSpecialists` (1–6 unique identifiers), `maxConsultationsPerInvocation`
  (integer 1–6), `maxQuestionCharacters` (integer 1–2,000), `maxDecisionSupportCharacters` (integer 1–1,000), and
  `onUnavailable` (`continue` | `fail`). `agent-version/v1` gains release 1.1.0 with the optional reference
  `artifacts.consultationPolicy { id, version, digest }`; release 1.0.0 documents stay valid. Catalog rules (Domain
  `Catalog/AgentCatalogRules`) add the violation codes `consultation-target-invalid` (an allowed identifier that is not
  a Specialist Agent of the catalog baseline), `consultation-policy-not-allowed` (referenced by a Specialist Agent
  version), and `consultation-route-missing` (a Coach Agent version with a consultation policy whose safety policy does
  not list `coach-to-specialist`). The safety-policy route rule accepts `coach-to-specialist` in the safety policies of
  both groups: for a Coach Agent it permits requesting consultations, for a Specialist Agent being consulted (if the
  registry implemented the rule for one group only, this feature widens it). The submission pins the consultation
  policy as `agent_invocation_pins` kind `consultation-policy` with the version's other artifacts.
- **Rationale**: FR-001 to FR-003 and the spec's assumption: the allowed list, bound, and unavailable behavior are the
  parent's "versioned orchestration policy" of [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol);
  as a pinned catalog artifact it is reviewed, immutable, content-addressed, and never changes for running work.
  Requiring the route on both sides keeps Specialists that were never reviewed for delegated questions out of reach.
- **Alternatives considered**: A field of the safety policy (mixes safety rules with orchestration limits and would
  change every Coach Agent safety policy's meaning); stamp configuration (not versioned or pinned, so running
  invocations could change behavior); a field of the agent version (no reuse across versions and no own digest in the
  lineage).

## R2 - Route value and contract releases

- **Decision**: The child route is `coach-to-specialist` (the catalog's existing safety-policy route name).
  `work-notification/v1` gains release 1.2.0 and `execution-envelope/v1` release 1.3.0, both widening `route` to
  `user-to-specialist`, `user-to-coach`, and `coach-to-specialist`; the envelope's `agent.artifacts[].kind` adds
  `consultation-policy` and the envelope adds the optional `consultations` (parent) and `consultation` (child) objects
  of [contracts/consultation-interfaces.md](contracts/consultation-interfaces.md). `attempt-report/v1` gains release
  1.3.0 with the completion's optional `consultationInfluence`. A new input contract
  `contracts/agent-orchestration/consultation-request/v1` (release 1.0.0) defines the request body. Earlier releases
  stay unchanged; every new release has golden fixtures, including an invalid notification with `route: "coach-to-coach"`
  and an invalid request with an extra `route` property.
- **Rationale**: FR-005, FR-017, FR-019, FR-037; [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md)
  treats enum widening and optional properties as additive within a major.
- **Alternatives considered**: A `v2` envelope (needless break); reusing `attempt-report` for the request (a request is
  not a report of the attempt's progress and has its own refusals and answer).

## R3 - Durable state: consultations, waiting edges, and child invocations

- **Decision**: One forward-only migration `NNNN_agentorchestration_consultations.sql` (numbered at implementation
  time) adds the versioned table `agent_consultations` (one row per consultation; a `pending` row is the parent's waiting
  edge) and changes `agent_invocations` (nullable `parent_invocation_id`, `consultation_id`, and
  `inherited_tool_policy_digest`; the route, state, and failure-reason checks widened), `agent_attempts` (end reasons
  `parent-ended` and `consultation-unavailable`), `agent_lineage_entries` (consultation kinds),
  `agent_workload_operations` (kind `consult`), and `agent_accepted_advice` (nullable `consultation_influence jsonb`);
  see [data-model.md](data-model.md). A child invocation is a row of `agent_invocations` with the parent's
  `conversation_id`, `team_id`, `match_id`, `analysis_run_id`, `owner_member_id`, and `correlation_id`, its own `id`,
  pins, `policy_version`, and attempts.
- **Rationale**: [Durable State And Transaction Boundaries](../../docs/architecture/intelligence-and-agents.md#durable-state-and-transaction-boundaries)
  commits the child consultation and invocation, the parent waiting edge and state, and the child outbox event in one
  transaction; children reuse every claim, lease, fencing, retry, and stale rule unchanged because they are ordinary
  invocations.
- **Alternatives considered**: A separate child-invocation table (duplicates the attempt and lineage machinery); a
  separate waiting-edge table (the pending consultation already is the edge, with one row per child).

## R4 - Consultation request operation and its checks

- **Decision**: `requestAgentConsultation`: `POST /api/v1/agent-orchestration/attempts/{attemptId}/consultations`
  (policy `AgentWorkload`) with the `consultation-request` 1.0.0 body `{ operationId, fencingToken, agentId, question,
  knownEvidenceCallIds, decisionSupport }`. In one unit of work, with the parent invocation row locked, the handler
  checks in this order: operation-identifier replay or conflict; the current attempt, token, lease, and deadline
  (`409 agent-attempt-stale` with its disposition); the parent's route `user-to-coach` and no `parent_invocation_id`,
  and the target in the catalog baseline's Specialist group (`409 consultation-refused`, `reason:
  route-not-supported`); the owner's right to submit (scope loss: fence, fail as `scope-lost`, answer the stale
  disposition `scope-lost`); a pinned consultation policy listing the target (`not-allowed`); the target's enablement
  row (`agent-not-available`); the enabled version's safety policy permitting `coach-to-specialist`
  (`not-consultable`); its tool policy within the parent's pinned tool policy (`tool-policy-not-inherited`); the count
  of the parent's consultations below `maxConsultationsPerInvocation` (`limit-reached`); then the request fields
  (`400 consultation-request-invalid` with `violations` for an empty or too long question or decision support, and for
  a known-evidence call identifier that is not a `tool-call` lineage entry of the same attempt with outcome
  `answered`). A valid request inserts the consultation, the child invocation with its pins (the target's enabled
  version and artifacts, as at submission) and `consultation-invocation/1` policy, lineage entries on both
  invocations, the operation record, and the child's `agent-invocation-ready` outbox message, sets the parent to
  `waiting-for-consultations`, increments its `progress_version`, and answers `201 { consultationId,
  childInvocationId, sequence, state: "pending" }` (replay `200`). Refusals record nothing except the audit event.
- **Rationale**: FR-004 to FR-009; the order puts stale and route checks first so that no unauthorized or looping
  request learns anything about policies or enablement, and re-evaluates authorization at delegation as
  [Authorization, Lineage, And Lifecycle](../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle)
  requires.
- **Alternatives considered**: Recording refused requests as lineage entries (the spec requires that refusals create
  nothing; the audit trail and Hermes' content-free checkpoint keep them traceable).

## R5 - Parent and child state transitions

- **Decision**: `agent_invocations.state` adds `waiting-for-consultations` and `synthesizing` (API names unchanged).
  Transitions:
  - consultation created → parent `waiting-for-consultations` (from `running` or `synthesizing`);
  - child succeeds (in the child's completion transaction, parent row locked) → consultation `answered`; when no
    pending consultation remains: parent `synthesizing` if it has an active attempt, otherwise `queued` with a new
    `ready_occurrence` and outbox message;
  - child fails with `attempts-exhausted` or a non-retryable failure (including `agent-not-available` at claim) →
    consultation `unavailable` with the child's failure reason; then the pinned `onUnavailable`: `continue` resolves as
    above; `fail` fails the parent with `consultation-unavailable`, fences its active attempt (end reason
    `consultation-unavailable`), and ends the other unresolved children;
  - parent attempt ends retryably (lease expiry, deadline, retryable failure, `runtime-stopping`) with a pending
    consultation → parent `waiting-for-consultations` without a current attempt and no notification while attempts
    remain; with no attempts left → parent `failed` (`attempts-exhausted`);
  - claim of a parent with only resolved consultations → `synthesizing`, otherwise `running` as before; only `queued`
    invocations are claimable;
  - parent ends `failed` for any reason → every unresolved child fails with `parent-ended` (active attempt fenced, end
    reason `parent-ended`, never retried) and its consultation becomes `ended`;
  - scope loss detected at any parent or child operation, claim, or monitor check → all active attempts of the parent
    and its children fenced, the parent and every unresolved child failed `scope-lost`, consultations `ended`.
  Every state change checks the expected version and allowed prior state; child outcome delivery locks the parent row
  before the child row so concurrent child completions serialize on the parent.
- **Rationale**: FR-010 to FR-016; the architecture's "Accept child outcome" commits accepted child advice and lineage,
  child terminal state, and parent resume eligibility in one step, and parent waiting edges and accepted child outcomes
  survive restarts because they are rows, not Hermes memory.
- **Alternatives considered**: Ending the parent attempt whenever it requests a consultation and always resuming in a
  new attempt (consumes the parent's attempts on every consultation and discards the model's in-memory work even
  without failures); failing the parent when its attempt ends while waiting (contradicts restart survival).

## R6 - Execution policies for consulting parents and children

- **Decision**: Submission records `consulting-invocation/1` instead of `direct-invocation/1` when the pinned Coach
  Agent version references a consultation policy; it has the same fields with a longer maximum attempt duration
  (development: 3 attempts, 30-second lease, 900-second attempt maximum, other values as `direct-invocation/1`). Children
  record `consultation-invocation/1` (development: 3 attempts, 30-second lease, 300-second attempt maximum, 10 history
  turns, 16,000 advice characters, 1,000 note characters). Options: `AgentOrchestration:Execution:ConsultingParent`
  and `AgentOrchestration:Execution:Consultation`. The owner's unfinished-invocation count considers only invocations
  without `parent_invocation_id`.
- **Rationale**: FR-011, FR-011a, and the clarification; a waiting attempt renews its lease but its maximum duration
  must cover a child's attempts.
- **Alternatives considered**: Raising `direct-invocation/1`'s maximum for every invocation (weakens the bound for
  direct work); pausing the parent's deadline while waiting (a second, hidden clock).

## R7 - Envelopes and the attempt-scoped consultation read

- **Decision**: `execution-envelope` 1.3.0 adds, for parents, `consultations` (0–6 items in sequence order:
  `consultationId`, `sequence`, `agentId`, `agentVersion`, `question`, `decisionSupport`, `state`, `response` when
  answered, `unavailableReason` when unavailable) and, for children, `consultation` (`consultationId`,
  `parentInvocationId`, `requestingAgentId`, `decisionSupport`, `knownEvidence` of `{ callId, toolId, outcome,
  evidenceReferences }` from the parent attempt's `tool-call` lineage entries, `initiatingQuestion`); a child's
  `question` is the consultation question and its `history` the conversation's earlier succeeded top-level turns.
  `readAgentConsultationOutcomes`: `POST /api/v1/agent-orchestration/attempts/{attemptId}/consultation-outcomes` with
  `{ fencingToken }` returns `{ invocationState, consultations }` in the envelope's shape, with the stale-disposition
  rules of the pinned-artifact read; it records no operation.
- **Rationale**: FR-017, FR-018, FR-027; the parent learns outcomes from durable state both while it waits and after a
  restart, so no consultation is repeated.
- **Alternatives considered**: Pushing outcomes through NATS to the waiting attempt (transport would carry content and
  become a second authority); a member-style `GET` (the read must be fenced to the current attempt).

## R8 - Parent completion and influence declaration (`attempt-report` 1.3.0)

- **Decision**: The completion gains the optional array `consultationInfluence` (0–6 items `{ consultationId, use:
  adopted | partially-adopted | not-adopted | unavailable, agreement: agrees | conflicts | not-applicable, note: 1–300
  characters }`). `completeAgentAttempt` refuses, nothing recorded and the attempt staying active, with
  `409 consultations-pending` while a consultation of the parent is pending, and with `409 consultation-influence-invalid`
  and `violations` (`missing`, `duplicate`, `unknown-consultation`, `unavailable-mismatch`, `not-a-parent`) when the
  declaration does not cover every consultation exactly once, names another parent's consultation, marks an
  unavailable one other than `unavailable` or an answered one `unavailable`, or is present on an invocation without
  consultations. A valid declaration is stored in `agent_accepted_advice.consultation_influence` and the `completed`
  lineage entry. Checkpoint kinds Hermes adds (content-free notes): `consultation-requested` (`consultation <n>;
  <agentId>`), `consultation-refused` (`<agentId>; <reason>`), and `consultations-resolved` (`answered <a>; unavailable
  <u>`).
- **Rationale**: FR-019, FR-020, FR-026; adoption and agreement make the influence and conflicts visible and checkable
  without parsing advice text.
- **Alternatives considered**: Inferring influence from the advice text (unverifiable); a free-text field only (no
  structured conflict marker for the Web UI or later evaluation suites).

## R9 - Member reads

- **Decision**: `listAgentConversationTurns`, `getAgentInvocation`, and `getAgentInvocationLineage` of a parent add
  `consultations` (sequence order): `consultationId`, `sequence`, `agentId`, `agentDisplayName`, `agentVersion`,
  `question`, `decisionSupport`, `knownEvidence`, `state` (`pending`, `answered`, `unavailable`, `ended`),
  `childInvocationId`, `childState`, and, once the parent has ended, `response`, `evidence` (the child's `tool-call`
  entries: `callId`, `toolId`, `outcome`, `evidenceReferences`), `executionReferences`, `unavailableReason`, and
  `influence` (`use`, `agreement`, `note`). States `waiting-for-consultations` and `synthesizing` appear in the
  invocation and turn reads. Turns list only invocations without `parent_invocation_id`. `getAgentInvocation` and
  `getAgentInvocationLineage` of a child are served to the parent's owner under the same rule once the parent has ended
  (not found before, consistent with the response returning only to the parent) and add `parentInvocationId` and
  `consultationId`. Lineage entry kinds `consultation-requested`, `consultation-answered`, `consultation-unavailable`,
  and `consultation-ended` on the parent and `consultation-created` on the child carry identifiers only. A parent's
  `progress_version` and `ETag` change with every consultation change.
- **Rationale**: FR-020, FR-021, and the visibility clarification.
- **Alternatives considered**: Separate consultation endpoints (more polling for the Web UI; the parent's reads
  already carry the turn).

## R10 - Hermes

- **Decision**: In `src/agents/hermes`:
  - `artifacts.py`: accepts the `consultation-policy` kind; a Coach Agent version with one requires the safety policy
    route `coach-to-specialist` (`incompatible-artifact` otherwise); route `coach-to-specialist` requires a Specialist
    Agent version without a consultation policy whose safety policy lists `coach-to-specialist`, and the envelope's
    `consultation` object (`incompatible-artifact` otherwise); other routes stay `unsupported-route`.
  - `consultations.py` (new): the Hermes-local function tools `request_consultation` (`specialistId`, `question`,
    `knownEvidenceCallIds` 0–10, `decisionSupport`) and `declare_consultation_influence` (the R8 items), offered only to
    Coach Agent attempts with a pinned consultation policy; local refusals for a target outside the list or the bound
    (counting the envelope's consultations); operation identifiers `hermes-<attemptId>-consult-<n>`; platform refusals
    are returned to the model as the function result with a `consultation-refused` checkpoint; stale answers abandon
    the attempt.
  - `execution.py`/`runner.py`: all `request_consultation` calls of one model turn are sent, then the attempt moves to
    the waiting pool (`SOCALYTICS_HERMES_MAX_WAITING_ATTEMPTS`, development 8, separate from
    `SOCALYTICS_HERMES_MAX_CONCURRENT_ATTEMPTS`) and polls `readAgentConsultationOutcomes` every
    `SOCALYTICS_HERMES_CONSULTATION_POLL_SECONDS` (development 2) while renewing the lease; when every consultation of the
    turn is resolved it waits for a free execution slot, records `consultations-resolved`, and returns each result
    (attributed response or unavailable notice) to the model. While the waiting pool is full, no further work is
    fetched. The deadline margin applies while waiting.
  - `rendering.py`: Coach Agent versions with a consultation policy get `hermes-coach-section/2` (the
    `hermes-coach-section/1` statements except the no-consultation rule, plus the allowed Specialists, the remaining
    bound, bounded questions, synthesis without hiding disagreement, the influence declaration, and never consulting a
    Coach Agent); earlier consultations from the envelope are added as attributed consultation messages; children get
    the Specialist rendering plus `hermes-consultation-section/1` (requesting Coach Agent, answer returns only to it,
    decision support, known evidence references, the member's question as context, no consultation or address of
    another agent) with the consultation question as the user message.
  - `advice.py`: before completing a parent with consultations, a missing or invalid declaration triggers one extra
    model turn asking for it; otherwise the attempt fails `advice-invalid` (retryable).
  - Declared runtime: `hermes/<version>+hermes-runtime-section/1+hermes-coach-section/2+hermes-profile-block/1` for
    consulting Coach Agents and `hermes/<version>+hermes-runtime-section/1+hermes-consultation-section/1` for children.
  - `logging.py`: fields `consultationId` and `consultationOutcome`; never questions, decision support, responses, or
    notes.
  - Copied contract releases: `consultation-policy` 1.0.0, `agent-version` 1.1.0, `work-notification` 1.2.0,
    `execution-envelope` 1.3.0, `attempt-report` 1.3.0, `consultation-request` 1.0.0.
- **Rationale**: FR-022 to FR-028 and the refused-request clarification; a separate waiting pool keeps a waiting Coach
  Agent from starving the child it waits for on the same instance.
- **Alternatives considered**: Blocking an execution slot while waiting (deadlocks a single-slot instance); letting the
  model poll (outcomes are a platform fact, not a model decision).

## R11 - Development and Test seed

- **Decision**: `AgentCatalogSeeder` gains a consultation step after the Coach Agent step, applied in Development and
  Test when no `consultation-policy` artifact exists (actor `system:seed`, audited, same validator and stores). Records
  under `Infrastructure/AgentOrchestration/Catalog/Seed/consultations/`: consultation policies
  `coach-jurgen-klopp-consultations`, `coach-pep-guardiola-consultations`, `coach-jose-mourinho-consultations` 0.1.0
  with the allowed Specialists of the catalog's "Typical SA consultations" column (Klopp: fitness, tactics, video
  analysis, player development; Guardiola: tactics, video analysis, goalkeeping, player development; Mourinho: tactics,
  video analysis, scouting, goalkeeping), `maxConsultationsPerInvocation` 3, `maxQuestionCharacters` 1,000,
  `maxDecisionSupportCharacters` 500, `onUnavailable` `continue`; the next minor versions of the seeded Coach Agent
  safety policy and of the two seeded Specialist safety policies with `coach-to-specialist` added; Coach prompt
  template versions 0.2.0 equal to 0.1.0 except that the consultation paragraph of the
  [First-Draft System Prompts](../../docs/architecture/intelligence-agent-catalog.md#first-draft-system-prompts) is
  restored verbatim; and agent versions 0.2.0 of the three Coach Agents and the two seeded Specialists that reference
  them, registered, approved, and enabled. Only the seeded Specialists are enabled, so consulting the others is refused
  as `agent-not-available`, which tests use.
- **Rationale**: FR-029 and the seed clarification; new versions keep the Coach Agent seed's 0.1.0 records immutable.
- **Alternatives considered**: Changing the Coach Agent seed's 0.1.0 records (registered records never change);
  enabling all six Specialists (the catalog registry seeds only the first-wave Specialists).

## R12 - Simulated agent workload

- **Decision**: `SocAlytics.Platform.SimulatedAgentWorkload` executes `coach-to-specialist` children like direct
  Specialist invocations (honoring the existing `[simulate:*]` markers at the start of the consultation question), and
  interprets the Coach Agent question marker `[simulate:consult:<specialistId>]` or
  `[simulate:consult:<specialistId>:<behavior>]`: it records its profile checkpoint, requests one consultation whose
  question starts with `[simulate:<behavior>]` when given, polls the consultation outcomes, and completes with simulated
  advice and a valid influence declaration (`adopted`/`agrees` for answered, `unavailable`/`not-applicable` otherwise).
  Without the marker it behaves as before.
- **Rationale**: FR-030; platform integration tests and the AppHost exercise consultations without Hermes.
- **Alternatives considered**: Consulting in every simulated Coach Agent invocation (changes existing tests and the Web
  UI's platform flows).

## R13 - Web UI

- **Decision**: In `src/clients/web`: `platform/models/agents.ts` adds the states `waiting-for-consultations` and
  `synthesizing`, the `Consultation` model (R9 fields), and child references; `agentConversationAdapter.ts` maps them
  (unknown values stay raw); `turnPresentation.ts` words the two states ("Consulting specialists" with the list of
  requested Specialists and their states; "Combining the specialists' answers") and keeps polling;
  `features/triage/ConsultationList.tsx` (new) renders, below `AdviceCard`, each consultation with attribution,
  question, response or worded unavailable or ended reason, evidence references, adoption, agreement, and note, with a
  conflict marked as a disagreement; `LineagePanel.tsx` words the consultation entry kinds and opens a child's lineage.
  In the Agent Catalog area: `catalogLimits.ts` adds the consultation-policy limits and enumerations;
  `artifacts/ConsultationPolicyView.tsx` and `ConsultationPolicyForm.tsx` (new); `ArtifactPage`, `ArtifactCreatePage`,
  `ArtifactsPage`, `AgentVersionPage`, and `AgentVersionCreatePage` handle the new kind and the optional reference for
  Coach Agent versions; `SafetyPolicyView` words `coach-to-specialist`; `outcomeText.ts` translates the three new
  violation codes; `agentCatalogAdapter.ts` maps the kind. New text in the `triage` and `agentCatalog` namespaces.
  Tests: adapter and view unit tests; simulated e2e `triage-consultations.spec.ts` and
  `agent-catalog-consultations.spec.ts` with routes in `simulatedApi.ts`; the platform agent catalog test asserts the
  seeded consultation policies referenced by the enabled Coach Agent versions.
- **Rationale**: FR-031 to FR-035; the Agent Catalog area must list every artifact kind
  ([Web UI Agent Catalog Administration](../20261018-103316-web-ui-agent-catalog/spec.md), FR-006 there).
- **Alternatives considered**: Showing consultations only in the lineage (the architecture requires visible
  attribution and influence next to the synthesis).

## R14 - Audit and telemetry

- **Decision**: Audit events `agent.consultation.requested` (outcome `created`, `replayed`, or `refused` with the
  reason code), `agent.consultation.resolved` (`answered`, `unavailable` with the child's failure reason, `ended`), and
  the existing invocation events for children (route `coach-to-specialist`) and for parent failures with
  `consultation-unavailable` or `parent-ended`; details hold identifiers, agent identifiers, reason codes, and counts
  only. `AgentOrchestrationMetrics` adds the counter `agent_orchestration.consultations` (tags `outcome`, `agent_id`)
  and the observable gauge `agent_orchestration.waiting_parents`; the existing counters carry `route`
  `coach-to-specialist` for children.
- **Rationale**: FR-036; the audit detail allow-list keeps questions, decision support, responses, notes, and advice
  out.
- **Alternatives considered**: A consultation-specific meter (one meter per functional area is the existing pattern).

## R15 - Cross-process evidence and environment

- **Decision**: `AgentRuntimeStackTests` (Hermes Agent Runtime) gains a consultation case: with the seed's consulting
  Coach Agent versions enabled, the Hermes script for `coach-jurgen-klopp` calls `get_match`, requests a consultation
  of `specialist-tactics`, and after the result declares its influence and answers; the script for
  `specialist-tactics` with the consultation section calls `find_pressing_phases` and answers. The test asserts, within
  120 seconds, the succeeded parent, the answered consultation with the child's tool call, the influence declaration,
  and the child absent from the turns. No new framework, SDK, or tool is needed: Python projects under `src/agents/`
  are verified by [Agents Python Verification Environment](../20261018-110526-agents-python-environment/spec.md), and
  the platform, Web UI, Docker, and image tests use the existing verification.
- **Rationale**: FR-039 and SC-001; constitution Technology and Tooling.
- **Alternatives considered**: A separate stack test class (duplicates the stack fixture).
