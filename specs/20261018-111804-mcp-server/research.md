# Research: MCP Server

## R1 - Project location, name, and toolchain

- **Decision**: One independent uv project at `src/agents/mcp-server/` with the distribution name
  `socalytics-mcp-server`, the import package `socalytics_mcp_server` (src layout), `requires-python = ">=3.12,<3.13"`,
  the `hatchling` build backend and `pytest` in a locked `dev` dependency group, and a committed `uv.lock`, exactly as the
  agent project convention of [Agents Python Verification Environment](../20261018-110526-agents-python-environment/contracts/environment-interface.md)
  requires. It has no local path dependency. Runtime dependencies: `mcp==2.3.0` (official MCP Python SDK; it brings
  Starlette, uvicorn, the `httpx2` HTTP client, `jsonschema`, `pydantic`, and `pyjwt[crypto]`), declared directly as
  `uvicorn`, `starlette`, `httpx2`, `jsonschema`, and `pyjwt[crypto]` with lower bounds compatible with the lock, so the
  server does not rely on transitive declarations.
- **Rationale**: [Source and Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline)
  assigns intelligence-agent runtimes and integration code to `src/agents`; the environment spec verifies projects
  directly under `src/agents/<name>/` with the provisioned CPython 3.12.15 and uv 0.13.1. `mcp` 2.3.0 requires
  Python >= 3.10 (its documentation names 3.11+), so CPython 3.12 satisfies it. Every library the server needs (HTTP
  server, HTTP client, JSON Schema validation, ES256 JWS) is already a dependency of the SDK, so no additional
  ecosystem is introduced.
- **Alternatives considered**: A shared uv workspace for all agent projects (rejected: the environment convention
  forbids workspaces and requires independent projects); `python-jose` or `joserfc` for JWS (rejected: PyJWT with
  `cryptography` is already locked through the SDK); `requests` or `aiohttp` (rejected: a second HTTP client).

## R2 - MCP transport and endpoint

- **Decision**: Streamable HTTP, the MCP transport for servers in a separate process or container, at the single
  endpoint `POST/GET/DELETE /mcp` served by uvicorn on port 8080, with stateful sessions (`Mcp-Session-Id` issued at
  `initialize`) and JSON responses (no server-initiated streams, because the server sends no notifications or
  requests). The server uses the SDK's low-level server API with explicit tool definitions (name, description,
  `inputSchema`, `outputSchema`) and handlers for `tools/list` and `tools/call`; capabilities advertise `tools` only
  (`listChanged: false`), and no resources, prompts, sampling, elicitation, or logging capability.
- **Rationale**: [Intelligence and Agents → MCP Layer](../../docs/architecture/intelligence-and-agents.md#mcp-layer)
  places MCP between agents and the API as its own component, and the overview draws an MCP Server next to the agent
  runtime; stdio would require the runtime to spawn the server inside its own container. Stateful sessions carry the
  attempt binding (R4). The low-level API accepts JSON Schema documents directly, which the generated tool catalog
  (R3) provides; the decorator API would derive schemas from Python type hints and could drift from the platform.
- **Alternatives considered**: stdio inside the runtime container (rejected: not a separate deployable and couples
  the server's credentials to the runtime); legacy HTTP+SSE transport (rejected: superseded by Streamable HTTP);
  stateless Streamable HTTP with the binding on every request (rejected: a per-request binding lets one connection
  switch attempts, which FR-005 forbids).

## R3 - Tool catalog derived from the allowlist contract and the API description

- **Decision**: A committed, generated tool catalog `src/agents/mcp-server/src/socalytics_mcp_server/tool_catalog.json`
  (package data) holds, for every entry of `contracts/agent-tools/tool-allowlist/v1/examples/valid/allowlist-1.0.0.json`
  in allowlist order: `toolId`, `operationId`, `method`, `pathTemplate`, `answers`, `producingCapability`,
  `inputSchema` (the OpenAPI component named by `argumentsSchema`, with every `$ref` resolved from
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json` into a self-contained JSON Schema 2020-12 document), and
    `outputSchema` (the operation's `200` response body `{ callId, toolId, outcome, result }`, resolved the same way,
    whose `result` is the component named by `resultSchema`), plus the allowlist and schema
  versions and the SHA-256 digests of both source files. The project script `tools/generate_tool_catalog.py`
  (standard library plus `jsonschema`, run with `uv run python tools/generate_tool_catalog.py`) writes it. A
  conformance test regenerates the catalog in memory from the repository files and fails when it differs from the
  committed file; it also validates the allowlist fixture against the allowlist schema of the contract, checks that
  every `operationId` exists in the API description with the allowlist's method and path, and checks that the request
  body of each operation is `{ callId, fencingToken, arguments }` with `arguments` equal to `argumentsSchema`.
- **Rationale**: FR-002, FR-003: the server's tools are mechanically equal to the contract and the platform's
  published description, and a change to either fails the server's tests (agent projects are verified on every
  `contracts/agent-*` change; the API description is caught by finalize verification and by the drift test whenever
  the server changes). The image contains no repository files, so the catalog must be package data; the digests make
  a stale catalog visible.
- **Alternatives considered**: Reading the contract and API description at run time (rejected: not present in the
  image and would make a deployment depend on repository layout); hand-written tool definitions (rejected: drift);
  generating a Python client with Kiota (rejected: the architecture's Python Kiota client remains planned, and a
  forwarding server needs only the tool operations' raw JSON, which must pass through unchanged).

## R4 - Session authentication and attempt binding

- **Decision**: Every request to `/mcp` must carry `Authorization: Bearer <runtime secret>`; the secret is read once
  at start from the file named by `SOCALYTICS_MCP_RUNTIME_SECRET_FILE` (at least 32 bytes), compared with
  `hmac.compare_digest`, and a mismatch answers `401` with no session. The `initialize` request must carry
  `SocAlytics-Attempt-Id` (UUID) and `SocAlytics-Fencing-Token` headers; before the session is created the server
  calls the platform's `readPinnedAgentArtifacts` (`POST /api/v1/agent-orchestration/attempts/{attemptId}/pinned-artifacts`
  with `{ fencingToken }`) with its own workload token. A `200` creates the session with the attempt identifier, the
  fencing token, and the tool policy taken from the `tool-policy` artifact's `tools`, intersected with the catalog;
  `404` and `409 agent-attempt-stale` (with its disposition) are returned to the client as an HTTP error with the
  platform's problem body and no session. Later requests of the session must carry no binding headers or exactly the
  same values; different values are refused with `400` and leave the session unchanged. Sessions end on `DELETE`,
  after the configured idle time, or at server stop; a session is never rebound.
- **Rationale**: Clarifications 1 and 2; FR-004 to FR-006. The pinned-artifact read is the only platform operation
  that confirms a current attempt and returns its pinned tool policy without accepting a caller-chosen digest, so the
  policy comes from durable authority rather than from the runtime. Headers keep the fencing token out of JSON-RPC
  bodies. Delegated consultation attempts (later feature) bind the same way.
- **Alternatives considered**: The MCP authorization flow with OAuth 2.1 (rejected: requires an authorization server
  the stamp does not run, for one stamp-local caller); mutual TLS (rejected for now: the stamp's internal transport
  security is an open profile value); binding through `initialize` parameters (`_meta`) (rejected: puts the fencing
  token into protocol bodies that clients commonly log).

## R5 - Platform credentials, token, and proofs

- **Decision**: The server is its own agent workload client of
  [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/contracts/agent-orchestration-api.md):
  `SOCALYTICS_MCP_WORKLOAD_CLIENT_ID` and an ECDSA P-256 private key in PKCS#8 PEM at
  `SOCALYTICS_MCP_WORKLOAD_KEY_FILE`; the stamp configures the matching public JWK in
  `AgentOrchestration:Workloads:Clients` next to the runtime's own client. It obtains tokens from
  `issueAgentWorkloadToken` with `grant_type=client_credentials`, `scope=agent-workload`, a `private_key_jwt` client
  assertion (ES256; `iss` and `sub` the client id; `aud` the public token endpoint URL; `jti`; `iat`; `exp` 60 s), and a
  DPoP proof; every later request carries `Authorization: DPoP <token>` and a fresh proof (`typ` `dpop+jwt`, ES256,
  public `jwk` header, `jti`, `iat`, `htm`, `htu` = the public base URL plus the path without query, `ath` =
  base64url SHA-256 of the token), the same rules the Analyst Manager follows
  (`src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/ProofFactory.cs`). Proof URLs and the assertion
  audience use `SOCALYTICS_MCP_PLATFORM_PUBLIC_BASE_URL` (default: `SOCALYTICS_MCP_PLATFORM_URL`), because the platform
  validates them against `Stamp:PublicBaseUri` while the server may connect through an internal address. The token is
  cached in memory only, renewed when less than 30 seconds of its lifetime remain, and renewed once when the platform
  answers `401` with a DPoP challenge; a second `401` fails the call with `mcp-platform-authentication-failed`.
- **Rationale**: Clarification 3; FR-009. A separate client keeps keys per container, attributes tool calls to the MCP
  server in the platform's audit, and lets the stamp disable the server alone.
- **Alternatives considered**: Sharing the runtime's key (rejected: one key in two containers); delegated proofs
  created by the runtime per call (rejected: the runtime would need the platform's URLs and would hand its bearer
  token to another process).

## R6 - Forwarding and faithful results

- **Decision**: A `tools/call` for a tool in the session's policy validates `arguments` against the tool's
  `inputSchema` with `jsonschema` (Draft 2020-12; the platform's schemas forbid additional properties, so team, match,
  conversation, run, and attempt references are refused), generates `callId` = `mcp-` plus a UUID4, and sends exactly
  one `POST {platformUrl}{pathTemplate with attemptId}` with `{ callId, fencingToken, arguments }`. A `200` becomes an
  MCP result with `structuredContent` equal to the platform's JSON body (`{ callId, toolId, outcome, result }`) and one
  text content item with the same JSON; nothing is added or removed. Any other platform status becomes a tool error
  (`isError: true`) whose `structuredContent` is `{ origin: "platform", status, callId, problem }` with the platform's
  problem details object unchanged. Local refusals use `{ origin: "mcp-server", status, callId?, problem }` with problem
  type `urn:socalytics:problem:<code>` and codes `agent-tool-not-permitted` (403; tool in the allowlist but not in the
  session's policy), `validation` (400; field violations from schema validation), `mcp-platform-unavailable` (503;
  connection failure or no answer within `SOCALYTICS_MCP_PLATFORM_TIMEOUT_SECONDS`), and
  `mcp-platform-authentication-failed` (502). A tool name outside the catalog is a JSON-RPC invalid-params error. No
  call is retried.
- **Rationale**: FR-007, FR-008, FR-011, FR-012; clarification 4. Passing the platform's body through unchanged is the
  only way to be faithful; `origin` lets the agent runtime tell a platform answer from a local refusal. Not retrying
  avoids double counting against the attempt's tool call limits, because the platform refuses reused call identifiers.
- **Alternatives considered**: Mapping platform problems to JSON-RPC errors (rejected: hides the answer from the
  model, which MCP intends tool errors for); retrying idempotently with the same call identifier (rejected: the
  platform answers a reused identifier with a conflict, not with the earlier result).

## R7 - Logging, no caching, and secrets

- **Decision**: Logs are single-line JSON records on standard output with `event` (`session.opened`,
  `session.refused`, `session.closed`, `tool.call`), `attemptId`, `sessionId` (a hash of the MCP session identifier),
  `callId`, `toolId`, `origin`, `outcome`, `status`, and `durationMs`; a logging filter and the record schema permit only
  these keys. The HTTP client's and uvicorn's request logs are disabled or reduced to method, path template, and
  status, so no URL with an attempt identifier and no header is logged. Results, arguments, problem details, tokens,
  proofs, keys, and the runtime secret are never logged, cached, or written; the server writes no files. A test drives
  every outcome with a sentinel link, payload, fencing token, access token, and secret and asserts that none of them
  appears in captured logs.
- **Rationale**: Clarification 5; FR-013, FR-014; spec 6 FR-013 (links never stored or logged).
- **Alternatives considered**: OpenTelemetry export (deferred to the stamp composition, clarification 5).

## R8 - Health, readiness, configuration, and limits

- **Decision**: `GET /alive` answers `200` while the process serves requests. `GET /health` answers `200` when the
  server holds a workload token with more than 30 seconds of lifetime or obtains one within the platform timeout, and
  `503` otherwise; token acquisition from probes is rate-limited to once per 5 seconds. Neither endpoint requires the
  runtime secret or returns details beyond `{ status }`. Configuration comes only from environment variables (prefix
  `SOCALYTICS_MCP_`) and the files they name (data model); invalid values stop the process at start with exit code 2
  and one line per invalid setting naming the variable, never its value. Limits: `MAX_SESSIONS` (development 32;
  further `initialize` requests answer `503`), `SESSION_IDLE_TIMEOUT_SECONDS` (development 300),
  `PLATFORM_TIMEOUT_SECONDS` (development 30). Production values remain open.
- **Rationale**: FR-015 to FR-017; follows the API's `/alive` and `/health` convention and the image contract's
  "configuration only from environment variables and mounted files".
- **Alternatives considered**: Readiness on platform `/health` (rejected: proves reachability but not that the server's
  credentials work).

## R9 - Container image

- **Decision**: `src/agents/mcp-server/Dockerfile` with `Dockerfile.dockerignore`, built from the repository root
  (`docker build -f src/agents/mcp-server/Dockerfile --build-arg SOCALYTICS_REVISION=<commit> --build-arg SOCALYTICS_VERSION=<YYYY.MMDD.HHMMSS> -t socalytics-mcp-server:local .`).
  Two stages on `python:3.12-slim-bookworm@sha256:<digest current at implementation time>` (the base the reference
  Analyst uses). The builder stage fails when either build argument is missing or malformed, installs uv at the
    version of `.github/actions/environment-setup/analyst-media-toolchain.json` (0.13.1) from the hash-pinned
    requirement file `docker/uv-requirements.txt` (`pip install --require-hashes --only-binary :all:`, one `--hash` per
    published Linux wheel, so the definition names no CPU architecture), and installs the project with
    `uv sync --locked --all-groups --no-install-local --no-build` followed by
    `uv sync --locked --no-dev --no-editable --no-build-isolation --compile-bytecode` into `/opt/socalytics-mcp-server/venv`
    (`UV_PYTHON_DOWNLOADS=never`). The final stage copies only that environment, sets the OCI labels of the image
    contract (`org.opencontainers.image.title` `socalytics-mcp-server`, revision, version, source, description),
    `PYTHONDONTWRITEBYTECODE=1`, `PYTHONUNBUFFERED=1`, `USER 65532:65532`, `EXPOSE 8080`, no `HEALTHCHECK`, and
    `ENTRYPOINT ["/opt/socalytics-mcp-server/venv/bin/python", "-m", "socalytics_mcp_server"]`. A project test
    (`tests/test_dockerfile.py`) checks the base image digest pin, the uv version against the toolchain JSON and the
    presence of hashes, the labels, the user, the port, the absence of `HEALTHCHECK`, `ENV` secrets, and `COPY` of tests
    or caches, and that the ignore file admits only the project's sources, lockfile, and `docker/` files. Runtime evidence (build, read-only start with `/tmp` tmpfs, `/alive`, `/health`
    not ready without a platform, UID, labels, and a layer scan for secrets) is added to
    `src/platform/Tests/SocAlytics.Platform.Images.Tests` of [Cloud-Neutral OCI Images](../archive/20261018-092503-oci-images/plan.md)
    as `McpServerImageTests.cs`.
  - **Rationale**: FR-018 and the [image contract](../archive/20261018-092503-oci-images/contracts/container-images.md) (base
    pinned by digest, every downloaded tool verified, no CPU architecture in the definition); reuses the uv version the
    environment verifies. The agent project's default test run stays free of Docker and network services; the
    Docker-backed evidence lives where the repository already builds images.
  - **Alternatives considered**: The environment's x86_64 uv archive as the reference Analyst does (rejected: names a CPU
    architecture, which the image contract forbids for platform images); distroless Python (rejected: the official
    CPython base matches the verified interpreter line); `pip` without uv (rejected: a second resolver path instead of
    `uv.lock`).

## R10 - Tests and the cross-process question

- **Decision**: pytest tests in the project, all in process and without network services: a fake platform (a
  Starlette app reached through the `httpx2` ASGI transport) that verifies client assertions and DPoP proofs with the
  configured public key, records requests, and answers with fixtures validated against the API description's response
  schemas; MCP sessions driven through the SDK's Streamable HTTP client over an ASGI transport to the server app; the
  conformance test of R3; log, configuration, health, and Dockerfile tests. The SDK's anyio pytest plugin runs async
  tests, so no extra test dependency is needed. A cross-process test against the real platform API with the simulated
  agent workload is **not feasible in the existing test infrastructure**: the platform's integration and end-to-end
  tests host the API in an in-memory test server without a network listener, the simulated agent workload runs in
  process, the image tests run the API container over plain HTTP while workload operations require HTTPS, and an agent
  project's default test run must not depend on network services. It is deferred to the agent runtime feature, whose
  end-to-end path needs a real platform, the runtime, and this server together; the image tests prove that the image
  starts, refuses readiness without a platform, and holds no secrets.
- **Rationale**: FR-020 and the environment spec's FR-011; the fake platform's fixtures are validated against the same
  API description the catalog comes from, so the fake cannot answer in shapes the platform does not publish.
- **Alternatives considered**: A platform test that starts the Python server as a child process (rejected: makes the
  platform test suite depend on the Python toolchain); a Kestrel listener with a development certificate in the
  integration tests (rejected: changes shared test infrastructure for one consumer and still needs the runtime's
  claim flow, which belongs to the agent runtime feature).

## R11 - Environment

- **Decision**: No tool beyond the environment: the project uses the CPython 3.12.15 and uv 0.13.1 of the merged
  Analyst and Media Verification Environment, recognized and verified for `src/agents/<name>/` by
  [Agents Python Verification Environment](../20261018-110526-agents-python-environment/spec.md) (must merge first);
  the image test uses .NET 10 and Docker, which the platform verification already provides. External dependencies
  ship wheels for CPython 3.12 on Linux.
- **Rationale**: Constitution, Technology and Tooling Constraints.
