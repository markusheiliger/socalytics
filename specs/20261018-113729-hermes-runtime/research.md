# Research: Hermes Agent Runtime

All Technical Context items are resolved; no `NEEDS CLARIFICATION` remains. Each decision builds on the published
[Agent Orchestration Direct Invocations](../20261017-204332-agent-orchestration-invocations/spec.md) and the batch
siblings [Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md),
[Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/spec.md),
[MCP Server](../20261018-111804-mcp-server/spec.md),
[Agents Python Verification Environment](../20261018-110526-agents-python-environment/spec.md), and
[Cloud-Neutral OCI Images](../archive/20261018-092503-oci-images/spec.md), and on
[Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md).

## R1 - Project location, name, and dependencies

- **Decision**: One independent uv project at `src/agents/hermes/` with the distribution name `socalytics-hermes`, the
  import package `socalytics_hermes` (src layout), `requires-python = ">=3.12,<3.13"`, the `hatchling` build backend and
  `pytest` in a locked `dev` dependency group, and a committed `uv.lock`, as the agent project convention of the
  [environment interface](../20261018-110526-agents-python-environment/contracts/environment-interface.md) requires; no
  local path dependency (the MCP server is a separate deployable, not a library). Runtime dependencies, each declared
  directly where Hermes imports it: `agent-framework-core==1.22.0` (Microsoft Agent Framework: agents, chat clients,
  function tools, and its OpenAI chat client, which brings the `openai` SDK), `mcp==2.3.0` (official MCP Python SDK,
  the same version as the MCP server; client side only), `httpx` (platform client; already brought by `openai` and
  `mcp`), `pyjwt[crypto]` (ES256 client assertions and DPoP proofs), `jsonschema` (contract validation), `rfc8785`
  (RFC 8785 canonical JSON for artifact digests), `nats-py` and `nkeys` (JetStream work notifications and NATS user
  JWT signing), `starlette` and `uvicorn` (health endpoints; already brought by `mcp`). Exact versions are those
  resolved into `uv.lock` at implementation time; when `agent-framework-core` 1.22.0 and `mcp` 2.3.0 cannot be locked
  together, the implementation uses the `mcp` version the framework admits and records it, because Hermes uses only the
  stable MCP client API.
- **Rationale**: [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority)
  makes Hermes a separate Python OCI service on Microsoft Agent Framework; the environment verifies projects directly
  under `src/agents/<name>/` with CPython 3.12.15 and uv 0.13.1, and its research found `agent-framework-core` 1.22.0
  and `mcp` 2.3.0 admit the interpreter. `agent-framework-core` avoids the meta package's unrelated provider extras.
  Every library is pure Python or ships CPython 3.12 Linux wheels (`pynacl` for `nkeys` ships abi3 wheels); the locked
  tree is evidence the environment check produces.
- **Alternatives considered**: The meta package `agent-framework` (pulls Azure and other integrations Hermes does not
  use); a uv workspace with the MCP server (forbidden by the convention); a hand-written RFC 8785 serializer (number
  formatting is error-prone; `rfc8785` is small and tested against the RFC vectors); a Kiota Python client (the
  architecture's Python Kiota client remains planned, and Hermes needs eight workload operations whose shapes are fixed
  by JSON Schema contracts it validates against).

## R2 - Workload identity, platform client, and transport security

- **Decision**: Hermes is its own agent workload client (`SOCALYTICS_HERMES_WORKLOAD_CLIENT_ID`, development
  `hermes`) with an ECDSA P-256 private key in PKCS#8 PEM from `SOCALYTICS_HERMES_WORKLOAD_KEY_FILE`; the stamp
  configures the matching public JWK in `AgentOrchestration:Workloads:Clients` next to the MCP server's client. Token,
  client assertion, and DPoP rules are exactly those of the MCP server ([research R5 there](../20261018-111804-mcp-server/research.md#r5---platform-credentials-token-and-proofs)):
  `issueAgentWorkloadToken` with `private_key_jwt` and a DPoP proof, `Authorization: DPoP <token>` and a fresh proof
  per request, `htu` and `aud` from `SOCALYTICS_HERMES_PLATFORM_PUBLIC_BASE_URL` (default: the platform URL), token
  renewal below 30 seconds of lifetime and once after a `401` DPoP challenge. The HTTP client calls only the paths of
  the [runtime contract](contracts/hermes-runtime.md#3-outbound-calls-hermes--platform-and-mcp-server). An optional
  `SOCALYTICS_HERMES_PLATFORM_CA_FILE` (PEM bundle) adds a trusted certificate authority for the platform connection;
  the MCP server gains the equivalent optional `SOCALYTICS_MCP_PLATFORM_CA_FILE`, the only change to the MCP server.
- **Rationale**: FR-003; the platform requires HTTPS for workload operations, and the stamp's internal transport
  security is an open profile value, so a deployment and the cross-process test (R10) need a way to trust a stamp CA
  without baking it into the image. A separate client from the MCP server keeps one key per container and attributes
  claims, completions, and tool calls to different workload identities in the platform's audit.
- **Alternatives considered**: Sharing the MCP server's client and key (one key in two containers); relying on
  `SSL_CERT_FILE` (honored inconsistently by Python HTTP clients and invisible in the configuration contract).

## R3 - Work intake

- **Decision**: Hermes subscribes to the platform's agent work notifications as the durable pull consumer
  `agent-workload` of the JetStream stream `AGENT_WORK` (subject `agents.invocations.ready`), as
  [Agent Orchestration research R3](../20261017-204332-agent-orchestration-invocations/research.md#r3---work-notifications-and-workload-queue-access)
  defines. When the platform's `issueAgentWorkloadQueueCredentials` answers with credentials, Hermes generates an
  in-memory NATS user NKey, sends its public key, and connects to the returned `queueUrl` with the returned user JWT,
  renewing it before `expiresAt`; when it answers `409 queue-credentials-unavailable` (authentication mode `None`,
  Development and Test only), Hermes connects to `SOCALYTICS_HERMES_NATS_URL` without credentials. It fetches at most as
  many messages as it has free attempt slots (`SOCALYTICS_HERMES_MAX_CONCURRENT_ATTEMPTS`, development 2), validates
  each against `work-notification/v1`, and claims every notified invocation (an attempt whose route or agent group
  Hermes does not support is failed with `unsupported-route`, FR-006) with a fresh operation identifier
  `hermes-claim-<invocationId>-<uuid4>` that it reuses only for retries of that one request. The message is
  acknowledged once the claim has a definitive answer (granted, `claim-obsolete`, `agent-scope-lost`, or `404`); after a
  transient platform failure it is negatively acknowledged with a delay, and reconciliation republishes anything lost.
- **Rationale**: FR-004 and FR-005; notifications carry only identifiers, so all content comes from the claim. A per-
  delivery operation identifier lets a lost answer be replayed but makes a duplicate delivery to another instance an
  obsolete claim instead of a replay of someone else's attempt (operation identifiers are scoped per workload client,
  and all Hermes instances share one client).
- **Alternatives considered**: Deriving the claim operation identifier from `readyOccurrence` (a duplicate delivery to
  a second instance would replay the first instance's envelope and run one attempt twice); polling a "next work"
  operation (none exists; contradicts the architecture's notification transport).

## R4 - Claim, lease, checkpoints, completion, failure, and stale answers

- **Decision**: An `AttemptExecutor` per claimed attempt owns: a lease renewer that renews every `leaseSeconds / 3`
  and stops the attempt when a renewal is stale or when no renewal succeeded before `leaseExpiresAt`; a deadline that
  ends the work `SOCALYTICS_HERMES_DEADLINE_MARGIN_SECONDS` (development 10) before `deadlineAt`; checkpoints with
  operation identifiers `hermes-<attemptId>-checkpoint-<n>` of kinds `artifacts-resolved`, `evidence-session-opened`,
  and `model-turn` (note: turn number, tool identifiers requested, outcome counts); completion
  `hermes-<attemptId>-complete` and failure `hermes-<attemptId>-fail`. Workload requests other than the claim are
  retried at most twice with the same operation identifier on connection failures or `5xx` within the lease; a replay
  returns the recorded outcome. Any `409 agent-attempt-stale` (every disposition, including `scope-lost`) from a
  workload operation, the pinned-artifact read, the MCP session refusal, or a forwarded tool error (`origin: platform`,
  problem code `agent-attempt-stale`) cancels the attempt's work, discards its state, and sends nothing more.
  `409 execution-references-mismatch` or `400` on completion is a Hermes defect: the attempt is failed with
  `runtime-error` (retryable). Failure reason codes and retryability are the table in
  [data-model.md](data-model.md#failure-classification).
- **Rationale**: FR-016 to FR-023; renewing at a third of the lease survives one lost renewal; stable identifiers per
  attempt make lost answers safe (FR-020).
- **Alternatives considered**: Letting leases expire on every error (delays retries by a lease and loses the reason);
  framework checkpoint persistence (forbidden by the architecture as a second authority).

## R5 - Artifact resolution and validation

- **Decision**: After the claim, Hermes validates the envelope against `execution-envelope` 1.1.0 and requires its
  `agent` pins (an envelope without them is an `invalid-artifact` failure), then calls `readPinnedAgentArtifacts` once.
  For the agent version and each artifact it checks, in order: presence of exactly the pinned kinds (prompt template,
  model selection, tool policy, safety policy; a philosophy profile or modifier policy is `unsupported-route`), the
  record's JSON Schema from `contracts/agent-catalog/<kind>/v1`, `sha-256:` of its RFC 8785 canonical form equal to the
  envelope's pin, `group` = `specialist` for the agent version, prompt template, and safety policy, the safety policy's
  `routes` containing `user-to-specialist`, and prompt slots within `agent_id`, `display_name`, `tool_policy`,
  `evidence_context`. Schema, digest, or presence violations fail `invalid-artifact`; group, route, and slot violations
  fail `incompatible-artifact`; both are not retryable and happen before any model or MCP call. The released schemas
  Hermes needs (`execution-envelope` 1.1.0, `attempt-report` 1.1.0, `work-notification` 1.0.0, and the catalog's
  `agent-version`, `prompt-template`, `model-selection`, `tool-policy`, and `safety-policy` 1.0.0) are copied into
  package data by `tools/sync_contracts.py`, and a conformance test fails when a copy differs from the repository's
  `releases/` file.
- **Rationale**: FR-007 and FR-008; the platform already validated the records at registration, but Hermes must not
  execute a record it cannot verify, and digest checks make a corrupted or substituted read visible. Package data is
  needed because the image contains no repository files.
- **Alternatives considered**: Trusting the read without checks (no evidence that the executed artifacts are the
  pinned ones); reading schemas at run time from the repository (absent in the image).

## R6 - Provider-neutral model adapter

- **Decision**: Hermes' `ModelProvider` protocol has one method that returns an Agent Framework chat client for a
  `ResolvedModel` (`provider`, `model`, `deployment`, `parameters`); framework and SDK types do not cross it. Exactly
  one provider is configured with `SOCALYTICS_HERMES_MODEL_PROVIDER` (default `openai`):
  - `openai` (the architecture's default provider): the framework's OpenAI
    chat client with `SOCALYTICS_HERMES_MODEL_ENDPOINT` (default `https://api.openai.com/v1`) and the API key from
    `SOCALYTICS_HERMES_MODEL_CREDENTIAL_FILE`;
  - `github-models`: the same OpenAI-compatible client with the endpoint default `https://models.github.ai/inference`
    and a GitHub token from the credential file;
  - `simulated`: the scripted provider (R7), allowed only when `SOCALYTICS_HERMES_ENVIRONMENT` is `Development` or
    `Test`, with `SOCALYTICS_HERMES_MODEL_SCRIPT_FILE` and no credential file.
  The pinned model selection's `provider` must equal the configured one, otherwise the attempt fails
  `model-provider-mismatch` (not retryable) without a provider call. The request's model identifier is the pinned
  `deployment` (the deployment or configuration name the provider serves); `model` is recorded for lineage. The pinned
  `parameters` (`temperature`, `topP`, `maxOutputTokens`, `reasoningEffort`) are sent exactly as pinned and nothing
  else; the resolved parameters declared in the completion are that exact object. Provider errors map to
  `provider-unavailable` (connection failure, timeout of `SOCALYTICS_HERMES_MODEL_TIMEOUT_SECONDS`, `429`, `5xx`;
  retryable), `provider-authentication-failed` (`401`/`403`; not retryable), and `provider-refused` (other `4xx`,
  content refusal; not retryable).
- **Rationale**: FR-009 to FR-012 and the architecture's "configuration selects exactly one provider and deployment;
  no silent fallback"; GitHub Models serves an OpenAI-compatible inference API, so both real providers share one SDK
  path while staying separately selectable and recorded.
- **Alternatives considered**: Choosing the provider from the pin at run time with several configured credentials
  (would let a catalog change switch providers without operator intent); a provider default for omitted parameters
  recorded as resolved values (would claim values Hermes did not send).

## R7 - Scripted provider

- **Decision**: The scripted provider is an Agent Framework chat client implemented in Hermes that serves the catalog's
  `simulated` provider. Its script is a JSON file validated by the component-local schema
  `src/agents/hermes/src/socalytics_hermes/providers/script.schema.json`: a list of scenarios, each selected by the
  agent identifier and an optional question marker (`[script:<name>]` at the start of the question, as the simulated
  workload's markers), with ordered steps: `toolCalls` (tool identifiers and arguments), `answer` (text; the token
  `{{callIds}}` is replaced by the call identifiers of the attempt's earlier tool results), or `error`
  (`unavailable`, `refused`, `authentication`). It needs no network and no secret; the unit tests use it for every model
  interaction, and the cross-process test mounts a script that calls `get_match` and answers with the five sections.
- **Rationale**: FR-012 and the description's "deterministic scripted test provider used by all automated tests".
  Serving the catalog's `simulated` provider keeps the no-fallback rule intact: the seed's pinned provider is
  `simulated`, so only a Hermes configured for `simulated` executes it.
- **Alternatives considered**: A test-only fake outside the package (the image-based cross-process test needs the
  provider inside the image); recorded real-provider responses (need credentials to record and drift silently).

## R8 - Rendering

- **Decision**: The system context is the pinned prompt template with each declared slot replaced (`agent_id`: agent
  identifier; `display_name`: agent version display name; `tool_policy`: the session's tools in allowlist order;
  `evidence_context`: a fixed sentence naming the match, the bound analysis run, and that evidence is available only
  through the listed tools and may be incomplete), followed by the runtime section `hermes-runtime-section/1`: the
  safety policy's hard boundaries, refusal and escalation categories, the instruction to refuse requests outside them
  and outside the authorized match, to cite tool call identifiers and time ranges for every current-match claim and to
  mark unsupported hypotheses, and the five response-contract headings in order. The history from the envelope
  (newest first) is rendered oldest first as alternating user and assistant messages, each assistant message prefixed
  with its agent identifier; the question is the final user message; tool results are tool messages. Templates without
  slots (the seed prompts) are used verbatim with the runtime section appended. The runtime section's version is part
  of the declared `runtime` (`hermes/<package version>+hermes-runtime-section/1`).
- **Rationale**: FR-013 and FR-014; [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance)
  defines the response contract; untrusted text stays out of the system role.
- **Alternatives considered**: Putting the safety policy into a slot (the catalog's slot set has none, and seed prompts
  declare no slots).

## R9 - Evidence through MCP

- **Decision**: Per attempt Hermes opens one MCP client session with the official SDK's Streamable HTTP client to
  `SOCALYTICS_HERMES_MCP_URL` with `Authorization: Bearer <secret from SOCALYTICS_HERMES_MCP_SECRET_FILE>`,
  `SocAlytics-Attempt-Id`, and `SocAlytics-Fencing-Token`, lists the tools, keeps those also in the pinned tool policy,
  and exposes each to the Agent Framework agent as a function tool with the server's input schema. A tool call goes
  through the session; its `structuredContent` (answers, "analysis unavailable", or tool errors with
  `origin: platform` or `origin: mcp-server`) is given to the model as the tool result, except stale dispositions (R4).
  A model request for an unlisted tool is answered locally with a not-permitted tool result and counts as a turn. The
  session is closed (`DELETE`) when the attempt ends. Session open failure (connection, `5xx`, `401`) and tool
  calls that fail with `mcp-platform-unavailable`, `mcp-platform-authentication-failed`, or a lost session end the
  attempt with `mcp-unavailable` (retryable).
- **Rationale**: FR-015 and FR-022; owning the session (rather than the framework's built-in MCP tool) lets Hermes see
  every result before the model does, detect stale dispositions, and bind the session headers exactly as the
  [MCP server contract](../20261018-111804-mcp-server/contracts/mcp-server.md) requires.
- **Alternatives considered**: The framework's MCP tool type (hides tool errors from Hermes and offers no hook for the
  binding headers per attempt in all versions); calling the platform's tool operations directly (bypasses the
  architecture's MCP boundary).

## R10 - Cross-process evidence

- **Decision**: A new Docker-backed test class `AgentRuntimeStackTests` in
  `src/platform/Tests/SocAlytics.Platform.Images.Tests` (the image evidence project of
  [Cloud-Neutral OCI Images](../archive/20261018-092503-oci-images/research.md#r8-evidence-design)), next to the MCP server's
  `McpServerImageTests` and a new `HermesImageTests`. Its image fixture builds the API, Migrator, MCP server, and Hermes
  images with their documented commands. On one Testcontainers network it starts PostgreSQL with the roles script,
  NATS with JWT trust, and RustFS, runs the Migrator image, and runs in process (Test environment) the existing
  configured-workflow and agent catalog seeds and the integration tests' club, member, and analysed-match builders
  against the same database, as the image tests already seed workflows. A test certificate authority and an `api`
  server certificate are generated per run; the API image runs unchanged in its production environment with Kestrel
  HTTPS on 8443 (`ASPNETCORE_URLS`, `ASPNETCORE_Kestrel__Certificates__Default__Path` and `__KeyPath` mounted read-only)
  besides HTTP 8080 for probes, `Stamp__PublicBaseUri=https://api:8443`, NATS JWT authentication, and two workload
  clients (`hermes`, `mcp-server`) with test public keys. The MCP server and Hermes containers trust the test CA
  through their CA-file settings; Hermes runs with `SOCALYTICS_HERMES_ENVIRONMENT=Test`, the `simulated` provider, and
  a mounted script that calls `get_match` and answers. The test signs in as the owning Coach over HTTPS, creates a
  conversation for the analysed match's completed run, adds `specialist-tactics`, submits a question, polls the
  invocation until `succeeded` (60-second bound, SC-001), and asserts the turns, the lineage (one `tool-call` entry for
  `get_match` with outcome `answered`, Hermes' checkpoints, the completed entry), and the execution references (pins,
  `simulated` model selection, empty resolved parameters, `runtime` starting with `hermes/`), and that the Hermes and
  MCP server container logs contain neither the question nor the advice.
- **Rationale**: FR-030 and the MCP server's deferred evidence ([research R10 there](../20261018-111804-mcp-server/research.md#r10---tests-and-the-cross-process-question)):
  the in-memory test server has no listener and workload operations require HTTPS. Running the API image with Kestrel
  HTTPS through standard ASP.NET Core configuration needs no platform code change and no new tool: Docker, .NET, and
  Testcontainers are already in the platform verification, and the Python images are built by Docker.
- **Alternatives considered**: A TLS-terminating proxy container with forwarded headers (one more image to pin and the
  same CA trust problem); the in-process `WebApplicationFactory` with a Kestrel listener reached from containers
  through a host gateway (host networking differs per Docker platform); starting Python processes from the platform
  tests (makes the platform tests depend on the Python toolchain); the Development environment in the API container
  (the image tests run images unchanged in production configuration).

## R11 - Platform change: completion contract 1.1.0

- **Decision**: `contracts/agent-orchestration/attempt-report/v1` gains release 1.1.0: the completion's
  `executionReferences` adds the optional `modelSelectionRef` (pinned digest) and `parameters` (closed object with the
  model selection's typed bounds); 1.0.0 stays unchanged and valid. `CompleteAgentAttempt` additionally refuses with
  `409 execution-references-mismatch` (attempt stays active) when `modelProvider`, `model`, or `deployment` differ from
  the pinned model selection, or when `modelSelectionRef` or `parameters` are present and differ from the pinned
  digest or parameters. The lineage `completed` payload and `agent_accepted_advice.execution_references` (both `jsonb`)
  store the new fields; the owner's invocation and lineage reads return them. No migration is needed. Hermes always
  sends both fields; the simulated agent workload is unchanged.
- **Rationale**: FR-019a; minor-version additive input fields keep existing producers valid, and the equality check
  makes the recorded model, deployment, and parameters trustworthy rather than declared.
- **Alternatives considered**: Recording parameters in a checkpoint note (unstructured, content-like, unchecked);
  `attempt-report/v2` (needless break).

## R12 - Health, configuration, logging, shutdown, and image

- **Decision**: `GET /alive` (`200` while serving) and `GET /health` (`200` while a workload token is held or obtained
  and the NATS subscription is connected; `503` otherwise) on `SOCALYTICS_HERMES_PORT` (8080). Configuration and limits
  are the table in [data-model.md](data-model.md#configuration); invalid values stop the process with exit code 2 and
  one line per invalid variable. Logs are single-line JSON with the permitted keys of the
  [runtime contract](contracts/hermes-runtime.md#5-logs), enforced by a key allow-list; the OpenAI, httpx, MCP, and
  NATS libraries' loggers are raised to `WARNING` and filtered to the same keys. On `SIGTERM` Hermes stops fetching,
  cancels model and tool work, fails each active attempt with `runtime-stopping` within
  `SOCALYTICS_HERMES_SHUTDOWN_SECONDS` (development 20), closes MCP sessions and the NATS connection, and exits `0`. The
  Dockerfile mirrors the MCP server's ([research R9 there](../20261018-111804-mcp-server/research.md#r9---container-image)):
  two stages on `python:3.12-slim-bookworm` pinned by digest, uv 0.13.1 from a hash-pinned requirement file, the locked
  environment copied into the final stage, labels with title `socalytics-hermes`, `USER 65532:65532`, `EXPOSE 8080`, no
  `HEALTHCHECK`, entry point `python -m socalytics_hermes`. A project test checks the Dockerfile rules; `HermesImageTests`
  checks build, read-only start, `/alive`, `/health` not ready without a platform, UID, labels, and a layer scan.
- **Rationale**: FR-023 to FR-027; clarification on observability and shutdown.

## R13 - Environment

- **Decision**: No tool beyond the environment: CPython 3.12.15 and uv 0.13.1 from the merged Analyst and Media
  Verification Environment; verification of `src/agents/<name>/` from
  [Agents Python Verification Environment](../20261018-110526-agents-python-environment/spec.md) (must merge first);
  .NET 10, Docker, and Testcontainers for the platform, contract, and image tests from the platform verification. The
  Web UI's generated client is regenerated from the refreshed OpenAPI snapshot by its existing build.
- **Rationale**: Constitution, Technology and Tooling Constraints.
