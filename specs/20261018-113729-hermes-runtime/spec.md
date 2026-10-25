# Feature Specification: Hermes Agent Runtime

**Feature Branch**: `20261018-113729-hermes-runtime`

**Created**: 2026-10-18

**Status**: Draft

**Input**: User description: "Hermes Agent Runtime: the architecture makes Hermes a separate Python OCI service using Microsoft Agent Framework behind local adapters, recoverable from durable control-plane state, with no direct database access; it authenticates as a stamp-local workload, claims work, renews its bounded lease, checkpoints progress, and completes or fails attempts through Agent Orchestration; it resolves the versioned prompt, model/deployment, tool-policy, and safety-policy artifacts for each invocation and fails on invalid or incompatible artifacts without implicit fallback; it uses MCP for evidence; OpenAI API is the default model provider, GitHub Models an explicitly and manually selected development alternative, configuration selects exactly one provider and deployment with no silent fallback, a provider-neutral adapter isolates SDK details, and lineage records provider, model, deployment or configuration, and resolved parameters for every invocation (docs/architecture/intelligence-and-agents.md#agent-orchestration-authority, #client-transport-and-runtime-protocol, #authorization-lineage-and-lifecycle; docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance, #specialist-agent-catalog; docs/architecture/platform-implementation.md#agent-runtime). Nothing of Hermes exists. This feature creates Hermes as an independent locked Python project under `src/agents/` (the plan names it) for direct User->Specialist Agent invocations: it claims queued invocations from #155, reads the invocation's pinned catalog artifacts through spec 4's attempt-scoped read, renders the Specialist Agent's system context from the pinned prompt template and the conversation context the envelope carries, opens one MCP session per attempt with spec 7's server and lets the model call only the session's tools, renews the lease and checkpoints while working, and completes with advice that follows the catalog's shared response contract (attribution, uncertainty, evidence references, refusal of unsupported or unauthorized requests) plus full lineage; failures, stale dispositions, scope loss, provider errors, and invalid artifacts end the attempt explicitly so the platform's retry rules apply; it never stores conversation content outside the platform and never logs prompts, evidence, or advice. The provider-neutral adapter implements the OpenAI provider and the GitHub Models alternative behind configuration, plus a deterministic scripted test provider used by all automated tests so no test needs network access or secrets. Health and readiness, configuration through environment variables and mounted files, secrets from files, and an OCI image definition consistent with spec 1's rules. Automated evidence: unit tests with the scripted provider and fakes for the platform and MCP; and the cross-process evidence spec 7 deferred - Hermes, the MCP server, and the platform API running together against disposable dependencies so that a submitted direct invocation is claimed by Hermes, calls a tool through MCP, and completes with lineage visible through #155's member reads. Out of scope: Coach Agents and philosophy rendering, consultations, evaluation suites and promotion gates, real provider calls in automated tests, streaming, Compose integration, production provider configuration and secrets. Depends on specs/archive/20261017-204332-agent-orchestration-invocations (#155), specs/archive/20261018-101517-agent-catalog-registry, specs/20261018-104924-agent-evidence-tools, specs/20261018-111804-mcp-server, specs/20261018-110526-agents-python-environment, specs/archive/20261018-092503-oci-images."

## Clarifications

### Session 2026-10-18

- Q: How does the lineage record the resolved generation parameters and the model selection used, given that the completion today declares only provider, model, and deployment? → A: The completion contract gains a backward-compatible minor version with the pinned model-selection reference and the resolved parameters, and the platform refuses a completion whose model-selection reference, provider, model, deployment, or parameters differ from the pinned model selection.
- Q: Where does Hermes run during local development, given that the AppHost already runs the simulated agent workload on the same work queue? → A: Not in the AppHost: the AppHost keeps the simulated agent workload, and Hermes is run locally only by its documented command against a platform whose simulated workload is not running; automated tests are the evidence.
- Q: How many attempts may one Hermes instance execute at the same time? → A: A configured maximum per instance, development value 2.
- Q: What happens to the attempts Hermes holds when it is stopped? → A: It stops taking notifications and reports each active attempt as a retryable failure with reason "runtime stopping" within the shutdown time, so retries start without waiting for lease expiry; attempts it cannot report end through lease expiry.
- Q: What observability does Hermes provide? → A: Structured logs on standard output with identifiers, outcomes, failure reasons, counts, and durations per attempt, claim, model turn, and tool call; metrics and trace export are left to the stamp composition.

## User Scenarios & Testing *(mandatory)*

The actors are the member who asks a Specialist Agent a question in a triage conversation, the Hermes agent runtime that executes the work as a stamp-local workload, and the stamp operator who runs Hermes next to the platform API and the MCP server. Conversations, invocations, attempts, leases, fencing, stale dispositions, scope loss, retries, accepted advice, and lineage are owned by the platform's Agent Orchestration ([Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md)); the agent version and artifacts an invocation uses are pinned by the platform at submission ([Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md)); evidence is reachable only through the MCP server ([MCP Server](../20261018-111804-mcp-server/spec.md)), which forwards to the platform's agent tool operations ([Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/spec.md)). Hermes owns only active execution context, as [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority) requires: it chooses no agent or artifact version, holds no durable state, and is recoverable from the platform's state at any moment. Until now, a simulated agent workload stands in for it in Development and Test.

### User Story 1 - A specialist answers a member's question with evidence-backed advice (Priority: P1)

A Coach asks `specialist-tactics` a question about an analysed match. Hermes receives the work notification, claims the invocation, reads the agent version and artifacts pinned into it, renders the specialist's system context, opens one MCP session for the attempt, and lets the model consult only the tools the pinned tool policy permits. It keeps the attempt alive while it works, records its progress, and completes the attempt with advice that follows the catalog's shared response contract. The Coach reads the advice, and its lineage shows the pinned versions, the model provider, model, deployment, and parameters used, and every evidence tool call.

**Why this priority**: It replaces the simulated agent workload with the real runtime and delivers the first catalog wave's direct specialist conversations; everything else hardens this path.

**Independent Test**: With a fake platform and a fake MCP server, and the scripted model provider scripted to call `get_match` once and then answer, deliver a work notification and confirm that Hermes claims the invocation, reads the pinned artifacts, opens exactly one MCP session for the attempt with its attempt identifier and fencing token, exposes only the session's tools to the model, renews the lease, records checkpoints, and completes the attempt with advice containing the five response-contract sections and execution references equal to the pins and the resolved model selection.

**Acceptance Scenarios**:

1. **Given** a queued direct invocation of an enabled Specialist Agent, **When** Hermes receives its work notification, **Then** it claims the invocation exactly once and starts one attempt with the envelope, attempt identifier, lease, and fencing token the platform returned.
2. **Given** a claimed attempt, **When** Hermes prepares the execution, **Then** it reads the agent version and every artifact pinned into the invocation through the attempt-scoped read and uses exactly those versions; it never chooses, substitutes, or caches another version.
3. **Given** the pinned prompt template, agent version, tool policy, safety policy, and the envelope's question and earlier turns, **When** Hermes renders the system context, **Then** every declared rendering slot is filled, the safety policy's hard boundaries and refusal and escalation categories and the shared response contract are stated, earlier turns are attributed to their agents, and the member's question and every tool result stay outside the system context.
4. **Given** a running attempt, **When** the model requests evidence, **Then** Hermes calls the requested tool only through the attempt's single MCP session and only when the session lists it; the tool's result, including "analysis unavailable" and refusals, reaches the model as returned.
5. **Given** a model answer, **When** Hermes completes the attempt, **Then** the advice contains, in order, the sections Assessment, Evidence, Uncertainty and alternatives, Recommendations, and Next questions or consultations, and the completion declares the runtime, the pinned prompt, model selection, tool policy, and safety policy references, the provider, model, and deployment of the pinned model selection, and the resolved generation parameters.
6. **Given** a succeeded invocation, **When** its owner reads the invocation and its lineage, **Then** the lineage shows the attempt, Hermes' checkpoints, every tool call the attempt made through MCP, and the accepted advice with its execution references.

---

### User Story 2 - Failures end attempts explicitly and recover from durable state (Priority: P2)

Things go wrong: a pinned artifact is invalid or incompatible, the pinned model provider is not the one Hermes is configured for, the provider fails, the MCP server is unavailable, the platform answers that the attempt is stale or that the member lost scope, or Hermes itself stops. In every case Hermes ends its work explicitly so the platform's retry rules decide what happens next, never falls back to another provider, model, or artifact, and never answers from anything but the attempt's own evidence. A restarted Hermes holds nothing it must recover; the platform's leases, retries, and reconciliation bring the work back.

**Why this priority**: Agent work is long-running and depends on external providers; without explicit, classified endings the platform cannot retry safely and members would wait without an outcome. It builds on Story 1.

**Independent Test**: With the fakes, run attempts whose pinned artifacts fail validation, whose model selection names another provider, whose scripted provider fails transiently or permanently, whose MCP session cannot be opened, whose platform answers renewals or completions with each stale disposition, and whose tool calls return "scope lost", and confirm for each the exact failure reason and retryability reported, or that Hermes stops without reporting when the platform already ended the attempt; stop Hermes during an attempt and confirm it claims nothing new and ends its active attempts as the shutdown rules require.

**Acceptance Scenarios**:

1. **Given** a pinned artifact that is missing, fails its contract, does not match its pinned digest, or belongs to the wrong agent group, or a safety policy that does not permit the direct specialist route, **When** Hermes prepares the attempt, **Then** it fails the attempt as not retryable with a reason naming the invalid or incompatible artifact, without calling a model or a tool.
2. **Given** a pinned model selection whose provider differs from the one provider Hermes is configured for, **When** Hermes prepares the attempt, **Then** it fails the attempt as not retryable with reason "model provider mismatch" and calls no provider.
3. **Given** a provider request that fails transiently (unreachable, timed out, rate limited, or a server error), **When** the attempt cannot continue, **Then** Hermes fails it as retryable; **Given** a provider refusal or authentication failure, **Then** Hermes fails it as not retryable; it never switches to another provider, model, or deployment.
4. **Given** an MCP session that cannot be opened or an MCP server that stops answering, **When** the attempt needs evidence, **Then** Hermes fails the attempt as retryable and never lets the model answer without the session.
5. **Given** a renewal, checkpoint, completion, tool call, or MCP session refusal answered with a stale disposition, including "scope lost", **When** Hermes receives it, **Then** it stops the attempt at once, discards the attempt's partial work, and reports nothing more for that attempt.
6. **Given** a claim answered as obsolete, **When** Hermes receives it, **Then** it acknowledges the notification and starts no work.
7. **Given** Hermes restarting at any moment, **When** it starts again, **Then** it resumes only by claiming notified work; attempts it held end through the platform's lease expiry or its own explicit ending, and the platform's retries complete the invocations.

---

### User Story 3 - Operators run, configure, probe, and package Hermes safely (Priority: P3)

A stamp operator configures Hermes through environment variables and mounted files — its workload client and key, the MCP server and its shared secret, exactly one model provider with its credentials, and its limits — starts it as a container next to the platform API and the MCP server, and lets the orchestrator probe its liveness and readiness. Misconfiguration stops Hermes at start with a message naming the setting. Hermes logs only identifiers and outcomes, never prompts, questions, evidence, links, advice, tokens, or secrets. The image follows the repository's container image rules.

**Why this priority**: Hermes is a separate deployable of the intelligence plane; without configuration, health, safe logs, and an image no stamp can run it. Stories 1 and 2 prove its behavior first.

**Independent Test**: Start Hermes with each required setting missing or invalid and confirm it stops naming the setting; start it against fakes and confirm liveness answers at once and readiness reports ready only once it holds a platform workload token and its work notification subscription; drive every outcome with sentinel prompt, question, evidence, link, advice, token, and secret values and confirm none appears in the logs; build the image with the documented command and confirm it runs as a non-root user on a read-only root filesystem with the required labels and no secrets in any layer.

**Acceptance Scenarios**:

1. **Given** a missing or invalid required setting, an unknown model provider, or a provider without its required credential or script file, **When** Hermes starts, **Then** it stops with a message naming each invalid setting and never prints secret values.
2. **Given** the scripted provider configured outside Development and Test, **When** Hermes starts, **Then** it stops naming the provider setting.
3. **Given** a running Hermes, **When** the orchestrator probes liveness, **Then** it answers healthy while the process runs, independent of the platform; **When** it probes readiness, **Then** Hermes reports ready only while it can authenticate to the platform and receive work notifications.
4. **Given** any attempt outcome, **When** the logs are inspected, **Then** each record contains only identifiers, the agent identifier, the provider and model names, tool identifiers, outcomes, failure reasons, counts, and durations.
5. **Given** the documented image build command with a source revision and release version, **When** the image is built and run, **Then** it carries the repository's image labels, runs as a fixed non-root numeric user on a read-only root filesystem with one writable temporary location, declares no in-container health probe, and contains no secrets, provider credentials, or stamp values.

---

### User Story 4 - The runtime, the MCP server, and the platform prove the path together (Priority: P4)

The evidence the MCP server deferred is produced: Hermes, the MCP server, and the platform API run as separate processes against disposable dependencies, and a member's direct question is claimed by Hermes, consults evidence through the MCP server, and completes with advice and lineage that the member reads through the platform's ordinary reads.

**Why this priority**: The unit evidence of each component uses fakes; only a run of the real components together proves that their authentication, attempt binding, transport, and contracts agree. It needs Stories 1 to 3.

**Independent Test**: In an automated test with disposable dependencies, start the platform API, the MCP server, and Hermes as separate containers with the scripted provider scripted to call `get_match` and then answer; as the owning Coach, create a conversation for an analysed match, add `specialist-tactics`, submit a question, poll until the invocation has succeeded, and confirm that the advice is attributed to `specialist-tactics`, that the lineage lists one `get_match` tool call made by the attempt, and that the execution references equal the pins and the scripted model selection.

**Acceptance Scenarios**:

1. **Given** the three components and their disposable dependencies, **When** a member submits a direct question to an enabled specialist, **Then** Hermes claims it, the MCP server forwards its tool call to the platform for that attempt, and the invocation succeeds within the test's time bound.
2. **Given** the succeeded invocation, **When** the owner reads the invocation, its turns, and its lineage, **Then** they show the advice, the tool call entry with its tool identifier and outcome, and execution references naming Hermes, the pinned artifacts, and the scripted model selection.

---

### Edge Cases

- **Duplicate notification**: A notification for an invocation Hermes already works on, or one already ended, leads at most to an obsolete claim; Hermes never runs two attempts of one invocation at once.
- **Lease about to expire**: Hermes renews well before expiry; when a renewal cannot reach the platform before the lease expires, Hermes stops the attempt, because the platform will fence it.
- **Attempt deadline**: Hermes bounds its work by the attempt's deadline; work that cannot finish before it ends the attempt as retryable rather than letting the platform fence it.
- **Model never stops calling tools**: The number of model turns per attempt is bounded; reaching the bound ends the attempt explicitly.
- **Platform tool call limit reached**: The model receives the limit refusal as a tool result and must answer from the evidence it has, stating the limit; Hermes does not open another session to work around it.
- **Tool not permitted**: The model never sees tools outside the session; a forged tool request from the model is refused locally and counts as a model turn.
- **Question asks for unauthorized data or another team's match**: Tools cannot widen the scope; the advice states the refusal as the response contract requires.
- **Prompt injection in the question or in evidence**: Question text and tool results are passed only as user and tool content, never as system instructions, and cannot change the tools, the scope, or the response contract.
- **Earlier turns of other agents**: History from other participants is shown as their attributed advice, never as the current agent's own words.
- **Pinned prompt template without slots**: Templates without declared slots (such as the first-draft seed prompts) are used verbatim with the runtime's safety and response-contract section added.
- **Provider returns no text or empty advice**: The attempt fails as retryable; empty advice is never completed.
- **Hermes sees a Coach Agent or consultation route**: Coach Agent versions and `CA -> SA` work are not supported yet; Hermes fails such an attempt as not retryable with reason "unsupported route" rather than executing it as a specialist.
- **Several Hermes instances**: Instances share the platform's work queue; the platform's claims ensure each invocation has at most one active attempt.
- **MCP server restarted mid-attempt**: The session is lost; the next tool call fails and the attempt ends as retryable.

## Requirements *(mandatory)*

### Functional Requirements

#### Project and dependencies

- **FR-001**: Hermes MUST be an independent, locked Python project directly under `src/agents/` that satisfies the agent project convention of [Agents Python Verification Environment](../archive/20261018-110526-agents-python-environment/spec.md) and uses Microsoft Agent Framework for agent execution, with framework-specific types kept behind Hermes' own adapters, as [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority) requires.
- **FR-002**: Hermes MUST have no database, object storage, or metadata access and no durable state of its own; framework persistence and checkpoint features MUST NOT be used as a second workflow authority, and conversation content MUST exist in Hermes only in memory for the duration of the attempt that uses it.

#### Workload identity and work intake

- **FR-003**: Hermes MUST authenticate to the platform only as its own configured agent workload client with its own private key, obtaining short-lived, proof-bound workload tokens as [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md) defines (FR-014 there); its client and key MUST be distinct from the MCP server's, and it MUST never use member credentials.
- **FR-004**: Hermes MUST receive work through the platform's agent work notifications (with platform-issued queue credentials where the stamp requires them) and claim each notified invocation with a fresh operation identifier; an obsolete claim MUST end the handling of that notification without work.
- **FR-005**: Hermes MUST bound the number of attempts it executes at once by configuration (development value 2) and MUST NOT take further notifications while the bound is reached, so that unclaimed work stays available to other instances.
- **FR-006**: Hermes MUST execute only `User -> SA` invocations of Specialist Agent versions; any other route or agent group MUST end the attempt as not retryable with reason "unsupported route".

#### Artifact resolution and validation

- **FR-007**: For every attempt, Hermes MUST read the agent version and the artifacts pinned into the invocation through the attempt-scoped pinned-artifact read ([Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md), FR-022 there) and MUST use exactly those artifacts; it MUST NOT read, cache across attempts, or substitute any other version.
- **FR-008**: Hermes MUST validate each pinned artifact before any model or tool call: it MUST match its contract, its content digest MUST equal the pin in the execution envelope, its agent group MUST be the Specialist Agent group, its safety policy MUST permit the `User -> SA` route, and its prompt template's declared slots MUST be ones Hermes can render; any violation MUST end the attempt as not retryable with reason "invalid artifact" or "incompatible artifact", without implicit fallback.

#### Model providers

- **FR-009**: Hermes MUST reach models only through a provider-neutral adapter that isolates provider SDK details and offers exactly these providers: the OpenAI API (default), GitHub Models (an explicitly and manually selected development alternative), and a deterministic scripted provider that serves the catalog's simulated provider and is available only in Development and Test.
- **FR-010**: Configuration MUST select exactly one provider with its endpoint and credentials; the pinned model selection MUST name that provider, and its model, deployment or configuration, and parameters MUST be used exactly as pinned. A pinned provider other than the configured one MUST end the attempt as not retryable with reason "model provider mismatch"; Hermes MUST never fall back to another provider, model, deployment, or parameter set.
- **FR-011**: Provider credentials MUST be read only from files named by configuration, MUST never appear in logs, errors, checkpoints, completions, or images, and MUST NOT be part of any catalog artifact.
- **FR-012**: The scripted provider MUST produce predetermined tool requests and answers from a configured script without network access, so that every automated test runs without a model service, credentials, or secrets.

#### Rendering and execution

- **FR-013**: Hermes MUST render the system context from the pinned prompt template by filling its declared slots (agent identifier, display name, the pinned tool policy's tools, and the evidence context of the invocation's match and bound run) and adding a runtime section, versioned with Hermes, that states the pinned safety policy's hard boundaries, refusal and escalation categories, and the shared response contract of [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance).
- **FR-014**: The envelope's earlier turns MUST be presented as conversation history attributed to their agents, the member's question as the current user message, and tool results as tool content; none of them MUST become part of the system context.
- **FR-015**: Hermes MUST open exactly one MCP session per attempt with the MCP server, authenticated with the configured shared secret and bound to the attempt's identifier and current fencing token, and MUST offer the model only the tools that session lists; a model request for any other tool MUST be refused without contacting the MCP server.
- **FR-016**: Hermes MUST bound each attempt by a configured maximum number of model turns and a model request timeout, and by the attempt's deadline from the envelope; reaching a bound MUST end the attempt explicitly (failure reasons and retryability per FR-021).
- **FR-017**: Hermes MUST renew the attempt's lease before it expires for as long as it works on the attempt and MUST record checkpoints at least when the artifacts are resolved, when the MCP session is opened, and after each model turn; checkpoint notes MUST contain only counts, tool identifiers, and outcomes, never prompt, question, evidence, or advice text.

#### Completion and lineage

- **FR-018**: Hermes MUST complete an attempt only with non-empty advice within the envelope's advice limit that contains the five sections of the shared response contract in order; advice that violates these rules MUST NOT be completed.
- **FR-019**: Every completion MUST declare the runtime with its version, the pinned prompt template, model selection, tool policy, and safety policy references, the provider, model, and deployment of the pinned model selection, and the resolved generation parameters sent to the provider, so that the invocation's lineage records them as [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority) requires; evidence references enter the lineage through the platform's tool-call entries.
- **FR-019a**: The platform's completion contract MUST gain a backward-compatible minor version that adds the pinned model-selection reference and the resolved generation parameters to the declared execution references; the platform MUST refuse, with the existing execution-references mismatch answer and without change, a completion whose model-selection reference, provider, model, deployment, or parameters differ from the invocation's pinned model selection, and the owner's invocation and lineage reads MUST show the resolved parameters with the other execution references.
- **FR-020**: Hermes MUST use stable operation identifiers per attempt for its claim, checkpoints, completion, and failure, so that a repeated request after a lost answer returns the platform's recorded outcome instead of a second change.

#### Failures, stale dispositions, and shutdown

- **FR-021**: Hermes MUST end every attempt it cannot complete with exactly one failure report carrying a bounded reason code and whether it is retryable, following one documented classification: invalid or incompatible artifacts, model provider mismatch, provider refusal or authentication failure, unsupported route, and the model-turn bound are not retryable; transient provider errors, MCP unavailability, empty or contract-violating advice, the attempt deadline, runtime stopping, and unexpected runtime errors are retryable.
- **FR-022**: When the platform answers any workload operation, pinned-artifact read, or tool call (directly or through the MCP server) with a stale disposition, including "scope lost", Hermes MUST stop the attempt at once, discard its partial work, and send no further report for it.
- **FR-023**: When Hermes stops, it MUST stop taking notifications and report each active attempt as a retryable failure with reason "runtime stopping" within a configured shutdown time, so that retries start without waiting for lease expiry; attempts it cannot report end through the platform's lease expiry, and it MUST never complete advice for an attempt it no longer holds.

#### Operation, privacy, and packaging

- **FR-024**: Hermes MUST read its configuration only from environment variables and mounted files and its secrets and keys only from files, validate the configuration at start, and stop with a message naming every invalid setting without printing secret values.
- **FR-025**: Hermes MUST expose a liveness endpoint that answers while the process runs and a readiness endpoint that reports ready only while it holds or can obtain a platform workload token and receives work notifications.
- **FR-026**: Hermes MUST log only structured records on standard output for each claim, attempt, model turn, and tool call, with identifiers (invocation, attempt, correlation, operation, call), the agent identifier, provider and model names, tool identifiers, outcomes, failure reasons, counts, and durations; prompts, rendered context, questions, earlier turns, tool arguments and results, links, advice, tokens, keys, and secrets MUST NOT appear in logs or error reports. Metrics and trace export are left to the stamp composition.
- **FR-027**: The repository MUST contain an OCI image definition for Hermes that follows the rules of [Cloud-Neutral OCI Images](../archive/20261018-092503-oci-images/spec.md): staged build from the source revision, base image pinned by digest, required source revision and release version inputs, the standard labels, a fixed non-root numeric user, a read-only root filesystem with one writable temporary location, no in-container health probe, configuration only from environment variables and mounted files, and no secrets, provider credentials, or stamp values in any layer.
- **FR-028**: Hermes' build, test, run, and image build commands MUST be documented with the repository's other supported commands, and the agents source-area description MUST name Hermes next to the MCP server.

#### Evidence

- **FR-029**: Automated tests MUST run without network access beyond locked dependency installation and without model services or secrets, and MUST cover: work intake and claims, artifact resolution and every validation failure, provider selection and mismatch, rendering, the per-attempt MCP session and tool restriction, lease renewal and checkpoints, completion content and execution references, every failure classification, every stale disposition and scope loss, shutdown, configuration validation, health and readiness, the absence of content and secrets from logs, and the image definition's rules; platform tests MUST cover the completion contract's new version, its fixtures, the model-selection mismatch refusals, and the resolved parameters in the owner's reads.
- **FR-030**: An automated cross-process test MUST run the platform API, the MCP server, and Hermes as separate processes against disposable dependencies and prove that a member's direct question is claimed by Hermes, calls at least one tool through the MCP server, and completes with advice and lineage visible through the member reads of [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md); this is the cross-process evidence the [MCP Server](../20261018-111804-mcp-server/spec.md) deferred.

### Key Entities

- **Attempt execution**: Hermes' in-memory work on one claimed attempt: the envelope, the pinned artifacts, the rendered context, the MCP session, the model turns, and the lease renewal, discarded when the attempt ends.
- **Rendered context**: The system context built from the pinned prompt template, its filled slots, and Hermes' versioned runtime section, plus the attributed history and the question; never persisted or logged.
- **Provider configuration**: The one configured model provider with its endpoint and credential file; it must equal the provider of the pinned model selection.
- **Execution references**: The runtime and version, pinned artifact references including the model-selection reference, provider, model, deployment, and resolved parameters declared with each completion, checked against the pins and recorded in the lineage by the platform.
- **Failure report**: A bounded reason code and retryability that ends an attempt Hermes cannot complete.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the cross-process test, a submitted direct question is answered with accepted advice whose lineage shows at least one tool call through the MCP server within 60 seconds of submission.
- **SC-002**: In automated tests, 100% of completed attempts declare execution references equal to the invocation's pins and the resolved model selection, and 100% of their advice contains the five response-contract sections in order.
- **SC-003**: In automated tests, 100% of invalid or incompatible artifacts, provider mismatches, and stale dispositions end the attempt with 0 model or tool calls after the condition is detected, and 0 attempts complete with another provider, model, deployment, or artifact than pinned.
- **SC-004**: In automated tests, every failure scenario ends with exactly one failure report with the documented reason and retryability, or none when the platform already ended the attempt.
- **SC-005**: In automated tests, 0 log records, metrics, or error reports contain a prompt, question, evidence item, link, advice text, token, key, or secret.
- **SC-006**: A configured Hermes reports ready within 10 seconds of start when the platform is reachable, and an invalid configuration stops it within 5 seconds with every invalid setting named.

## Assumptions

- **Platform authority**: Conversations, invocations, attempts, leases, fencing, stale dispositions, scope loss, retries, accepted advice, and lineage are those of [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md); pins, the pinned-artifact read, and the completion reference check are those of [Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md); tool calls, limits, and tool-call lineage are those of [Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/spec.md); sessions and forwarding are those of [MCP Server](../20261018-111804-mcp-server/spec.md). Changes to the platform and the MCP server are limited to the completion contract of FR-019a and what the cross-process evidence of FR-030 needs to run them together.
- **Wave 1 only**: This feature implements the runtime for direct specialist conversations of [Implementation Waves](../../docs/architecture/intelligence-agent-catalog.md#implementation-waves) Wave 1, without its evaluation suites or promotion gates; approving and enabling versions stays an accountable decision only.
- **Simulated agent workload**: The platform's simulated agent workload stays the Development and Test stand-in for the AppHost's local composition and Web UI tests; Hermes is not added to the AppHost and is run locally only by its documented command against a platform whose simulated workload is not running, because both consume the same work queue.
- **Configured values**: Concurrency, model-turn bounds, timeouts, renewal margins, and shutdown time have development values; production values, production provider configuration and credentials, and the stamp's internal transport security remain open ([Production Deployment and Operations](../../docs/architecture/production-operations.md)).
- **Out of scope**: Coach Agents and philosophy-profile rendering, delegated consultations, evaluation suites and promotion gates, real provider calls in automated tests, streaming responses, the stamp composition beyond what [Compose Stamp Composition](../archive/20261018-094332-compose-stamp/spec.md) can reference later, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support, model quality, or production readiness.
- **Dependencies**: [Agents Python Verification Environment](../archive/20261018-110526-agents-python-environment/spec.md) (environment; must merge first), [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md), [Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md), [Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/spec.md), [MCP Server](../20261018-111804-mcp-server/spec.md), and [Cloud-Neutral OCI Images](../archive/20261018-092503-oci-images/spec.md).
- **Architecture References**: [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md) (Agent Orchestration Authority; Client, Transport, And Runtime Protocol; Authorization, Lineage, And Lifecycle), [Intelligence Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md) (Prompt And Advice Governance; Specialist Agent Catalog; Implementation Waves), and [Platform Implementation Profile → Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime).
