# Feature Specification: MCP Server

**Feature Branch**: `20261018-111804-mcp-server`

**Created**: 2026-10-18

**Status**: Draft

**Input**: User description: "MCP Server: the architecture places an MCP Server in the intelligence plane between the agents and the API; MCP is the team-authorized evidence boundary, its tools map one-to-one from the explicit allowlist of read-only API operations with stable operation IDs, every call retains the initiating actor, team, match, conversation, and invocation context and is reauthorized by the underlying API, MCP never accesses databases or raw metadata, reports unavailable or incomplete analysis instead of inferring it, and is not the agent-to-agent message bus (docs/architecture/intelligence-and-agents.md#mcp-layer, #mcp-tools, #agent-orchestration-authority, #authorization-lineage-and-lifecycle; docs/architecture/overview.md places it in the Intelligence Plane). Spec 6 provides the platform side. This feature creates the MCP server as an independent locked Python project under `src/agents/` (the plan names it), built on the official MCP Python SDK: it exposes each allowlisted tool of spec 6's allowlist contract as an MCP tool with the same identifier, argument schema, and result shape, generated from or verified against that contract so the two cannot drift; it serves only the agent runtime inside the stamp (the plan chooses the MCP transport suitable for a separate container and how a session is bound to exactly one current attempt: attempt id, fencing token, and the workload's credentials or a delegated proof, never member credentials, and never accepting arguments that widen the attempt's scope); every tool call is forwarded to spec 6's API operation and the API's answer, refusal, scope loss, or 'analysis unavailable' outcome is returned faithfully, with no caching of evidence beyond the call and no logging of evidence content or presigned links; it refuses tools outside the session's pinned tool policy before forwarding (defense in depth; the API stays authoritative); it exposes health and readiness for orchestration, configuration through environment variables and files, and an OCI image definition consistent with spec 1's rules. Tests run without network beyond locked dependencies: unit tests against a fake API, contract-conformance tests against the allowlist contract and its examples, and a cross-process test against the platform API with the simulated agent workload's context if the plan finds it feasible in the existing test infrastructure. Out of scope: Hermes and model calls (spec 8), new tools or API operations, write tools, MCP resources or prompts beyond tools, public exposure outside the stamp, Compose integration beyond what spec 2 can reference later, production values. Depends on `specs/20261018-104924-agent-evidence-tools` (batch sibling), `specs/archive/20261018-110526-agents-python-environment` (environment, must merge first), `specs/archive/20261017-204332-agent-orchestration-invocations` (#155), `specs/archive/20261018-092503-oci-images` (image rules). Implements docs/architecture/intelligence-and-agents.md#mcp-layer and #mcp-tools (server side) and the src/agents ownership in src/agents/README.md."

## Clarifications

### Session 2026-10-18

- Q: Where does the server learn the tool policy of a session's invocation? → A: From the platform: when a session is opened, the server reads the attempt's pinned artifacts through the attempt-scoped workload read, which also confirms the binding; it never takes the tool policy from the agent runtime.
- Q: How does the agent runtime authenticate to the server? → A: With a configured shared secret read from a mounted file and presented on every request, compared in constant time; production values and rotation remain open.
- Q: Whose platform credentials does the server use? → A: Its own configured agent workload client with its own private key (not shared with the agent runtime), from which it obtains short-lived, proof-bound workload tokens.
- Q: Who chooses the call identifier of a forwarded tool call? → A: The server generates a fresh identifier for every forwarded call and returns it with the result, so the runtime can relate the call to the invocation's lineage.
- Q: What observability does the server provide? → A: Structured logs on standard output with attempt identifier, call identifier, tool, outcome, platform status, and duration per call, and session open and close events; metrics and trace export are left to the stamp composition.

## User Scenarios & Testing *(mandatory)*

The actors are the stamp-local agent runtime that executes an invocation's attempt (Hermes, a later feature; until then a test client in its place) and the stamp operator who runs the server next to the platform API. The server is the [MCP Layer](../../docs/architecture/intelligence-and-agents.md#mcp-layer) of the architecture: agents reach evidence only through it, and it reaches evidence only through the read-only agent tool operations of [Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/spec.md), which stay the authority for every answer. Attempts, fencing tokens, stale dispositions, scope loss, and the workload identity come from [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md); the tool policy pinned into each invocation comes from [Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md). The server owns no evidence, no authorization decision, and no lineage.

### User Story 1 - The agent runtime reads match evidence through MCP tools (Priority: P1)

While executing an attempt, the agent runtime opens one MCP session for that attempt, lists the tools it may use, and calls them by the same identifiers, with the same arguments, as the platform's agent tool operations. Every call is forwarded to the platform for that attempt, and the runtime receives exactly what the platform answered: the match and its bound run, pages of accepted facts with coverage, evidence media with short-lived links, or an explicit "analysis unavailable" answer.

**Why this priority**: Agent frameworks speak MCP; without the server, no agent runtime can reach the platform's evidence tools, and the architecture's evidence boundary does not exist. It is the smallest slice that gives an agent runtime evidence-backed answers.

**Independent Test**: Against a fake platform that answers like the agent tool operations, open a session for an attempt, list the tools, call `get_match` without and with a fact selection, `get_video_clip`, and `get_formations`, and confirm that each call reached the matching platform operation with the session's attempt and fencing token and that each MCP result equals the platform's answer.

**Acceptance Scenarios**:

1. **Given** a session bound to a current attempt, **When** the runtime lists the tools, **Then** it sees only tools of the platform's tool allowlist that the invocation's pinned tool policy permits, each with the allowlist's identifier and the argument and result schemas of its platform operation.
2. **Given** the same session, **When** the runtime calls an allowlisted tool with valid arguments, **Then** the server forwards exactly one request to that tool's platform operation for the session's attempt and returns the platform's outcome and result unchanged.
3. **Given** a tool whose platform operation answers "analysis unavailable", **When** the runtime calls it, **Then** the MCP result states that the analysis is unavailable with the platform's reason and producing capability, and the server adds, removes, or derives nothing.
4. **Given** a clip resolution that returns short-lived links, **When** the runtime receives the result, **Then** the links are present only in that result and nowhere in the server's logs, metrics, or stored state.

---

### User Story 2 - Sessions are attempt-bound, least-privilege, and fail closed (Priority: P2)

Only the agent runtime inside the stamp can open a session, and each session belongs to exactly one attempt for its whole life. Nothing a runtime sends in a tool call can change the attempt, the team, the match, the conversation, or the run a call reads. Tools outside the invocation's pinned tool policy are refused before anything is forwarded. When the platform refuses a call, reports a stale attempt, or detects scope loss, the runtime receives that answer as it was given, and the server never turns a refusal into an answer.

**Why this priority**: The MCP server holds platform credentials; if it widened scope, accepted calls for other attempts, or hid refusals, it would become a confused deputy that defeats the platform's authorization model. It builds on Story 1's forwarding.

**Independent Test**: Open sessions without runtime authentication, with a stale or unknown attempt, and with a fencing token the platform rejects, and confirm each is refused; in a valid session call a tool outside the pinned policy and confirm it is refused without any request to the platform; send arguments that name a team, match, conversation, run, or attempt and confirm they are refused; let the fake platform answer each stale disposition, scope loss, not permitted, not found, invalid, limit reached, and storage unavailable, and confirm each reaches the runtime unchanged as a tool error.

**Acceptance Scenarios**:

1. **Given** a client that does not present the agent runtime's shared secret, **When** it connects, **Then** it is refused and no session is created.
2. **Given** a session request whose attempt the platform does not confirm as current with the presented fencing token, **When** the session is opened, **Then** it is refused with the platform's stale disposition or not-found answer and no session exists.
3. **Given** a session, **When** the runtime calls a tool that is not in the invocation's pinned tool policy, **Then** the call is refused as not permitted and no request reaches the platform.
4. **Given** a session, **When** a tool call carries an argument outside the tool's argument schema, including any team, match, conversation, run, or attempt reference, **Then** the call is refused as invalid and no request reaches the platform.
5. **Given** a forwarded call that the platform refuses or denies (stale attempt, scope lost, not permitted, not found, invalid, call identifier reused, limit reached, or storage unavailable), **When** the runtime receives the result, **Then** it is a tool error carrying the platform's status, problem code, and disposition unchanged.
6. **Given** the server's requests to the platform, **When** they are inspected, **Then** every request is authenticated with the server's own workload credentials and never with member credentials, and only the platform operations the server needs are ever called.

---

### User Story 3 - Operators can run, probe, and package the server (Priority: P3)

A stamp operator configures the server through environment variables and mounted files, starts it as a container next to the platform API, and lets the orchestrator probe its liveness and readiness. Misconfiguration stops the server at start with a message naming the setting. The image follows the repository's container image rules.

**Why this priority**: The server is a separate deployable of the intelligence plane; without health, configuration, and an image, no stamp can run it. It is needed before the agent runtime and the stamp composition can use the server, but Stories 1 and 2 prove its behavior first.

**Independent Test**: Start the server with a missing setting and confirm it stops naming the setting; start it with a fake platform and confirm liveness answers at once and readiness reports ready only once the platform has issued it a workload token; build the image with the documented command and confirm it runs as a non-root user on a read-only root filesystem with the required labels and no secrets in any layer.

**Acceptance Scenarios**:

1. **Given** a missing or invalid required setting, **When** the server starts, **Then** it stops with a message that names the setting and never prints secret values.
2. **Given** a running server, **When** the orchestrator probes liveness, **Then** it answers healthy while the process serves requests, independent of the platform.
3. **Given** a running server, **When** the orchestrator probes readiness, **Then** it reports ready only while the server can authenticate to the platform with its workload credentials, and not ready otherwise.
4. **Given** the documented image build command with a source revision and release version, **When** the image is built and run, **Then** it carries the repository's image labels, runs as a fixed non-root numeric user on a read-only root filesystem with one writable temporary location, listens on one unprivileged port, declares no in-container health probe, and contains no secrets or stamp values.

---

### Edge Cases

- **Attempt becomes stale during a session**: Later calls are still forwarded and receive the platform's stale disposition; the server never answers from earlier results.
- **Scope lost during a session**: The platform denies the call and fences the attempt; the runtime receives the "scope lost" disposition, and every later call of the session receives the platform's stale disposition.
- **Platform unreachable or slow**: A forwarded call that gets no answer within the configured timeout fails with an explicit "platform unavailable" tool error; the server does not retry it, because a retry is a new tool call that counts against the attempt's limits.
- **Workload token expired between calls**: The server obtains a new workload token before forwarding; when the platform refuses the server's credentials, the call fails with an explicit authentication error and readiness turns not ready.
- **Concurrent calls in one session**: Each call is forwarded on its own with its own call identifier; their results are returned to the calls that made them.
- **Tool policy pinned with a tool the allowlist does not know**: The server exposes only the intersection of the pinned policy and the allowlist it was built from; an unknown tool is never exposed.
- **Allowlist version mismatch**: When the platform publishes an allowlist version the server was not built from, the mismatch is detected by the server's conformance tests before release, never at run time by guessing.
- **Large results**: Fact pages and clip resolutions are returned as the platform bounds them; the server does not page, merge, or truncate them.
- **Session limit reached or idle session**: New sessions beyond the configured maximum are refused; idle sessions end after the configured timeout and must be reopened for the same attempt.
- **Runtime asks for resources, prompts, or sampling**: The server offers only tools; other MCP capabilities are not advertised and requests for them are refused.

## Requirements *(mandatory)*

### Functional Requirements

#### Project and tool surface

- **FR-001**: The server MUST be an independent, locked Python project directly under `src/agents/` that satisfies the agent project convention of [Agents Python Verification Environment](../archive/20261018-110526-agents-python-environment/spec.md) and is built on the official MCP Python SDK.
- **FR-002**: The server MUST expose exactly one MCP tool per entry of the platform's tool allowlist contract (`contracts/agent-tools/tool-allowlist/v1`, allowlist version 1.0.0, of [Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/spec.md)), with the entry's tool identifier, the argument schema of its platform operation as the tool's input schema, and the platform operation's success response, whose result has the entry's result schema, as the tool's output schema; it MUST expose no other tool and no MCP resources, prompts, or sampling.
- **FR-003**: The server's tool definitions MUST be derived from the allowlist contract and the platform's published API description, and automated tests MUST fail when the server's tools differ from them in identifier, operation, argument schema, result schema, or answer kind.

#### Clients and session binding

- **FR-004**: The server MUST accept sessions only from the agent runtime inside the stamp, authenticated by a configured shared secret read from a mounted file, presented on every request, and compared in constant time; it MUST refuse unauthenticated clients without creating a session, and this feature MUST add no definition that publishes the server outside the stamp.
- **FR-005**: Every session MUST be bound when it is opened to exactly one attempt, named by its attempt identifier and current fencing token, and the binding MUST NOT change for the life of the session; one attempt per session also applies to the attempts of delegated consultations.
- **FR-006**: When a session is opened, the server MUST confirm the binding by reading the attempt's pinned artifacts through the platform's attempt-scoped workload read ([Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md), FR-022 there) and MUST refuse the session, returning the platform's stale disposition or not-found answer, when the platform does not confirm the attempt as current with that fencing token. The session's tool policy MUST be the pinned tool policy from that read and MUST NOT be taken from the agent runtime.
- **FR-007**: Tool arguments MUST be validated against the tool's argument schema before forwarding; arguments outside the schema, including any team, match, conversation, run, or attempt reference, MUST be refused as invalid without a request to the platform.
- **FR-008**: The server MUST list and accept only tools that are both in the allowlist and in the tool policy pinned into the session's invocation; a call to any other tool MUST be refused as not permitted without a request to the platform. The platform's enforcement stays authoritative.

#### Platform access and forwarding

- **FR-009**: The server MUST authenticate to the platform only with its own configured agent workload client and private key ([Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md), FR-014 there), distinct from the agent runtime's, obtaining short-lived, proof-bound workload tokens; it MUST NEVER use or accept member credentials and MUST NOT pass the agent runtime's credentials to the platform.
- **FR-010**: The server MUST call only the platform operations it needs: the workload token operation, the attempt-scoped pinned-artifact read, and the operations of the agent tool route group; it MUST NOT access databases, object storage, metadata tables, or message transport.
- **FR-011**: Every permitted tool call MUST be forwarded as exactly one request to the tool's platform operation, carrying the session's attempt, its fencing token, a call identifier that the server generates fresh for every forwarded call, and the validated arguments; the server MUST return the call identifier with the result and MUST NOT retry a forwarded call.
- **FR-012**: The server MUST return the platform's answer faithfully: answered and unavailable outcomes with their result unchanged, and every refusal, denial, stale disposition (including scope lost), and failure as a tool error that carries the platform's status, problem code, and disposition unchanged; a call without a platform answer within the configured timeout MUST fail with an explicit "platform unavailable" tool error.
- **FR-013**: The server MUST NOT derive, interpret, aggregate, merge, cache, or persist evidence; every result exists only for the duration of the call that produced it.
- **FR-014**: The server MUST log only structured records on standard output: per forwarded or locally refused call the attempt identifier, call identifier, tool identifier, outcome, platform status, and duration, and session open and close events; logs and error reports MUST NOT contain tool arguments, results, fact payloads, links, fencing tokens, access tokens, keys, or secrets. Metrics and trace export are left to the stamp composition.

#### Operation and packaging

- **FR-015**: The server MUST expose a liveness endpoint that answers while the process serves requests and a readiness endpoint that reports ready only while the server holds or can obtain a valid workload token from the platform.
- **FR-016**: The server MUST read its configuration only from environment variables and mounted files, read secrets and keys only from files, validate the configuration at start, and stop with a message naming each invalid setting without printing secret values.
- **FR-017**: The server MUST bound its resource use with configured limits on concurrent sessions, session idle time, and platform request timeout, with development values; production values remain open.
- **FR-018**: The repository MUST contain an OCI image definition for the server that follows the rules of [Cloud-Neutral OCI Images](../archive/20261018-092503-oci-images/spec.md): staged build from the source revision, base image pinned by digest, required source revision and release version build inputs, the standard image labels, a fixed non-root numeric user, a read-only root filesystem with one writable temporary location, one unprivileged listening port, no in-container health probe, configuration only from environment variables and mounted files, and no secrets or stamp values in any layer.
- **FR-019**: The server's build, test, run, and image build commands MUST be documented with the repository's other supported commands, and the agents source-area description MUST name the server as its first executable project.

#### Evidence

- **FR-020**: Automated tests MUST run without network access beyond locked dependency installation and MUST cover: tool listing and definitions against the allowlist contract and its fixtures; forwarding of each tool to its platform operation against a fake platform; faithful return of every outcome, refusal, and stale disposition; session authentication and binding; local refusal of tools outside the pinned policy and of invalid arguments; the platform credential flow; the absence of evidence, links, tokens, and secrets from logs; configuration validation; health and readiness; and the image definition's rules.

### Key Entities

- **MCP session**: One connection of the agent runtime, authenticated by the runtime's shared secret and bound to exactly one attempt (attempt identifier and fencing token) and to the tool policy pinned into its invocation, as read from the platform, for its whole life.
- **Exposed tool**: An MCP tool that mirrors one allowlist entry: tool identifier, platform operation, argument schema, result schema, and answer kind.
- **Forwarded call**: One tool call relayed to one platform operation with the session's attempt binding and a server-generated call identifier; its result is returned to the caller with that identifier and not kept.
- **Workload credentials**: The server's own configured agent workload client and private key, distinct from the agent runtime's, with which it obtains short-lived, proof-bound platform tokens.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated tests, 100% of the allowlist's tools are exposed with identifiers, argument schemas, and result schemas equal to the contract and the platform's description, and 0 other tools, resources, or prompts are exposed.
- **SC-002**: In automated tests, 100% of permitted tool calls produce exactly one platform request for the session's attempt, and 100% of platform answers, refusals, and stale dispositions reach the runtime unchanged.
- **SC-003**: In automated tests, 100% of calls to tools outside the pinned tool policy, with invalid or scope-widening arguments, or from unauthenticated clients are refused with 0 platform requests.
- **SC-004**: In automated tests, 0 log records, metrics, or error reports contain a link, fact payload, fencing token, access token, key, or secret.
- **SC-005**: A configured server reports ready within 10 seconds of start when the platform is reachable, and an invalid configuration stops it within 5 seconds with every invalid setting named.

## Assumptions

- **Platform authority**: The agent tool operations, their allowlist contract, attempt binding, call limits, scope loss, audit, and lineage are those of [Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/spec.md); this feature adds no tool, no platform operation, and no evidence semantics.
- **Agent runtime**: Hermes ([Intelligence and Agents → Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)) is the later client of the server; it claims attempts, opens one session per attempt, and calls tools for its models. Until it exists, test clients stand in for it.
- **Not the message bus**: The server carries no agent-to-agent messages, consultations, or notifications; delegation remains with Agent Orchestration ([Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority)).
- **Configured values**: Session, idle, timeout, and token values have development values; production values, the stamp's internal transport security, and the deployment of the server's credentials remain open ([Production Deployment and Operations](../../docs/architecture/production-operations.md)).
- **Out of scope**: Hermes and model calls, new tools or platform operations, write tools, MCP resources, prompts, or sampling, exposure outside the stamp, the stamp composition beyond what [Compose Stamp Composition](../archive/20261018-094332-compose-stamp/spec.md) can reference later, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
- **Dependencies**: [Agents Python Verification Environment](../archive/20261018-110526-agents-python-environment/spec.md) (environment; must merge first), [Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/spec.md) (tool operations and allowlist contract), [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md) (workload identity, attempts, fencing, stale dispositions), [Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md) (pinned tool policy and its attempt-scoped read), and [Cloud-Neutral OCI Images](../archive/20261018-092503-oci-images/spec.md) (image rules).
- **Architecture References**: [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md) (MCP Layer; MCP Tools; Agent Orchestration Authority; Authorization, Lineage, And Lifecycle), [Architecture Overview](../../docs/architecture/overview.md) (Intelligence Plane), [Intelligence Agent Catalog → MCP Tool Policy](../../docs/architecture/intelligence-agent-catalog.md#mcp-tool-policy), and [Platform Implementation Profile → Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime).
