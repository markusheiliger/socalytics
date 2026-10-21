# Feature Specification: Agent Evidence Tools API

**Feature Branch**: `20261018-104924-agent-evidence-tools`

**Created**: 2026-10-18

**Status**: Draft

**Input**: User description: "Agent Evidence Tools API: the architecture says MCP tools map one-to-one from an explicit allowlist of agent-safe, read-only control-plane API operations with stable OpenAPI operation IDs; administration, mutation, lifecycle, worker, registration, claim, lease, checkpoint, and completion operations are never tools; every call retains the initiating actor, team, match, conversation, and invocation context and is reauthorized by the underlying API; agent-specific tool policies select subsets; football queries are backed only by accepted, API-indexed Analyst output and report unavailable or incomplete analysis instead of inferring it; and when authorization re-evaluation detects scope loss during an active attempt, Agent Orchestration invalidates the attempt and denies every subsequent evidence call (docs/architecture/intelligence-and-agents.md#mcp-tools, #authorization-lineage-and-lifecycle; docs/architecture/platform-implementation.md lists \"MCP-facing application tools\" as a platform functional area; the catalog's MCP Tool Policy table). This feature implements that platform side: an explicit, versioned allowlist of tool operations under a dedicated `/api/v1` route group with stable operation IDs, callable only by the authenticated agent workload for its current, unfenced attempt (attempt id and fencing token bound to the call) and only for tools in the invocation's pinned tool policy; every call is evaluated with the invocation's stored context (initiating member, team, match, conversation, and the conversation's bound run) and the member's current access, fails closed on scope loss and then fences the attempt per #155, and is recorded in the invocation's lineage as a tool call (tool id, arguments digest, result digest, evidence references, outcome) without storing evidence content in transport. The first allowlist covers what accepted data exists: `get_match` (match, bound run, run status, which capabilities have accepted results), accepted fact queries scoped to the bound run (reusing #144's read semantics, coverage, and limits), and `get_video_clip` (reusing #162's evidence media resolution with short-lived links that are returned to the workload but never stored or logged); the concept tools named by the catalog (`find_counter_attacks()`, `get_formations()`, and so on) are registered in the allowlist and answer an explicit \"analysis unavailable\" outcome because no accepted producer exists yet, never deriving concepts from raw facts. The simulated agent workload from #155 calls the tools in tests. Out of scope: the MCP server process and protocol (spec 7), Hermes (spec 8), new Analyst capabilities or concept producers, player statistics without a producing capability, write tools, production limits. Depends on `specs/archive/20261017-204332-agent-orchestration-invocations` (#155), `specs/20261018-101517-agent-catalog-registry` (batch sibling), `specs/archive/20261017-175648-fact-indexing-queries` (#144), `specs/20261017-224326-evidence-clip-access` (#162). Implements the platform side of docs/architecture/intelligence-and-agents.md#mcp-tools and #authorization-lineage-and-lifecycle and the \"MCP-facing application tools\" functional area."

## Clarifications

### Session 2026-10-18

- Q: Which allowlisted tool serves accepted fact reads of the bound run? → A: `get_match`, with an optional fact selection; without a selection it answers the match overview, so the allowlist stays the eleven identifiers of the architecture and the catalog baseline.
- Q: Should the number of tool calls per attempt be limited? → A: Yes: a configured maximum of tool calls per attempt (development 50), of which at most a configured number may be clip resolutions (development 10); further calls are refused as "tool call limit reached" without fencing the attempt.
- Q: Which tool calls create security audit events? → A: Refused and denied calls (wrong caller, stale attempt, not permitted, scope lost, limit reached) and every clip resolution that issues a link, with workload, invocation, attempt, and tool; other answered calls are recorded only in the lineage.
- Q: Should tool activity appear in the platform's telemetry? → A: Yes: counters of tool calls by tool and outcome, without arguments, results, or evidence.
- Q: How are repeated tool calls identified? → A: Each call carries a workload-chosen call identifier unique within its attempt, recorded in the lineage; reusing one is refused as a conflict without reading anything.

## User Scenarios & Testing *(mandatory)*

The actors are the stamp-local agent workload that executes an invocation's attempt ([Intelligence and Agents → Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)) and the member who owns the triage conversation and reads the invocation's lineage. Conversations, invocations, attempts, fencing, scope-loss rules, and lineage come from [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md); the agent version and tool policy pinned into each invocation come from [Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md); accepted facts and their coverage from [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md); and evidence media resolution from [Evidence Clip Access](../20261017-224326-evidence-clip-access/spec.md). This feature gives agents their only path to match evidence: the read-only evidence tools of [MCP Tools](../../docs/architecture/intelligence-and-agents.md#mcp-tools), enforced as [Authorization, Lineage, And Lifecycle](../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle) requires. The MCP server process that later exposes these operations as MCP tools, and Hermes, are later features; until then the simulated agent workload calls the tools.

### User Story 1 - An agent reads the evidence of its conversation's match (Priority: P1)

While executing an attempt, the agent workload asks for the match of the conversation it works on: the match, the conversation's bound analysis run with its state and whether it is still the match's current run, and which capabilities of that run have accepted results. It then asks the same match tool for accepted facts of one capability of that run, page by page with explicit coverage, and resolves the evidence of a fact to short-lived media links. Every answer comes only from accepted, indexed Analyst output of the bound run; the workload never names a match, team, or run itself.

**Why this priority**: Agents can only give evidence-backed advice if they can read accepted evidence; without these tools the advice of every agent is ungrounded. It is the smallest slice that proves the evidence path end to end.

**Independent Test**: With an invocation whose conversation is bound to a completed run with indexed facts, let the simulated agent workload claim it and call the match tool without and with a fact selection of one capability, and the video clip tool for one returned fact, and confirm that each answer covers only the bound run, that the fact read returns the same facts and coverage as the member fact read, and that the clip answer carries the evidence media parts of that fact.

**Acceptance Scenarios**:

1. **Given** an active attempt of an invocation whose conversation is bound to a `Completed` or `PartiallyCompleted` run, **When** the workload calls the match tool with the attempt's current fencing token, **Then** it receives the match, the bound run with its state and whether it is still the match's current run, and every capability of the run with its accepted fact count and coverage summary.
2. **Given** the same attempt, **When** the workload calls the match tool with a fact selection of one capability, capability version, and schema version of the bound run, optionally limited to a match-time range, **Then** it receives the facts in stable time order, page by page, with coverage and provenance exactly as the member fact read defines them, and never facts of another run.
3. **Given** a fact returned by a fact selection of the match tool, or a match-time range of the bound run, **When** the workload calls the video clip tool, **Then** it receives the evidence media parts with short-lived links exactly as the member evidence media resolution defines them.
4. **Given** a capability the bound run does not contain, or coverage that is missing, failed, blocked, or skipped for parts of the match, **When** the workload reads facts, **Then** the answer states that the analysis is unavailable or incomplete instead of returning an empty result that looks like "nothing happened".

---

### User Story 2 - Tools are least-privilege, attempt-bound, and fail closed (Priority: P2)

Only the agent workload may call the tools, only for its current, unfenced attempt, and only the tools of the invocation's pinned tool policy. Every call is evaluated with the invocation's stored context and the initiating member's current access. When the member has lost the right the invocation runs under, the call is denied, the attempt is fenced, and every later tool call of that attempt is refused. Administration, mutation, lifecycle, worker, registration, claim, lease, checkpoint, and completion operations are never tools.

**Why this priority**: Evidence is sensitive team data; tools that a stale attempt, another caller, or an agent without permission could call would break the architecture's authorization model. It builds on Story 1's tools.

**Independent Test**: Call each tool with a member session, an Analyst Manager token, a stale fencing token, an expired lease, and for a tool outside the pinned tool policy, and confirm each is refused without any evidence; remove the initiating member's Coach role while an attempt is active, call a tool, and confirm the call is denied, the attempt is fenced, the invocation fails with reason "scope lost", and every further tool call of that attempt is refused.

**Acceptance Scenarios**:

1. **Given** a caller that is not an authenticated agent workload identity, **When** it calls a tool, **Then** the call is refused and no evidence is disclosed.
2. **Given** an attempt that is not current, a fencing token that is not current, an expired lease, or an ended invocation, **When** the workload calls a tool, **Then** the call is answered with the same explicit stale disposition as the other workload operations and nothing is read.
3. **Given** an invocation whose pinned tool policy does not include a tool, **When** the workload calls that tool, **Then** the call is refused as not permitted, no evidence is disclosed, and the refusal is recorded in the invocation's lineage.
4. **Given** an active attempt whose initiating member no longer holds the right to run the invocation, **When** the workload calls any tool, **Then** the call is denied, the attempt is fenced and the invocation fails with reason "scope lost" exactly as the orchestration's scope-loss rules define, and every later tool call of that attempt gets a stale disposition.
5. **Given** the published list of tool operations, **When** it is compared with the platform's operations, **Then** it contains only read-only operations of the dedicated tool route group, each with a stable operation identifier, and no administration, mutation, lifecycle, worker, registration, claim, lease, checkpoint, or completion operation.

---

### User Story 3 - Every tool call is attributable in the lineage, and missing analysis is reported (Priority: P3)

Each tool call becomes an entry of the invocation's lineage with the tool, a digest of its arguments and of its result, the evidence it referenced, and its outcome, so the owner can see which evidence an answer rests on without the lineage storing the evidence itself. The concept tools named by the catalog exist in the allowlist and answer that the analysis is unavailable, because no accepted producer of those concepts exists yet; they never derive concepts from raw facts.

**Why this priority**: Advice is only trustworthy when its evidence is attributable, and agents must learn explicitly what analysis is missing instead of guessing. The tools work without it, but they must not ship without it.

**Independent Test**: Complete an invocation whose attempt called the match tool, the match tool with a fact selection, the clip tool, and `get_formations`, read its lineage as the owner, and confirm four tool-call entries in call order with tool, digests, evidence references, and outcome, no fact payloads and no links; confirm that `get_formations` answered "analysis unavailable" naming its producing capability.

**Acceptance Scenarios**:

1. **Given** a tool call that was answered, refused, or denied, **When** the owner reads the invocation's lineage, **Then** it contains one tool-call entry in call order with the call identifier, the tool identifier, the attempt, a digest of the arguments, a digest of the result where one was returned, the evidence references, and the outcome.
2. **Given** a fact selection or clip resolution, **When** its lineage entry is read, **Then** its evidence references identify the facts (accepted result and position) or media (digest and match-time window) it returned, and the entry contains no fact payload, no link, and no media content.
3. **Given** any concept tool of the catalog's allowlist (counter-attacks, turnovers, heatmap, ball actions, set plays, formations, pressing phases, tactical phases) or the player statistics tool, **When** the workload calls it, **Then** the answer is an explicit "analysis unavailable" outcome that names the producing capability where the catalog defines one, and no fact is interpreted, aggregated, or derived.
4. **Given** the invocation's lineage, **When** it is replayed for reading, **Then** no tool is called again and no evidence access is gained.

---

### Edge Cases

- **Bound run superseded during an attempt**: The tools keep reading the conversation's bound run, which stays usable; the match tool reports that it is no longer the match's current run. Supersession is not scope loss.
- **Initiating member keeps only a Viewer role**: The member may still read the conversation, but the invocation's execution authority is lost, so the next tool call is denied and the attempt is fenced, as for any other scope loss.
- **Fact or range of another run**: A clip request for a fact that does not belong to the bound run is answered "not found" without disclosure; a range outside the match is answered with what the evidence media resolution reports for it.
- **Concurrent tool calls of one attempt**: Each call is evaluated on its own and gets its own lineage entry; their order follows the order in which the platform recorded them.
- **Tool call racing the attempt's end**: A call that arrives after the attempt was completed, failed, expired, or fenced gets a stale disposition; a call that started before is recorded with its outcome, and no tool call changes the attempt or the invocation except a scope-loss denial.
- **Evidence media not ready**: Parts that are materializing, rematerializing, failed, or not covered are reported as the evidence media resolution reports them; the tool never waits for materialization.
- **Storage unavailable during a clip resolution**: The call fails with the storage-unavailable outcome, issues no link, and is recorded in the lineage; it does not fence the attempt.
- **Invalid arguments**: Malformed or out-of-bounds arguments (page size, range, continuation, context) are refused as invalid with the same bounds as the member reads, recorded in the lineage once the call passed the attempt binding (a body without a call identifier or fencing token cannot be bound and is only refused), and do not fence the attempt.
- **Tool policy without a tool the agent asks for**: The refusal is the agent's problem to handle; it is not scope loss and does not fence the attempt.
- **Tool call limit reached**: Once an attempt has used its configured number of tool calls or clip resolutions, further calls are refused as "tool call limit reached"; the attempt stays active and can still complete or fail, and a retry attempt starts with a fresh limit.
- **Repeated call identifier**: A call that reuses a call identifier of its attempt is refused as a conflict without reading anything, so a network retry must use a new identifier; reads are safe to repeat.
- **Invocations without a pin**: Cannot be running, because the catalog registry refuses their claim; tool calls for them get a stale disposition.

## Requirements *(mandatory)*

### Functional Requirements

#### Tool allowlist

- **FR-001**: The platform MUST publish a versioned tool allowlist that maps each tool identifier of the catalog's allowlist ([MCP Tools](../../docs/architecture/intelligence-and-agents.md#mcp-tools), [MCP Tool Policy](../../docs/architecture/intelligence-agent-catalog.md#mcp-tool-policy)) one-to-one to one platform operation with a stable operation identifier, its argument schema, its result schema, and whether it answers from accepted evidence or always reports analysis as unavailable; the allowlist MUST be a machine-readable contract in the repository's contract catalog with golden fixtures, and its tool identifiers MUST equal the tool allowlist of the agent catalog baseline.
- **FR-002**: Every allowlisted operation MUST be read-only and MUST live in one dedicated `/api/v1` route group that contains no other operation; no administration, mutation, lifecycle, worker, registration, claim, lease, checkpoint, or completion operation MAY be part of the allowlist or of that route group.
- **FR-003**: The first allowlist MUST consist of exactly the eleven tool identifiers of the catalog baseline and MUST answer from accepted evidence for the match tool (`get_match`, which also serves accepted fact reads of the bound run) and the video clip tool (`get_video_clip`); the concept tools `find_counter_attacks`, `find_turnovers`, `get_heatmap`, `find_ball_actions`, `find_set_plays`, `get_formations`, `find_pressing_phases`, and `get_tactical_phases`, and the player statistics tool `get_player_stats`, MUST be allowlisted operations that always answer "analysis unavailable".

#### Caller, attempt, and policy binding

- **FR-004**: Tool operations MUST accept only an authenticated agent workload identity as defined by [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md) (FR-014 there); member sessions, Analyst Manager credentials, and anonymous callers MUST be refused without disclosure.
- **FR-005**: Every tool call MUST name the attempt and carry its current fencing token; only the current attempt of a running invocation with an unexpired lease MAY call a tool, and every other call MUST receive the orchestration's explicit stale disposition (attempt not current, lease expired, attempt fenced, invocation ended, scope lost) and read nothing.
- **FR-006**: A tool call MUST be refused as not permitted unless the tool is in the tool policy pinned into the invocation at submission ([Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md), FR-019 there); a not-permitted call MUST disclose no evidence and MUST NOT fence the attempt.
- **FR-007**: Tool arguments MUST NOT name a team, match, conversation, or run; every call MUST be scoped to the invocation's stored team, match, and the conversation's bound run, and a fact or result reference outside that run MUST be answered "not found" without disclosure.
- **FR-007a**: Every tool call MUST carry a workload-chosen call identifier that is unique within its attempt; reusing a call identifier within the attempt MUST be refused as a conflict without reading anything.
- **FR-007b**: The platform MUST limit each attempt to a configured maximum number of tool calls (development 50), of which at most a configured number may be clip resolutions (development 10); further calls MUST be refused as "tool call limit reached" without reading anything and without fencing the attempt. These per-attempt limits replace the member evidence-media resolution rate for tool calls. Production values remain open.

#### Authorization and scope loss

- **FR-008**: Every tool call MUST re-evaluate the initiating member's execution authority for the invocation (active membership and Club Admin or a Coach role on the owning team, as the orchestration's FR-026 defines) before reading any evidence.
- **FR-009**: When a tool call detects scope loss, the platform MUST deny the call without evidence, fence the attempt, and end the invocation as `failed` with reason "scope lost" exactly as the orchestration's FR-027 defines, in the same atomic step; every later tool call of that attempt MUST receive the stale disposition "scope lost" or "attempt fenced".

#### Evidence tools

- **FR-010**: The match tool MUST return the match, the conversation's bound run with its state and whether it is still the match's current run, and, for every capability node of the run, its capability identifier and version, schema version, output type, accepted fact count, and coverage summary, with the semantics of the member fact-stream listing of [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md) (FR-018 there).
- **FR-011**: Accepted fact reads of the bound run MUST be served by the match tool when its call carries a fact selection (capability identifier, capability version, schema version, and optionally node key, match-time range, page size, and continuation), with the selection, ordering, pagination, page-size bounds, range overlap, coverage, provenance, and run isolation of the member fact read (FR-011 to FR-017 and FR-019 to FR-021 there); a capability the bound run does not contain MUST be answered as "analysis unavailable" naming it rather than as an error.
- **FR-012**: The video clip tool MUST resolve either a fact of the bound run (accepted result and position, with optional context) or a match-time range of the bound run with the semantics, bounds, part states, integrity checks, and link lifetime of the member evidence media resolution of [Evidence Clip Access](../20261017-224326-evidence-clip-access/spec.md) (FR-001 to FR-012 there).
- **FR-013**: Links issued to the workload MUST be returned only in the tool call's response; they MUST NOT be stored, recorded in the lineage, or written to logs, traces, metrics, audit events, notifications, or error reports.
- **FR-014**: The concept tools and the player statistics tool MUST answer with an explicit "analysis unavailable" outcome that names the producing capability defined by the [Analyst Capability Catalog](../../docs/architecture/analyst-capability-catalog.md) where one exists and states that no accepted producer is supported yet; they MUST NOT read, interpret, aggregate, or derive anything from indexed facts.
- **FR-015**: Every answer from accepted evidence MUST state incomplete analysis explicitly through coverage; an empty fact list MUST never be presented without the coverage that explains it.

#### Lineage

- **FR-016**: Every tool call that reaches the attempt binding check successfully MUST append one tool-call entry to the invocation's append-only lineage, in the order the platform records the calls, with the attempt, the call identifier, the tool identifier, a digest of the canonical arguments, a digest of the returned result where one was returned, the evidence references, and the outcome (answered, unavailable, not found, not permitted, invalid, limit reached, scope lost, or failed with a reason); a refused reuse of a call identifier MUST NOT add an entry.
- **FR-017**: Evidence references MUST identify returned facts by accepted result and position and returned media by digest and match-time window; lineage entries MUST NOT contain fact payloads, links, media content, or other evidence content.
- **FR-018**: The owner's lineage read of [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md) (FR-010 there) MUST include the tool-call entries; reading or replaying a lineage MUST NOT call a tool or grant evidence access.

#### Audit and telemetry

- **FR-018a**: The platform MUST record a security audit event for every refused or denied tool call (caller not a workload identity, stale attempt, not permitted, call identifier reused, limit reached, scope lost) and for every clip resolution that issues at least one link, with the workload identity, the initiating member, the invocation, and the attempt where they are known, the tool, and the outcome, and for link grants the media digests and the latest expiry; other answered calls are recorded only in the lineage. Audit events MUST NOT contain links, fact payloads, or question or advice text.
- **FR-018b**: The platform MUST emit telemetry counters of tool calls by tool and outcome through its existing telemetry, without arguments, results, or evidence.

#### Simulated workload, interfaces, and tests

- **FR-019**: The simulated agent workload MUST call the match tool and at least one concept tool during every attempt it executes, and, under test control, a fact selection and a clip resolution, and MUST declare the evidence it used in its simulated advice; its execution and advice stay deterministic and marked as simulated.
- **FR-020**: All tool operations MUST appear in the platform's OpenAPI document with their stable operation identifiers and typed argument and result schemas, and the committed OpenAPI snapshot MUST be refreshed.
- **FR-021**: Automated tests MUST cover each tool's answers against the member reads it reuses, run and scope isolation, caller and attempt binding with every stale disposition, the pinned tool policy, call identifiers and tool call limits, scope loss during an attempt and the denial of later calls, "analysis unavailable" answers, the lineage entries and their content limits, audit events and telemetry and their content limits, the absence of links from storage and logs, the allowlist contract and its equality with the catalog baseline, and the simulated agent workload end to end.

### Key Entities

- **Tool allowlist**: The versioned, machine-readable list of tool identifiers, each with its operation identifier, argument and result schemas, and whether it answers from accepted evidence or reports analysis as unavailable; it equals the catalog baseline's tool allowlist.
- **Tool call**: One call of an allowlisted tool by the agent workload for one attempt, with its call identifier (unique within the attempt), arguments, outcome, and returned result; the result is returned to the caller only.
- **Tool-call lineage entry**: An append-only lineage entry of an invocation recording one tool call's tool, attempt, argument and result digests, evidence references, and outcome, without evidence content.
- **Evidence reference**: The identity of returned evidence: a fact (accepted result and position) or a media piece (digest and match-time window).
- **Analysis-unavailable outcome**: The explicit answer of a tool whose analysis has no accepted producer or is not part of the bound run, naming the producing capability where one exists.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated tests, 100% of fact and clip answers of the tools equal the answers of the corresponding member reads for the same run and selection, and 0 answers contain evidence of another run, match, or team.
- **SC-002**: In automated tests, 100% of tool calls by callers other than the agent workload, with a stale attempt or token, or for tools outside the pinned tool policy are refused and disclose no evidence.
- **SC-003**: In automated tests, every attempt whose initiating member lost execution authority is fenced at its next tool call, and 0 tool calls of a fenced attempt read evidence.
- **SC-004**: In automated tests, 100% of answered, unavailable, refused, and denied tool calls that passed the attempt binding have exactly one lineage entry, every refused or denied call and every link grant has an audit event, and 0 lineage entries, logs, audit events, or telemetry records contain a link or a fact payload.
- **SC-005**: 100% of concept and player statistics tool calls answer "analysis unavailable", and the published allowlist contains 0 operations outside the dedicated read-only route group.
- **SC-006**: With the simulated agent workload running locally, a member's question is answered within 30 seconds of submission and its lineage shows the tool calls the workload made.

## Assumptions

- **Tool caller**: The agent workload identity of [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md) calls the tools; the later MCP server feature exposes these operations as MCP tools one-to-one and relays the attempt binding, and Hermes calls them through MCP. Neither exists in this feature.
- **Evidence semantics are reused**: Fact selection, coverage, provenance, limits, and evidence media resolution are those of the fact-indexing and evidence-clip features; this feature adds no new evidence semantics and no new Analyst output.
- **No accepted concept producers yet**: No soccer-concept or player-statistics capability is accepted on any stamp, so their tools report analysis as unavailable; turning a tool into one that answers from accepted evidence is a later feature together with its producing capability.
- **Configured values**: Limits are those of the reused reads, the orchestration's execution policy, and the per-attempt tool call limits, with development and test values; production values remain open.
- **Out of scope**: The MCP server process and protocol, Hermes, real models, new Analyst capabilities or concept producers, player statistics without a producing capability, write tools, Coach Agent invocations and delegated consultations (their tool calls come with those features), the Web UI, governance review reads, and production limits.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
- **Dependencies**: [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md) (workload identity, attempts, fencing, stale dispositions, scope-loss rules, lineage, simulated agent workload), [Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md) (tool allowlist in the catalog baseline, pinned tool policy, execution envelope pins), [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md) (fact streams, fact reads, coverage), [Evidence Clip Access](../20261017-224326-evidence-clip-access/spec.md) (evidence media resolution and links), and [Analysis Run Supersession](../archive/20261017-183953-analysis-run-supersession/spec.md) (whether a run is still the match's current run).
- **Architecture References**: [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md) (MCP Tools; Authorization, Lineage, And Lifecycle), [Intelligence Agent Catalog → MCP Tool Policy](../../docs/architecture/intelligence-agent-catalog.md#mcp-tool-policy), [Analyst Capability Catalog](../../docs/architecture/analyst-capability-catalog.md), and [Platform Implementation Profile → Control Plane](../../docs/architecture/platform-implementation.md#control-plane) ("MCP-facing application tools").
