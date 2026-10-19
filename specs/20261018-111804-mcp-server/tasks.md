# Tasks: MCP Server

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md),
[MCP server contract](contracts/mcp-server.md), and [quickstart.md](quickstart.md).

**Tests**: Required by FR-020 and the validation guide. Each implementation task includes its directly related tests in
`src/agents/mcp-server/tests/` (or, for the image, `src/platform/Tests/SocAlytics.Platform.Images.Tests/`), so a task
never lands deliberately failing tests. Project tests run in process without network services, Docker, model
providers, or secrets.

**Organization**: Setup and foundational modules first, then one phase per user story in priority order, then
documentation and final acceptance.

## Format And Boundaries

- Tasks use `- [ ] Tnnn [P?] [USn?] Description`.
- Consecutive unchecked `[P]` tasks under one heading run in parallel, each from the same commit; their predicted file
  changes must be disjoint and neither may depend on the other.
- Production code lives only in `src/agents/mcp-server/`; the only other source change is the image test class in
  `src/platform/Tests/SocAlytics.Platform.Images.Tests/`. Add no platform operation, contract, tool, MCP resource or
  prompt, or write path; never change `contracts/` or the platform's OpenAPI snapshot.
- Run project checks from `src/agents/mcp-server/` with the environment's locked sequence:
  `uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`,
  `uv build --wheel --no-build-isolation`, `uv run --no-sync pytest`. Change dependencies only by editing
  `pyproject.toml` and regenerating `uv.lock` with `uv lock`.

## Phase 1: Setup

**Purpose**: Create the independent agent project so verification recognizes it.

- [ ] T001 Create the uv project `src/agents/mcp-server/` per research R1: `pyproject.toml` with
  `[project].name = "socalytics-mcp-server"`, `requires-python = ">=3.12,<3.13"`, runtime dependencies `mcp==2.3.0`
  and direct declarations of `uvicorn`, `starlette`, `httpx2`, `jsonschema`, and `pyjwt[crypto]` with lower bounds the
  lock satisfies,   `[build-system]` on `hatchling` with the package `src/socalytics_mcp_server` (package files such as the later
    `tool_catalog.json` are included in the wheel), a `dev` dependency group with `hatchling` and `pytest`, and pytest configured for `tests/` with
  the anyio plugin; the committed `uv.lock` generated with `uv lock`; `src/socalytics_mcp_server/__init__.py`; and
  `tests/test_package.py` asserting the package imports and its version. Add `src/agents/mcp-server/.venv/` and
  `src/agents/mcp-server/dist/` to the repository `.gitignore` only if no existing entry covers them. The locked
  sequence passes.

**Checkpoint**: The project is a valid agent project with one passing test.

## Phase 2: Foundational

**Purpose**: The tool catalog, configuration, logging, and platform credentials every story needs.

### Independent foundation modules

- [ ] T002 [P] Add the tool catalog per research R3 and data-model "Tool catalog entry": the generator
  `src/agents/mcp-server/tools/generate_tool_catalog.py` (reads
  `contracts/agent-tools/tool-allowlist/v1/examples/valid/allowlist-1.0.0.json` and
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json`, resolves every `$ref` into self-contained JSON Schema 2020-12
  documents, writes entries in allowlist order with `toolId`, `operationId`, `method`, `pathTemplate`, `answers`,
  `producingCapability`, `inputSchema`, `outputSchema`, and the header `allowlistVersion`, `allowlistSchemaVersion`,
  `allowlistDigest`, `openApiDigest` as `sha-256:` of the source files); the generated
  `src/agents/mcp-server/src/socalytics_mcp_server/tool_catalog.json`; the loader
  `src/agents/mcp-server/src/socalytics_mcp_server/catalog.py` (catalog lookup by `toolId`, Draft 2020-12 validators per
  tool, tool descriptions stating the answer kind and producing capability); and
  `src/agents/mcp-server/tests/test_catalog_conformance.py`: the committed catalog equals an in-memory regeneration
  from the repository files; it has exactly the eleven allowlist entries with `toolId` matching
  `^[a-z][a-z0-9_]{0,63}$`; the allowlist fixture validates against the contract's schema; every `operationId` exists in
  the API description with method `POST` and the allowlist's path; every operation's request body is
  `{ callId, fencingToken, arguments }` with `arguments` equal to the entry's `argumentsSchema`; and the output schema is
    the operation's `200` response body `{ callId, toolId, outcome, result }` with `result` equal to the entry's
    `resultSchema`.
- [ ] T003 [P] Add configuration and logging per data-model "Configuration" and research R7 in
  `src/agents/mcp-server/src/socalytics_mcp_server/configuration.py` and
  `src/agents/mcp-server/src/socalytics_mcp_server/logging.py`, with
  `src/agents/mcp-server/tests/test_configuration.py` and `src/agents/mcp-server/tests/test_logging.py`. Configuration
  reads only `SOCALYTICS_MCP_*` variables and the files they name: `PLATFORM_URL` (required, "Absolute `http` or `https`
  URL without query"), `PLATFORM_PUBLIC_BASE_URL` (default `PLATFORM_URL`, absolute origin), `WORKLOAD_CLIENT_ID`
  (required), `WORKLOAD_KEY_FILE` (required, "ECDSA P-256 private key, PKCS#8 PEM"), `RUNTIME_SECRET_FILE` (required,
  "At least 32 bytes after trimming a final newline"), `PORT` (default 8080, "1024–65535"), `MAX_SESSIONS` (default 32,
  "1–1024"), `SESSION_IDLE_TIMEOUT_SECONDS` (default 300, "10–3600"), `PLATFORM_TIMEOUT_SECONDS` (default 30, "1–120"),
  `LOG_LEVEL` (default `info`; `debug`, `info`, `warning`, `error`); it reports every invalid variable by name, never its
  value. Logging writes single-line JSON to standard output and drops every key except `event`, `timestamp`, `level`,
  `attemptId`, `sessionId`, `callId`, `toolId`, `origin`, `outcome`, `status`, `durationMs`, and `setting`; `sessionId` is
  logged only as a SHA-256 prefix. Tests cover each variable's default, bound, and error message without secret values,
  and that unknown keys and values passed to the logger never reach the output.
- [ ] T004 [P] Add the platform credentials per research R5 in
  `src/agents/mcp-server/src/socalytics_mcp_server/platform/__init__.py`,
  `src/agents/mcp-server/src/socalytics_mcp_server/platform/proofs.py`, and
  `src/agents/mcp-server/src/socalytics_mcp_server/platform/tokens.py`, with the shared test support
  `src/agents/mcp-server/tests/conftest.py` (generated P-256 test keys, temporary key and secret files, configuration
  fixture) and `src/agents/mcp-server/tests/fake_platform.py` (an ASGI fake of `issueAgentWorkloadToken`,
  `readPinnedAgentArtifacts`, and the eleven `agentTool*` operations that verifies client assertions and DPoP proofs
    against the configured public key, records every request, and answers from per-test configurable fixtures (status and
    body per operation, including every problem the agent tools contract lists) validated against the response
    schemas of `src/platform/SocAlytics.Platform.Api/openapi/v1.json`), and
  `src/agents/mcp-server/tests/test_platform_credentials.py`. The client assertion is ES256 with `iss` and `sub` the
  client id, `aud` the public token endpoint URL, `jti`, `iat`, and `exp` 60 s; DPoP proofs carry `typ` `dpop+jwt`, ES256,
  the public `jwk` header, `jti`, `iat`, `htm`, `htu` built from `PLATFORM_PUBLIC_BASE_URL` plus the path without query,
  and `ath` for token-bound requests, as `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/ProofFactory.cs`
  does; the token is held in memory only and renewed when less than 30 seconds remain. Tests verify each claim with an
  independent decoder, unique `jti` values, `htu` from the public base URL when it differs from `PLATFORM_URL`, and
  renewal timing.

### Platform client

- [ ] T005 Add the outbound client per research R5 and R6 and contract §3 in
  `src/agents/mcp-server/src/socalytics_mcp_server/platform/client.py` with
  `src/agents/mcp-server/tests/test_platform_client.py`: it sends requests only to `issueAgentWorkloadToken`,
  `readPinnedAgentArtifacts`, and the `pathTemplate`s of the catalog and refuses any other path; every request is
  authenticated with the server's DPoP-bound workload token and never carries member cookies or the runtime secret; a
  tool request is exactly one `POST` with `{ callId, fencingToken, arguments }`, never retried; a `401` DPoP challenge
  renews the token once and repeats the request once, and a second `401` yields `mcp-platform-authentication-failed`;
  no answer within `PLATFORM_TIMEOUT_SECONDS` or a connection failure yields `mcp-platform-unavailable`; responses are
  returned as status plus the unchanged JSON body. Tests run against `tests/fake_platform.py`.

**Checkpoint**: The server can derive its tools and talk to the platform with its own credentials.

## Phase 3: User Story 1 - The agent runtime reads match evidence through MCP tools (Priority: P1) 🎯 MVP

**Goal**: An authenticated, attempt-bound session lists the permitted tools and forwards each call faithfully.

**Independent Test**: Against the fake platform, open a session for an attempt, list the tools, call `get_match`
without and with a fact selection, `get_video_clip`, and `get_formations`, and confirm one matching platform request
per call with the session's attempt and fencing token and MCP results equal to the platform's answers.

- [ ] T006 [US1] Add sessions per research R4 and data-model "Session" in
  `src/agents/mcp-server/src/socalytics_mcp_server/sessions.py` with `src/agents/mcp-server/tests/test_sessions.py`:
  every request must carry `Authorization: Bearer <runtime secret>` compared with `hmac.compare_digest`; `initialize`
  must carry `SocAlytics-Attempt-Id` (UUID) and `SocAlytics-Fencing-Token`; before the session exists the server calls
  `readPinnedAgentArtifacts` and on `200` stores the attempt identifier, fencing token, and tool policy = the
  `tool-policy` artifact's `tools` ∩ catalog, all immutable for the session. Tests: a confirmed session holds exactly
  the pinned policy's tools; the policy is never taken from request data; the confirmation request carries the
  attempt's fencing token and the server's workload credentials.
- [ ] T007 [US1] Add the MCP surface per research R2 and R6 and contract §1 in
  `src/agents/mcp-server/src/socalytics_mcp_server/tools.py` and `src/agents/mcp-server/src/socalytics_mcp_server/app.py`
  with `src/agents/mcp-server/tests/test_tools_forwarding.py` and the shared test helper
  `src/agents/mcp-server/tests/mcp_session.py` (opens an authenticated, bound session with the SDK's Streamable HTTP
  client over an ASGI transport): the ASGI app serves MCP Streamable HTTP at `/mcp` with
  stateful sessions and JSON responses through the SDK's low-level server, advertising only `tools`
  (`listChanged: false`), and routes every `/mcp` request through `sessions.py` for authentication, binding, limits,
  and session end; `tools/list` returns the session's tools in allowlist order with the catalog's name,
  description, `inputSchema`, and `outputSchema`; `tools/call` validates arguments, generates `callId` `mcp-` + UUID4
  (matching `^[A-Za-z0-9._\-]{1,128}$`), forwards one request through `platform/client.py`, and on `200` returns
  `structuredContent` equal to the platform body and one text item with the same JSON, and on any other status a tool
  error `{ origin: "platform", status, callId, problem }` with the problem unchanged. Tests drive the SDK's Streamable
  HTTP client over an ASGI transport: each of the eleven tools reaches its operation once with the session's attempt
  and fencing token; `get_match` with and without `facts`, `get_video_clip` with a link, and `get_formations`
  ("analysis unavailable" with reason and producing capability) are returned unchanged; the link appears only in the
  result; two concurrent calls in one session each reach the platform once with distinct call identifiers and receive
  their own results; resources, prompts, and sampling are not offered, and `resources/list` and `prompts/list` requests
  are answered with JSON-RPC method-not-found.

**Checkpoint**: The MVP: an agent runtime can read evidence through MCP.

## Phase 4: User Story 2 - Sessions are attempt-bound, least-privilege, and fail closed (Priority: P2)

**Goal**: Every unauthenticated, unbound, out-of-policy, scope-widening, or refused call is refused or returned
faithfully, with no platform request for local refusals.

**Independent Test**: Refused sessions, local tool refusals with zero platform requests, and every platform problem
and stale disposition returned unchanged.

### Parallel refusal paths

These tasks use `tests/conftest.py`, `tests/fake_platform.py`, and `tests/mcp_session.py` without changing them, and
each changes only the files it names.

- [ ] T008 [P] [US2] Complete the session refusals in `src/agents/mcp-server/src/socalytics_mcp_server/sessions.py`
  with `src/agents/mcp-server/tests/test_session_refusals.py`: no or a wrong runtime secret → `401` and no session;
  missing or malformed binding headers → `400`; the pinned-artifact read answering `404` or `409 agent-attempt-stale`
  (each disposition `attempt-not-current`, `lease-expired`, `attempt-fenced`, `invocation-ended`, `scope-lost`) → the
  same status with the platform's problem body and no session; later requests with different binding headers → `400`
  with the session unchanged; `initialize` beyond `MAX_SESSIONS` → `503`; sessions end on `DELETE`, after
  `SESSION_IDLE_TIMEOUT_SECONDS`, and at server stop, and are never rebound.
- [ ] T009 [P] [US2] Complete the tool refusals in `src/agents/mcp-server/src/socalytics_mcp_server/tools.py` with
  `src/agents/mcp-server/tests/test_tool_refusals.py`: a tool in the catalog but not in the session's policy → tool
  error `{ origin: "mcp-server", status: 403, problem: { type: "urn:socalytics:problem:agent-tool-not-permitted" } }`;
  arguments invalid against `inputSchema`, including extra `teamId`, `matchId`, `conversationId`, `runId`, or
  `attemptId` properties → tool error with `urn:socalytics:problem:validation` and `{ pointer, detail }` entries; a
  name outside the catalog → JSON-RPC `-32602`; each with zero platform requests. Platform answers `400`, `403
  agent-tool-not-permitted`, `404`, `409 agent-attempt-stale` with every disposition, `409 agent-tool-call-id-reused`,
  `429 agent-tool-call-limit-reached`, and `503 evidence-storage-unavailable` are returned unchanged with
  `origin: "platform"`; after a `scope-lost` answer, later calls are still forwarded and return the platform's stale
  disposition; `mcp-platform-unavailable` (503) and `mcp-platform-authentication-failed` (502) carry
  `origin: "mcp-server"`.
- [ ] T010 [P] [US2] Add the outbound isolation evidence in `src/agents/mcp-server/tests/test_outbound_isolation.py`:
  across a session that exercises every tool and refusal, the fake platform records only the token operation, the
  pinned-artifact read, and `agentTool*` operations; every request carries `Authorization: DPoP` with the server's own
  client identity and never the runtime secret, a member session cookie, or an anti-forgery header; and the server
  opens no connection to any other host.

**Checkpoint**: The server cannot widen scope or hide a refusal.

## Phase 5: User Story 3 - Operators can run, probe, and package the server (Priority: P3)

**Goal**: The server starts from configuration, reports liveness and readiness, and ships as an image that follows
the image rules.

**Independent Test**: Invalid configuration stops the server naming the setting; liveness and readiness behave
against the fake platform; the image builds and runs read-only as a non-root user without secrets.

### Parallel runtime and image definition

- [ ] T011 [P] [US3] Add health and the entry point per research R8 and contract §2 in
  `src/agents/mcp-server/src/socalytics_mcp_server/app.py` and `src/agents/mcp-server/src/socalytics_mcp_server/__main__.py`
  with `src/agents/mcp-server/tests/test_health.py` and `src/agents/mcp-server/tests/test_entrypoint.py`: `GET /alive`
  → `200 { "status": "alive" }`; `GET /health` → `200 { "status": "ready" }` while a workload token with more than 30
  seconds left is held or obtained within the platform timeout, otherwise `503 { "status": "not-ready" }`, with probe-
  triggered token requests at most once per 5 seconds; neither needs the runtime secret. `python -m
  socalytics_mcp_server` validates configuration, configures logging, and serves the app with uvicorn on `PORT`
  (uvicorn access logs off; startup and error logs without headers or URLs with attempt identifiers). Tests: ready
  within 10 seconds against the fake platform and not ready when it refuses the client; invalid configuration exits
  with code 2 within 5 seconds naming every invalid variable and no value.
- [ ] T012 [P] [US3] Add the image definition per research R9 and contract §5: `src/agents/mcp-server/Dockerfile`,
  `src/agents/mcp-server/Dockerfile.dockerignore`, and `src/agents/mcp-server/docker/uv-requirements.txt` (uv 0.13.1
  with one `--hash` per published Linux wheel), with `src/agents/mcp-server/tests/test_dockerfile.py`. Two stages on
  `python:3.12-slim-bookworm@sha256:<digest current at implementation time>`; the build fails naming
  `SOCALYTICS_REVISION` or `SOCALYTICS_VERSION` when missing or malformed (version shape `YYYY.MMDD.HHMMSS`); uv is
  installed with `pip install --require-hashes --only-binary :all:`; the project is installed with `uv sync --locked
  --all-groups --no-install-local --no-build` and `uv sync --locked --no-dev --no-editable --no-build-isolation
  --compile-bytecode` into `/opt/socalytics-mcp-server/venv` with `UV_PYTHON_DOWNLOADS=never`; the final stage copies
  only that environment, sets the labels `org.opencontainers.image.revision`, `.version`, `.source`
  (`https://github.com/markusheiliger/socalytics`), `.title` (`socalytics-mcp-server`), and `.description`,
  `PYTHONDONTWRITEBYTECODE=1`, `PYTHONUNBUFFERED=1`, `USER 65532:65532`, `EXPOSE 8080`, no `HEALTHCHECK`, and
  `ENTRYPOINT ["/opt/socalytics-mcp-server/venv/bin/python", "-m", "socalytics_mcp_server"]`. Tests check the digest
  pin, the uv version against `.github/actions/environment-setup/analyst-media-toolchain.json` and its hashes, the
  labels, user, port, entry point, no `HEALTHCHECK`, no CPU architecture, no secret-like `ENV` or `ARG`, and that the
  ignore file admits only `pyproject.toml`, `uv.lock`, `src/`, and `docker/` of the project.

### Image evidence

- [ ] T013 [US3] Add `src/platform/Tests/SocAlytics.Platform.Images.Tests/McpServerImageTests.cs` using the image test
  support of [Cloud-Neutral OCI Images](../archive/20261018-092503-oci-images/tasks.md) (`ImageBuildFixture`, `ContainerProbe`,
  `LayerScanner`): build `src/agents/mcp-server/Dockerfile` from the repository root with both build arguments and
  assert the build fails without each; start the image read-only with a `/tmp` tmpfs, only environment variables,
  and mounted test key and secret files, with `SOCALYTICS_MCP_PLATFORM_URL` pointing at an unreachable address; assert
  `/alive` answers `200` within 30 seconds, `/health` answers `503`, the process UID is 65532, `ReadonlyRootfs` is
  true, the labels match contract §5, no in-image health probe is declared, and no layer or image configuration
  contains a private key, the test secret, or a `.venv` from the build context; start it without
  `SOCALYTICS_MCP_WORKLOAD_CLIENT_ID` and assert exit code 2 naming the variable.

**Checkpoint**: The server is a runnable, probeable, packaged deployable.

## Phase 6: Polish And Cross-Cutting Concerns

### Parallel evidence and documentation

- [ ] T014 [P] Add the end-to-end redaction evidence in `src/agents/mcp-server/tests/test_logging_redaction.py`: with
  sentinel values for a link, a fact payload, a fencing token, an access token, a DPoP proof, the private key, and the
  runtime secret, drive every session outcome and every tool outcome of T006–T011 and assert that none of the
  sentinels appears in captured standard output or standard error and that every record has only the permitted keys
  (SC-004).
- [ ] T015 [P] Document the server: `src/agents/mcp-server/README.md` (purpose, the four locked project commands, the
  catalog regeneration command, configuration variables and files, `/alive` and `/health`, the image build command
  and its evidence command, development-only values, and what remains open); `src/agents/README.md` (the MCP server as
  the first executable project; Hermes still planned); the repository `README.md` (the server's commands with the other
  supported commands); and `AGENTS.md` "Current State" and "Repository Setup" (the server, its commands, its
  development evidence, the deferred cross-process evidence, and Hermes and model execution among what does not
  exist).
- [ ] T016 [P] Make `docs/architecture/` describe the merged state: re-read the documents at implementation time and
  make every narrative and status statement listed in plan.md "Architecture Updates" true, including
  `intelligence-and-agents.md` (MCP Layer, MCP Tools, Authorization, Lineage, And Lifecycle), `intelligence-agent-catalog.md`
  (introduction, MCP Tool Policy), `platform-implementation.md` (Source and Runtime Baseline, Agent Runtime),
  `tenancy-and-technology.md` (AI), `overview.md`, `analyst-capability-catalog.md`, `analysts-models-and-hardware.md`,
  `security-and-data-governance.md` (`GOV-CRED-003`, `BND-006`, `CTL-007`), `production-operations.md`,
  `terminology-and-principles.md`, and `README.md`, and any other planned, future-tense, or "not implemented" wording
  about the MCP server, MCP tools, or `src/agents`; turn the design updates into present tense as development evidence,
  keep Hermes, model execution, and every production value open, and treat the listed rewrites as examples, not the
  whole job.

### Final acceptance

- [ ] T017 Run the validation of `specs/20261018-111804-mcp-server/quickstart.md`: the locked project sequence and
  `uv run python tools/generate_tool_catalog.py` with no diff in `src/agents/mcp-server/`, the documented `docker build`
  command, `dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests --filter McpServerImageTests`, and
  `node .github/scripts/check-markdown.mjs`; fix any failure in the files of the task it belongs to without weakening a
  test, and confirm that no platform operation, contract, or OpenAPI snapshot changed.

## Dependencies And Execution Order

```text
T001 -> T002, T003, T004 (parallel) -> T005
T005 -> T006 -> T007                      US1 (MVP)
T007 -> T008, T009, T010 (parallel)       US2
T010 -> T011, T012 (parallel) -> T013     US3
T013 -> T014, T015, T016 (parallel) -> T017
```

- T002, T003, and T004 change disjoint files; T005 needs the catalog (T002) and the credentials (T004).
- US1 needs the foundation; T007 uses the sessions of T006.
- US2 extends `sessions.py` (T008) and `tools.py` (T009) separately and adds a new test file (T010).
- US3: T011 changes `app.py` and adds `__main__.py`; T012 adds the image files; T013 builds the image of T012.
- T014 depends on every outcome of T006–T011; T016 needs the implemented behavior to describe; T017 is last.

## Parallel Execution Examples

- **Foundational**: T002 (catalog), T003 (configuration and logging), and T004 (credentials and shared test support)
  together; T005 after them.
- **US2**: T008 (`sessions.py`, `tests/test_session_refusals.py`), T009 (`tools.py`, `tests/test_tool_refusals.py`), and
  T010 (`tests/test_outbound_isolation.py`) together.
- **US3**: T011 (`app.py`, `__main__.py`, health and entry point tests) and T012 (Dockerfile, ignore file,
  `docker/uv-requirements.txt`, `tests/test_dockerfile.py`) together; T013 after T012.
- **Polish**: T014 (redaction test), T015 (READMEs and `AGENTS.md`), and T016 (`docs/architecture/`) together; T017
  last.

## Implementation Strategy

1. Create the project and its foundation (T001–T005).
2. Deliver US1 as the MVP: attempt-bound sessions that forward every tool faithfully (T006, T007).
3. Harden with US2's refusals and isolation evidence (T008–T010).
4. Make it operable and packaged with US3 (T011–T013).
5. Prove redaction end to end, document the merged state, and run the full validation (T014–T017).

Passing these tasks is development evidence only; it does not establish Hermes support, a cross-process path with the
real platform (deferred to the agent runtime feature), deployment support, or production readiness.
