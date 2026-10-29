# Research: Agent Evidence Tools API

## R1 - Placement and layering

- **Decision**: Add a `Tools` folder to the existing Agent Orchestration functional area in each platform layer
  (`Domain/AgentOrchestration/Tools`, `Application/AgentOrchestration/Tools`, `Infrastructure/Persistence/AgentOrchestration/Tools`,
  `Api/AgentOrchestration/AgentToolEndpoints.cs`). Tool handlers compose the Analysis read cores of the fact-indexing and
  evidence-clip features through Application interfaces; they never call member endpoints over HTTP and never touch
  tables of the Analysis area directly.
- **Rationale**: The platform is one well-structured monolith whose functional areas are folders ([Control Plane](../../../docs/architecture/platform-implementation.md#control-plane));
  "MCP-facing application tools" are a functional area whose only caller today is the agent workload of Agent
  Orchestration, and attempt binding, scope evaluation, fencing, and lineage are Agent Orchestration state. Placing them
  next to the workload operations reuses the workload authentication, `IInvocationScopeEvaluator`, and the lineage store
  without a cross-area dependency in the other direction.
- **Alternatives considered**: A separate top-level `McpTools` folder (rejected: it would need Agent Orchestration
  internals for binding and lineage anyway); calling the member endpoints with a synthetic member principal (rejected:
  it would impersonate the member, bypass attempt binding, and apply the per-member rate limiter).

## R2 - Route group, operation identifiers, and request shape

- **Decision**: One route group `/api/v1/agent-tools` mapped with a new `MapAgentToolApi()` that requires the existing
  `AgentWorkload` policy (scheme `AgentWorkloadDPoP` of the orchestration feature) and HTTPS, and nothing else. Each
  allowlisted tool is exactly one operation `POST /api/v1/agent-tools/attempts/{attemptId}/{tool-path}` with the
  operation identifier `agentTool` + PascalCase tool name (`agentToolGetMatch`, `agentToolGetVideoClip`,
  `agentToolFindCounterAttacks`, …) and the tag `Agent tools`. The body is `{ callId, fencingToken, arguments }`; the
  response is `{ callId, toolId, outcome, result }`. `POST` is used because the body carries the fencing token; every
  operation is read-only with respect to evidence and changes only tool-call records and, on scope loss, the attempt.
- **Rationale**: Stable, prefixed operation identifiers avoid collisions with member operations (`getMatch` already
  exists) and give the later MCP server a one-to-one mapping. A dedicated group makes FR-002 checkable by an
  architecture test that enumerates the group's endpoints against the allowlist.
- **Alternatives considered**: `GET` with the fencing token in a header (rejected: fact selections and clip requests
  are structured bodies, and the evidence media operation is already `POST`); one generic `invokeTool` operation
  (rejected: the architecture requires one-to-one mapping to operations with stable identifiers).

## R3 - Tool allowlist contract

- **Decision**: `contracts/agent-tools/tool-allowlist/v1` (JSON Schema 2020-12, `releases/1.0.0`, valid fixture
  `examples/valid/allowlist-1.0.0.json`, invalid fixtures). Each entry: `toolId` (the catalog identifier without `()`),
  `operationId`, `method`, `pathTemplate`, `answers` (`accepted-evidence` or `analysis-unavailable`),
  `producingCapability` (capability identifier or `null`), `argumentsSchema` and `resultSchema` (OpenAPI component
  names). The Domain type `AgentToolAllowlist` holds the same list; tests assert that it equals the fixture, that its
  tool identifiers equal the catalog baseline's tool allowlist (`contracts/agent-catalog/catalog-baseline/v1`), and that
  the OpenAPI document's `/api/v1/agent-tools` operations equal its entries.
- **Rationale**: FR-001 requires a machine-readable, versioned allowlist; the catalog baseline already fixes the tool
  identifiers, so this contract adds only the operation mapping and answer kind; three equality tests keep the
  architecture, catalog, allowlist, and API from drifting.
- **Alternatives considered**: Deriving the allowlist from OpenAPI extensions only (rejected: not a validated contract
  with fixtures); adding the mapping to the catalog baseline (rejected: the baseline belongs to the catalog feature and
  changes only with the architecture's agent roster).

## R4 - Attempt binding, scope evaluation, and fencing

- **Decision**: Every tool call runs `AuthorizeToolCall` in a short unit of work that locks the invocation row as the
  orchestration's workload commands do: the attempt must be the invocation's current, `active` attempt with the
  presented fencing token, an unexpired lease, and an unreached deadline, otherwise `409 agent-attempt-stale` with the
  orchestration's dispositions; then `IInvocationScopeEvaluator` re-evaluates the initiating member; scope loss runs the
  orchestration's scope-loss transition (attempt `fenced`, invocation `failed` with `scope-lost`, lineage
  `scope-lost`, audit) in the same unit of work, and the call is answered `409 agent-attempt-stale` with disposition
  `scope-lost`; then the tool must be in the invocation's pinned tool policy (`403 agent-tool-not-permitted`); then the
  call identifier must be new for the attempt and the per-attempt limits must not be exhausted. Passing calls write an
  immutable `agent_tool_calls` reservation row in the same unit of work.
- **Rationale**: FR-005, FR-006, FR-007a, FR-007b, FR-008, FR-009; reusing the orchestration's locking and scope
  evaluator gives tool calls exactly the semantics of the other workload operations, as the architecture requires
  ("denies every subsequent MCP call").
- **Alternatives considered**: Re-evaluating scope after the read (rejected: evidence would be read with stale
  authority); fencing on a not-permitted tool (rejected: an agent error is not scope loss).

## R5 - Executing reads outside the lock and appending lineage

- **Decision**: After authorization commits, the handler executes the read without holding the invocation lock, then
  appends one `tool-call` lineage entry in a second short unit of work that takes the invocation lock only to assign
  the next lineage sequence. Refusals detected in the authorization step after the binding check (not permitted, call
  limit reached, scope lost) append their entry in the authorization unit of work; invalid arguments are detected by
  the read cores after the reservation and are appended like answered calls with outcome `invalid`. A reservation
  without a lineage entry (process stopped between the two units of work) is closed by the orchestration's attempt
  monitor with outcome `failed` and reason `interrupted` once its attempt has ended or the reservation is older than
  the attempt's maximum duration. The append is idempotent per attempt and call identifier (a partial unique index
  over `tool-call` entries): whichever entry is written first stands, and a later append for the same call is
  skipped, so a slow read never produces a second entry.
- **Rationale**: Fact pages and evidence media integrity checks can take longer than lease-critical commands; holding
  the invocation row lock during them would block renewals and completions. The reservation row keeps call-identifier
  uniqueness and limits exact; the monitor guarantees "exactly one lineage entry" (SC-004).
- **Alternatives considered**: One unit of work around the read (rejected: blocks renewals); recording lineage before
  the read (rejected: the outcome and result digest are unknown).

## R6 - Reusing the fact and evidence read cores

- **Decision**: The fact-indexing feature's `ListRunFactStreams` and `ListRunFacts` and the evidence-clip feature's
  `ResolveEvidenceMedia` each split into a member handler (authorization through `IAccessAuthorizer`, per-member rate
  limit, member audit) and an internal core that takes an already-authorized run scope and an audit actor:
  `IRunFactStreamReader`, `IRunFactReader`, and `IEvidenceMediaResolver`. Member handlers call the cores unchanged in
  behavior; tool handlers call them with the invocation's bound run. Selection validation, bounds, ordering,
  continuation tokens, coverage, provenance, part states, integrity checks, and link lifetime therefore come from one
  implementation.
- **Rationale**: FR-010 to FR-012 and SC-001 require equal answers; one implementation is the only way to guarantee it.
- **Alternatives considered**: Duplicating the queries for tools (rejected: drift); calling member handlers with a
  synthesized principal (rejected in R1).

## R7 - get_match with a fact selection

- **Decision**: `agentToolGetMatch` arguments are `{ facts?: { capabilityId, capabilityVersion, schemaVersion, nodeKey?,
  fromMs?, toMs?, pageSize?, continuationToken? } }`. Without `facts` the result is `{ match, run, streams }`: the match
  summary (identifier, team, opponent, kickoff, home or away, competition), the bound run (identifier, state,
  `isCurrent`, `supersededBy`), and the fact-stream listing entries. With `facts` the result adds `facts` with the
  member fact read's `stream`, `range`, `items`, `coverage`, and `continuationToken`. A capability, version, schema
  version, or node key the run does not contain answers outcome `unavailable` with reason `capability-not-in-run`;
  an ambiguous capability or invalid selection answers `400` with the fact read's validation codes.
- **Rationale**: The clarification keeps the allowlist at the architecture's eleven identifiers ("`get_match()` …
  query accepted match … facts"); one tool still maps to one operation.
- **Alternatives considered**: A new `get_match_facts` tool (rejected in clarification: it would change the
  architecture's tool list and the catalog baseline).

## R8 - Concept and player statistics tools

- **Decision**: The nine tools `get_player_stats`, `find_counter_attacks`, `find_turnovers`, `get_heatmap`,
  `find_ball_actions`, `find_set_plays`, `get_formations`, `find_pressing_phases`, and `get_tactical_phases` accept an
  optional bounded `{ fromMs?, toMs? }` range (validated like the fact read) and always answer outcome `unavailable`
  with reason `no-accepted-producer` and `producingCapability` from the allowlist: `counter-attack-analysis`,
  `possession-and-transitions`, `spatial-aggregation`, `ball-action-analysis`, `set-play-analysis`,
  `formation-analysis`, `pressing-analysis`, `tactical-phase-aggregation`, and `null` for `get_player_stats` (no
  catalog capability produces player statistics). They read no facts.
- **Rationale**: FR-014; the [Analyst Capability Catalog](../../../docs/architecture/analyst-capability-catalog.md) names
  the producers; reading nothing makes "never derive" structurally true.
- **Alternatives considered**: Answering `501` (rejected: an unavailable analysis is a valid answer the agent must
  reason about, not an error).

## R9 - Lineage payload, digests, and evidence references

- **Decision**: The `tool-call` lineage payload is `{ callId, toolId, outcome, reason?, argumentsDigest, resultDigest?,
  evidence: { facts: [{ acceptedResultId, ordinal }], media: [{ mediaDigest, startMs, endMs }] } }`. Digests are
  `sha-256:` of the canonical JSON (`CanonicalJson`/`ContentDigest`) of the arguments and of the result with every
  `media.url` and `media.expiresAt` removed, so digests never depend on or reveal links. Fact references are bounded by
  the page size (at most 500), media references by the evidence parts of one resolution.
- **Rationale**: FR-016, FR-017; the owner's lineage read already returns payloads (orchestration contract), so tool
  calls appear there without a new read.
- **Alternatives considered**: Storing results (rejected: evidence content in lineage); separate tool-call read
  (rejected: unnecessary).

## R10 - Limits

- **Decision**: `AgentOrchestration:Tools` options `MaxCallsPerAttempt` (development and test 50) and
  `MaxClipResolutionsPerAttempt` (development and test 10), validated at start; counted from the attempt's
  reservations. Exceeding either answers `429 agent-tool-call-limit-reached` with the limit name. The evidence
  feature's per-member rate limiter does not apply to the tool route group.
- **Rationale**: Clarification; per-attempt limits bound a runaway agent without coupling it to the member's own
  interactive budget.
- **Alternatives considered**: Recording the limits in the execution policy (rejected: would version the orchestration
  policy for a value that does not govern retries).

## R11 - Audit and telemetry

- **Decision**: `AgentToolAuditEvents` records `agent.tool.call.refused` (reasons `caller-not-workload`, recorded by an
  authorization result handler (`IAuthorizationMiddlewareResultHandler`) for the route group when authentication or the
  `AgentWorkload` policy fails, and `attempt-stale`,
  `not-permitted`, `call-id-reused`, and `limit-reached`), `agent.tool.call.scope-lost`, and `agent.tool.evidence-media.granted` (media
  digests, latest expiry, initiating member, invocation, attempt, tool; never the link). Details pass the existing
  audit-trail allow-list. A new meter `SocAlytics.Platform.AgentTools` counts `agent_tool_calls_total` with tags `tool`
  and `outcome`.
- **Rationale**: Clarification; mirrors the evidence feature's grant event and the orchestration's telemetry.

## R12 - Simulated agent workload

- **Decision**: The simulated agent workload (orchestration feature) calls `get_match` and `get_formations` in every
  attempt; question markers interpreted only by the simulator add `[simulate:tools-facts]` (a fact selection of the
  first stream with accepted facts), `[simulate:tools-clip]` (a clip of the first returned fact), and
  `[simulate:tools-denied]` (a tool outside the pinned policy). Its deterministic advice lists the tool outcomes and the
  evidence references it used. Integration tests host its runner in process, as the orchestration feature does.
- **Rationale**: FR-019; proves the full path with the existing simulator conventions.

## R13 - Environment

- **Decision**: No new framework, SDK, or tool. Build and tests need .NET 10 and Docker (PostgreSQL, NATS, and the
  S3-compatible storage the evidence feature's tests already use), which `.github/actions/environment-setup` and
  `environment-verify` provide.
