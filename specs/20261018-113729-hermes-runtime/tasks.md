# Tasks: Hermes Agent Runtime

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md),
[Hermes runtime contract](contracts/hermes-runtime.md), [attempt report 1.1.0](contracts/attempt-report-1.1.0.md),
and [quickstart.md](quickstart.md).

**Tests**: Required by FR-029 and FR-030 and the validation guide. Each implementation task includes its directly
related tests in `src/agents/hermes/tests/` (or the named platform test project), so a task never lands deliberately
failing tests. Hermes project tests run in process without network services, Docker, model providers, or secrets; all
model interactions use the scripted provider.

**Organization**: Setup and foundational modules first, then one phase per user story in priority order, then
documentation and final acceptance.

## Format And Boundaries

- Tasks use `- [ ] Tnnn [P?] [USn?] Description`.
- Consecutive unchecked `[P]` tasks under one heading run in parallel, each from the same commit; their predicted file
  changes must be disjoint and neither may depend on the other.
- Hermes code lives only in `src/agents/hermes/`. Other source changes are limited to: the `attempt-report` 1.1.0
  contract under `contracts/agent-orchestration/attempt-report/v1/`, the completion checks and read schemas in the
  platform's Agent Orchestration folders with the refreshed OpenAPI snapshot, the regenerated MCP server tool catalog,
  the optional CA-file setting of `src/agents/mcp-server/`, and the image and cross-process tests in
  `src/platform/Tests/SocAlytics.Platform.Images.Tests/`. No migration, no new platform operation, no AppHost change.
- Run Hermes checks from `src/agents/hermes/` with the environment's locked sequence:
  `uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`,
  `uv build --wheel --no-build-isolation`, `uv run --no-sync pytest`. Change dependencies only by editing
  `pyproject.toml` and regenerating `uv.lock` with `uv lock`.
- `src/agents/hermes/pyproject.toml`, `uv.lock`, and `.gitignore` are changed only by T001, and
  `src/agents/hermes/tests/conftest.py` only by T004; later tasks keep their fixtures in their own test files or in new
  files under `src/agents/hermes/tests/fakes/` and `src/agents/hermes/tests/scripts/`.

## Phase 1: Setup

**Purpose**: Create the independent agent project so verification recognizes it.

- [ ] T001 Create the uv project `src/agents/hermes/` per research R1: `pyproject.toml` with
  `[project].name = "socalytics-hermes"`, `requires-python = ">=3.12,<3.13"`, runtime dependencies
  `agent-framework-core==1.22.0` and `mcp==2.3.0` (or the `mcp` version the framework admits, recorded in the README)
  and direct declarations of `httpx`, `pyjwt[crypto]`, `jsonschema`, `rfc8785`, `nats-py`, `nkeys`, `starlette`, and
  `uvicorn` with lower bounds the lock satisfies, `[build-system]` on `hatchling` with the package
  `src/socalytics_hermes` (package data such as `contracts/*.json` and `providers/script.schema.json` included in the
  wheel), a `dev` dependency group with `hatchling` and `pytest`, and pytest configured for `tests/` with the anyio
  plugin; the committed `uv.lock` generated with `uv lock`; `src/socalytics_hermes/__init__.py` with the package
  version; and `tests/test_package.py` asserting the package imports and its version. Add `src/agents/hermes/.venv/` and
  `src/agents/hermes/dist/` to the repository `.gitignore` only if no existing entry covers them. The locked sequence
  passes (FR-001).

**Checkpoint**: The project is a valid agent project with one passing test.

## Phase 2: Foundational

**Purpose**: Contracts, configuration, logging, platform credentials, the platform contract release, and the MCP server
setting every story needs.

### Independent foundation modules

- [ ] T002 [P] Add the contract copies per research R5: `src/agents/hermes/tools/sync_contracts.py` copies the released
  schemas `contracts/agent-orchestration/work-notification/v1/releases/1.0.0.schema.json`,
  `contracts/agent-orchestration/execution-envelope/v1/releases/1.1.0.schema.json`,
  `contracts/agent-orchestration/attempt-report/v1/releases/1.1.0.schema.json`, and
  `contracts/agent-catalog/{agent-version,prompt-template,model-selection,tool-policy,safety-policy}/v1/releases/1.0.0.schema.json`
  into `src/agents/hermes/src/socalytics_hermes/contracts/`; `src/agents/hermes/src/socalytics_hermes/contract_validation.py`
  (Draft 2020-12 validators by contract name, `digest(document)` = `sha-256:` + hex SHA-256 of the RFC 8785 canonical
  form); and `src/agents/hermes/tests/test_contract_copies.py`: each copy equals its repository file byte for byte
  (fails with the command to rerun), each validator accepts the contract's valid fixtures and rejects its invalid ones,
  and `digest` reproduces the RFC 8785 test vectors. Because `attempt-report` 1.1.0 is created by T005, the copy of
  that schema and its assertions are added by T007, not here (FR-008, FR-019).
- [ ] T003 [P] Add configuration and logging per data-model "Configuration" and contract §5:
  `src/agents/hermes/src/socalytics_hermes/configuration.py` (every `SOCALYTICS_HERMES_*` variable with its rules,
  secrets and keys read only from the named files, `simulated` allowed only when `SOCALYTICS_HERMES_ENVIRONMENT` is
  `Development` or `Test`, credential file required for `openai` and `github-models` and forbidden for `simulated`,
  script file required only for `simulated`, `http` platform URLs only in Development and Test, one validation error
  per invalid variable naming it without its value) and `src/agents/hermes/src/socalytics_hermes/logging.py`
  (single-line JSON records with only the permitted keys of contract §5, a filter that drops any other key, library
  loggers of `openai`, `httpx`, `mcp`, and `nats` raised to `WARNING` and passed through the same filter); tests in
  `src/agents/hermes/tests/test_configuration.py` (each required variable missing, each invalid value, provider rules,
  defaults equal to the development values, secret values never in messages) and
  `src/agents/hermes/tests/test_logging.py` (unknown keys dropped, library records filtered) (FR-010, FR-011, FR-024,
  FR-026).
- [ ] T004 [P] Add the platform client per research R2 and contract §3:
  `src/agents/hermes/src/socalytics_hermes/platform/proofs.py` (ES256 client assertions with `iss`/`sub` the client id,
  `aud` the public token endpoint URL, `jti`, `iat`, `exp` 60 s; DPoP proofs with `typ` `dpop+jwt`, public `jwk`,
  `jti`, `iat`, `htm`, `htu` from the public base URL without query, `ath` for authorized requests),
  `platform/tokens.py` (memory-only token cache renewed below 30 s of lifetime and once after a `401` DPoP challenge),
  and `platform/client.py` (path allowlist of the eight operations of contract §3 only, typed calls for token, queue
  credentials, claim, pinned-artifact read, renewal, checkpoint, completion, and failure, `SOCALYTICS_HERMES_PLATFORM_CA_FILE`
  trusted in addition to the default store, `SOCALYTICS_HERMES_PLATFORM_TIMEOUT_SECONDS`, at most two retries with the
  same operation identifier on connection failures or `5xx`, problem details returned as typed errors with code and
  `disposition`); the ASGI fake `src/agents/hermes/tests/fakes/fake_platform.py` (verifies assertions and proofs with the
  configured public key, records every request, answers each operation from scripted responses) and shared fixtures
  in `src/agents/hermes/tests/conftest.py` (test keys and the fake platform; the client is constructed from explicit
  parameters, not from `configuration.py`, so this task is independent of T003); tests in `src/agents/hermes/tests/test_platform_client.py` (assertion and proof claims, token renewal and
  the `401` challenge, other paths refused before sending, retries keep the operation identifier, stale problems carry
  their disposition, the CA file is used) (FR-003, FR-020).
- [ ] T005 [P] Add `attempt-report` release 1.1.0 per contracts/attempt-report-1.1.0.md: update
  `contracts/agent-orchestration/attempt-report/v1/attempt-report.schema.json` to 1.1.0 with the optional
  `executionReferences.modelSelectionRef` (`^sha-256:[0-9a-f]{64}$`) and `parameters` (closed; optional `temperature`
  0–2, `topP` 0–1, `maxOutputTokens` integer 1–32,768, `reasoningEffort` `low`|`medium`|`high`), add
  `releases/1.1.0.schema.json` (keep `releases/1.0.0.schema.json` unchanged), register the release where the contract
  index lists releases, and add the fixtures `examples/valid/completion-1.1.0.json`,
  `examples/valid/completion-1.0.0-shape.json`, `examples/invalid/completion-parameters-extra.json`, and
  `examples/invalid/completion-model-selection-ref-malformed.json`, validated by
  `src/platform/Tests/SocAlytics.Platform.Contracts.Tests`. The Hermes copy of the release is made by T007 (FR-019a).
- [ ] T006 [P] Add the optional `SOCALYTICS_MCP_PLATFORM_CA_FILE` to the MCP server per contract §7: read the PEM bundle
  in `src/agents/mcp-server/src/socalytics_mcp_server/configuration.py` (invalid or unreadable file names the
  variable), trust it in addition to the default store in
  `src/agents/mcp-server/src/socalytics_mcp_server/platform/client.py`, cover both in
  `src/agents/mcp-server/tests/test_configuration.py` and its platform client tests, and list it in
  `src/agents/mcp-server/README.md` (FR-030).

### Contract copy completion

- [ ] T007 Copy `contracts/agent-orchestration/attempt-report/v1/releases/1.1.0.schema.json` into
  `src/agents/hermes/src/socalytics_hermes/contracts/` through `src/agents/hermes/tools/sync_contracts.py` and extend
  `src/agents/hermes/tests/test_contract_copies.py` with its byte-equality and fixture assertions, so Hermes validates
  completions against the release T005 created (FR-019a).

**Checkpoint**: Contracts, configuration, logging, and platform credentials exist; the platform contract and the MCP
server setting are ready.

## Phase 3: User Story 1 - A specialist answers a member's question with evidence-backed advice (Priority: P1) MVP

**Goal**: Hermes claims a notified direct invocation, executes it with the pinned artifacts, the scripted provider,
and one MCP session, and completes it with five-section advice and execution references the platform accepts.

**Independent Test**: With the fake platform, the fake MCP server, and a script that calls `get_match` once and then
answers, deliver a notification and confirm the claim, the pinned read, one MCP session with the attempt headers,
only the session's tools offered, renewals, checkpoints, and a completion whose advice has the five sections and whose
execution references equal the pins and the resolved model selection.

### Execution building blocks

- [ ] T008 [P] [US1] Add work intake per research R3 and contract §2:
  `src/agents/hermes/src/socalytics_hermes/intake/work_source.py` (protocol delivering validated `work-notification`
  1.0.0 messages with acknowledge and delayed negative acknowledge, and an in-memory source for tests) and
  `src/agents/hermes/src/socalytics_hermes/intake/nats_source.py` (queue credentials with an in-memory NKey and user JWT
  renewed before `expiresAt`, mode `None` on `409 queue-credentials-unavailable` using `SOCALYTICS_HERMES_NATS_URL`,
  pull consumer `agent-workload` on `AGENT_WORK` subject `agents.invocations.ready`, fetch bounded by free slots,
  invalid messages acknowledged and logged `notification.invalid`); tests in `src/agents/hermes/tests/test_intake.py`
  (validation, acknowledge rules, slot bound, credential renewal timing with a fake clock, mode `None`) (FR-004, FR-005).
- [ ] T009 [P] [US1] Add artifact resolution per research R5 and data-model "Failure classification":
  `src/agents/hermes/src/socalytics_hermes/artifacts.py` validates the envelope against `execution-envelope` 1.1.0 and
  requires `agent` pins, maps the pinned-artifact read into the agent version, prompt template, model selection, tool
  policy, and safety policy, and checks presence of exactly these kinds, each record's schema, `digest(document)` equal
  to its pin, `group` = `specialist`, `routes` containing `user-to-specialist`, and prompt slots within `agent_id`,
  `display_name`, `tool_policy`, `evidence_context`, returning a typed result or a failure with reason
  `invalid-artifact`, `incompatible-artifact`, or `unsupported-route` (profile or modifier artifacts, Coach Agent group,
  route other than `user-to-specialist`); tests in `src/agents/hermes/tests/test_artifacts.py` for the seed-shaped
  specialist records and each violation (FR-006, FR-007, FR-008).
- [ ] T010 [P] [US1] Add the provider adapter per research R6 and R7:
  `src/agents/hermes/src/socalytics_hermes/providers/base.py` (`ModelProvider` protocol, `ResolvedModel` with
  `provider`, `model`, `deployment`, `parameters`, provider match check returning `model-provider-mismatch`, error
  mapping to `provider-unavailable`, `provider-authentication-failed`, `provider-refused`),
  `providers/openai_compatible.py` (Agent Framework OpenAI chat client for `openai` with default endpoint
  `https://api.openai.com/v1` and `github-models` with default endpoint `https://models.github.ai/inference`, the
  pinned `deployment` as request model, exactly the pinned parameters, the credential from its file),
  `providers/scripted.py` and `providers/script.schema.json` (the scripted chat client serving `simulated`: scenario
  selection by agent identifier and `[script:<marker>]`, steps `toolCalls`, `answer` with `{{callIds}}`, `error`; no
  matching scenario fails `provider-refused`); tests in `src/agents/hermes/tests/test_providers.py` (selection,
  mismatch, parameters sent exactly as pinned through an `httpx` mock transport with no network, each error mapping,
  script validation and scenario selection, no credential or endpoint for `simulated`) (FR-009, FR-010, FR-011,
  FR-012).
- [ ] T011 [P] [US1] Add rendering and the response contract check per research R8 and data-model "Response contract
  check": `src/agents/hermes/src/socalytics_hermes/rendering.py` (slot filling, runtime section
  `hermes-runtime-section/1` with hard boundaries, refusal and escalation categories, the evidence citation rule, and
  the five headings; templates without slots used verbatim plus the section; history rendered oldest first with
  assistant turns attributed to their agent; question as final user message; tool results as tool messages) and
  `src/agents/hermes/src/socalytics_hermes/advice.py` (non-empty, at most `limits.maxAdviceCharacters`, the five
  headings in order at line starts, case-insensitive, optional Markdown markers, colon or line end); tests in
  `src/agents/hermes/tests/test_rendering.py` (each slot, seed prompts verbatim, injection text in the question and tool
  results never in the system context, attribution of other agents' turns) and `src/agents/hermes/tests/test_advice.py`
  (FR-013, FR-014, FR-018).
- [ ] T012 [P] [US1] Add the evidence session per research R9:
  `src/agents/hermes/src/socalytics_hermes/evidence.py` (one official-SDK Streamable HTTP client session per attempt to
  `SOCALYTICS_HERMES_MCP_URL` with the bearer secret, `SocAlytics-Attempt-Id`, and `SocAlytics-Fencing-Token`; listed
  tools ∩ pinned tool policy exposed as Agent Framework function tools with the server's input schemas; results passed
  as tool content; unlisted tool requests answered locally as not permitted; stale tool errors raised as stale; session
  `DELETE` at the end), the in-process fake `src/agents/hermes/tests/fakes/fake_mcp_server.py` (official SDK server
  that checks the secret and binding headers and answers scripted tool results and errors), and
  `src/agents/hermes/tests/test_evidence.py` (headers, tool intersection, faithful results including "analysis
  unavailable" and refusals, local refusal of unlisted tools, session closed) (FR-015).
- [ ] T013 [P] [US1] Add the platform completion checks per contracts/attempt-report-1.1.0.md and research R11: in
  `src/platform/SocAlytics.Platform.Application/AgentOrchestration/` make `CompleteAgentAttempt` refuse with
  `409 execution-references-mismatch` (fields named in the problem details, attempt unchanged) when `modelProvider`,
  `model`, or `deployment` differ from the pinned model selection, or when present `modelSelectionRef` or
  `parameters` differ from the pinned digest or parameters (absent pinned parameters equal `{}`), and store both fields
  in the lineage `completed` entry and accepted advice; expose them in the completion request and the
  `getAgentInvocation` and `getAgentInvocationLineage` read schemas in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/`;
  refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
  and regenerate `src/agents/mcp-server/src/socalytics_mcp_server/tool_catalog.json` with
  `uv run python tools/generate_tool_catalog.py` from `src/agents/mcp-server/` so its OpenAPI digest matches; add
  integration tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/` for each mismatch,
  equal 1.1.0 completions, unchanged 1.0.0-shaped completions of the simulated workload, and the owner's reads showing
  the resolved parameters (FR-019a, FR-029).

### Attempt execution

- [ ] T014 [US1] Add the attempt executor and runner per research R4 and data-model "Attempt execution":
  `src/agents/hermes/src/socalytics_hermes/execution.py` (`AttemptExecutor`: claim answer handling, artifacts (T009),
  provider (T010), rendering (T011), evidence session (T012), model turns through an Agent Framework agent bounded by
  `MAX_MODEL_TURNS` and `MODEL_TIMEOUT_SECONDS`, lease renewal every `leaseSeconds / 3`, checkpoints
  `artifacts-resolved`, `evidence-session-opened`, and `model-turn` with content-free notes, completion with operation
  identifier `hermes-<attemptId>-complete` and the execution references of data-model "Execution references" including
  `runtime` `hermes/<package version>+hermes-runtime-section/1`, `modelSelectionRef`, and `parameters`) and
  `src/agents/hermes/src/socalytics_hermes/runner.py` (intake loop over the work source, claims with
  `hermes-claim-<invocationId>-<uuid4>`, concurrency bound `MAX_CONCURRENT_ATTEMPTS`, acknowledge rules); the script
  `src/agents/hermes/tests/scripts/get-match-then-answer.json`; and `src/agents/hermes/tests/test_execution.py` for the
  happy path of the Independent Test, including two concurrent notifications and the slot bound, and that the attempt
  writes no file and keeps no state after it ends (FR-002, FR-004, FR-005, FR-013 to FR-020).

**Checkpoint**: Hermes completes a direct specialist invocation against the fakes; the platform accepts its completion
references.

## Phase 4: User Story 2 - Failures end attempts explicitly and recover from durable state (Priority: P2)

**Goal**: Every failure ends the attempt with the documented reason and retryability, stale dispositions stop the work
without a report, and shutdown fails active attempts as retryable.

**Independent Test**: Drive each failure, stale disposition, and shutdown against the fakes and confirm exactly one
failure report with the documented reason and retryability, or none when the platform already ended the attempt.

- [ ] T015 [US2] Complete the failure classification in `src/agents/hermes/src/socalytics_hermes/execution.py` per
  data-model "Failure classification" (one failure report `hermes-<attemptId>-fail` with the reason and retryability
  of the table; no model or tool call after an artifact, route, or provider-mismatch failure; `advice-invalid` for
  empty, too long, or section-missing answers; `attempt-deadline` at `DEADLINE_MARGIN_SECONDS` before `deadlineAt`;
  `mcp-unavailable` for session open failures, lost sessions, and MCP platform-unavailable or authentication errors;
  `runtime-error` for unexpected exceptions and refused completions) with scripts under
  `src/agents/hermes/tests/scripts/` and tests in `src/agents/hermes/tests/test_failures.py` covering every row
  (FR-008, FR-010, FR-016, FR-018, FR-021; SC-003, SC-004).
- [ ] T016 [US2] Add stale handling in `src/agents/hermes/src/socalytics_hermes/execution.py` and
  `src/agents/hermes/src/socalytics_hermes/runner.py` per research R4: every `409 agent-attempt-stale` disposition
  (including `scope-lost`) from renewal, checkpoint, completion, failure, the pinned-artifact read, the MCP session
  refusal, or a forwarded tool error cancels the work, closes the session, discards messages, and sends nothing more;
  a renewal that cannot succeed before `leaseExpiresAt` stops the attempt; `409 claim-obsolete`, `409 agent-scope-lost`,
  and `404` on claim acknowledge the notification without work; duplicate deliveries never run two attempts; tests in
  `src/agents/hermes/tests/test_stale.py` for each disposition and source (FR-020, FR-022; SC-003, SC-004).
- [ ] T017 [US2] Add shutdown in `src/agents/hermes/src/socalytics_hermes/runner.py` and
  `src/agents/hermes/src/socalytics_hermes/execution.py` per research R12: on stop, fetch no more notifications, cancel
  model and tool work, fail each active attempt with `runtime-stopping` (retryable) within `SHUTDOWN_SECONDS`, close
  MCP sessions and the work source, never complete after the stop began; tests in
  `src/agents/hermes/tests/test_shutdown.py` (reports within the bound, unreachable platform leaves attempts to lease
  expiry, restart claims only notified work) (FR-023).

**Checkpoint**: Every failure path ends explicitly and recoverably.

## Phase 5: User Story 3 - Operators run, configure, probe, and package Hermes safely (Priority: P3)

**Goal**: Hermes starts from validated configuration, answers health probes, logs no content, and ships as an image
under the image rules.

**Independent Test**: Invalid settings stop Hermes naming them; health and readiness behave as specified; sentinel
content never reaches logs; the image builds and runs under the image rules.

### Operation and packaging

- [ ] T018 [P] [US3] Add the entry point and health per contract §1 and research R12:
  `src/agents/hermes/src/socalytics_hermes/health.py` (`/alive`; `/health` ready only while a workload token is held or
  obtained, at most one acquisition per 5 seconds from probes, and the work source is connected; bodies only
  `{ status }`) and `src/agents/hermes/src/socalytics_hermes/__main__.py` (configuration validation with exit code 2
  and one line per invalid setting, logging setup, runner and health server on `SOCALYTICS_HERMES_PORT`, `SIGTERM`
  handling); tests in `src/agents/hermes/tests/test_health.py` and `src/agents/hermes/tests/test_entrypoint.py`
  (SC-006: ready within 10 seconds against the fake platform, invalid configuration stops within 5 seconds) (FR-024,
  FR-025).
- [ ] T019 [P] [US3] Add the image definition per contract §6 and research R12: `src/agents/hermes/Dockerfile`
  (two stages on `python:3.12-slim-bookworm@sha256:<digest current at implementation time>`, required and validated
  `SOCALYTICS_REVISION` and `SOCALYTICS_VERSION`, uv 0.13.1 from `src/agents/hermes/docker/uv-requirements.txt` with
  per-wheel hashes, `uv sync --locked` into `/opt/socalytics-hermes/venv` with `UV_PYTHON_DOWNLOADS=never`, only that
  environment in the final stage, the five labels with title `socalytics-hermes`, `PYTHONDONTWRITEBYTECODE=1`,
  `PYTHONUNBUFFERED=1`, `USER 65532:65532`, `EXPOSE 8080`, no `HEALTHCHECK`, entry point
  `["/opt/socalytics-hermes/venv/bin/python", "-m", "socalytics_hermes"]`), `src/agents/hermes/Dockerfile.dockerignore`
  (only the project's sources, lockfile, and `docker/` files), and `src/agents/hermes/tests/test_dockerfile.py` (digest
  pin, uv version equal to `.github/actions/environment-setup/analyst-media-toolchain.json` with hashes, labels, user,
  port, no `HEALTHCHECK`, no secret `ENV`, no tests or caches copied) (FR-027).
- [ ] T020 [P] [US3] Add the end-to-end redaction evidence in `src/agents/hermes/tests/test_logging_redaction.py`: with
  sentinel values for the prompt template text, the question, a history turn, a tool argument, a tool result with a
  link, the advice, the fencing token, an access token, a DPoP proof, the workload key, the provider credential, and
  the MCP secret, drive the happy path and every failure, stale, and shutdown outcome of T014–T017 and assert that none
  of the sentinels appears in captured standard output or standard error and that every record has only the permitted
  keys (FR-017, FR-026; SC-005).

### Image evidence

- [ ] T021 [US3] Add `src/platform/Tests/SocAlytics.Platform.Images.Tests/HermesImageTests.cs` and extend the image
  build support in `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/` with the Hermes image (documented
  `docker build -f src/agents/hermes/Dockerfile` command, unique tag, test revision and version): image configuration
  (user `65532`, the labels, no `Healthcheck`), a read-only start with a `/tmp` tmpfs and test configuration whose
  platform is unreachable, `/alive` `200`, `/health` `503`, the process UID, a missing build argument failing the
  build, and a layer scan of `docker save` output for keys, credentials, secrets, tests, and caches (FR-027).

**Checkpoint**: Hermes is operable and packaged.

## Phase 6: User Story 4 - The runtime, the MCP server, and the platform prove the path together (Priority: P4)

**Goal**: The cross-process evidence the MCP server deferred.

**Independent Test**: The stack test of the quickstart passes.

- [ ] T022 [US4] Add the cross-process evidence per research R10:
  `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/TestCertificateAuthority.cs` (per-run CA and `api` server
  certificate with key, written as PEM files for read-only mounts), a stack fixture in
  `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/AgentRuntimeStack.cs` (one network with PostgreSQL and
  the roles script, NATS with JWT trust, RustFS, the Migrator image run, in-process Test-environment seeding of the
  configured workflow, the agent catalog seed, a Coach member, and a match with a `Completed` run through the
  integration tests' builders against the same database; the API image in its production environment with
  `ASPNETCORE_URLS` for HTTPS 8443 and HTTP 8080, `ASPNETCORE_Kestrel__Certificates__Default__Path` and `__KeyPath`,
  `Stamp__PublicBaseUri=https://api:8443`, NATS JWT settings, and workload clients `hermes` and `mcp-server` with test
  public keys; the MCP server image with its key, runtime secret, and `SOCALYTICS_MCP_PLATFORM_CA_FILE`; the Hermes
  image with `SOCALYTICS_HERMES_ENVIRONMENT=Test`, provider `simulated`, the mounted script
  `get-match-then-answer.json`, its key, the MCP secret, and `SOCALYTICS_HERMES_PLATFORM_CA_FILE`), and
  `src/platform/Tests/SocAlytics.Platform.Images.Tests/AgentRuntimeStackTests.cs`: the Coach signs in over HTTPS,
  creates a conversation for the completed run, adds `specialist-tactics`, submits a question, polls until `succeeded`
  within 60 seconds, and asserts the turns (advice attributed to `specialist-tactics` with the five sections), the
  lineage (one `tool-call` entry for `get_match` with outcome `answered`, Hermes' checkpoints, the completed entry), the
  execution references (pins, `simulated` model selection reference, `{}` parameters, `runtime` starting with
  `hermes/`), and that the Hermes and MCP server container logs contain neither the question nor the advice (FR-030;
  SC-001).

**Checkpoint**: The three components run together and the path is proven.

## Phase 7: Polish And Cross-Cutting Concerns

### Parallel documentation

- [ ] T023 [P] Document Hermes: `src/agents/hermes/README.md` (purpose, the four locked project commands, the contract
  copy command, configuration variables and files, providers and the Development and Test restriction of `simulated`,
  the script format, `/alive` and `/health`, failure reasons, the image build command and its evidence commands,
  development-only values, and what remains open); `src/agents/README.md` (Hermes next to the MCP server); the
  repository `README.md` (Hermes' commands with the other supported commands); and `AGENTS.md` "Current State" and
  "Repository Setup" (Hermes, its commands, its development evidence including the cross-process test, the
  `attempt-report` 1.1.0 completion checks, and Coach Agents, consultations, real provider evidence, and production
  readiness among what does not exist) (FR-028).
- [ ] T024 [P] Make `docs/architecture/` describe the merged state: re-read the documents at implementation time and
  make every narrative and status statement listed in plan.md "Architecture Updates" true, including
  `intelligence-and-agents.md` (Agent Orchestration Authority; Client, Transport, And Runtime Protocol; Authorization,
  Lineage, And Lifecycle), `intelligence-agent-catalog.md` (introduction, Prompt And Advice Governance, Implementation
  Waves), `platform-implementation.md` (Source and Runtime Baseline, Agent Runtime), `tenancy-and-technology.md` (AI),
  `overview.md`, `security-and-data-governance.md` (`GOV-CRED-003`, `ACT-005`, `DAT-008`), `production-operations.md`,
  `terminology-and-principles.md`, and `README.md`, and any other planned, future-tense, or "not implemented" wording
  about Hermes, the agent runtime, model execution, model providers, or `src/agents`; turn the design updates into
  present tense as development evidence, keep Coach Agents, consultations, evaluation gates, and every production value
  open, and treat the listed rewrites as examples, not the whole job.

### Final acceptance

- [ ] T025 Run the validation of `specs/20261018-113729-hermes-runtime/quickstart.md`: the locked Hermes sequence, the
  MCP server's locked sequence, `uv run python tools/sync_contracts.py` with no diff in `src/agents/hermes/`, the
  documented `docker build` command, `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`, the
  Agent Orchestration integration tests, the Host and Architecture test projects under `src/platform/Tests/`, `npm ci`
  and `npm run build` in `src/clients/web` (its client is generated from the refreshed OpenAPI snapshot),
  `dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests --filter "FullyQualifiedName~HermesImageTests|FullyQualifiedName~AgentRuntimeStackTests"`,
  and `node .github/scripts/check-markdown.mjs`; fix any failure in the files of the task it belongs to without
  weakening a test, and confirm that no migration, platform operation, or AppHost change was added.

## Dependencies And Execution Order

```text
T001 -> T002, T003, T004, T005, T006 (parallel) -> T007
T007 -> T008, T009, T010, T011, T012, T013 (parallel) -> T014      US1 (MVP)
T014 -> T015 -> T016 -> T017                                         US2
T017 -> T018, T019, T020 (parallel) -> T021                          US3
T021 -> T022                                                         US4
T022 -> T023, T024 (parallel) -> T025
```

- T002–T006 change disjoint files: Hermes contract copies, Hermes configuration and logging, Hermes platform client and
  shared test fixtures, the platform contract, and the MCP server. T007 needs T002's sync tool and T005's release.
- US1's building blocks change disjoint modules and test files and take plain mappings and identifiers as inputs
  rather than each other's types; T013 changes only platform code, the OpenAPI snapshot, and the MCP server's
  generated catalog. T014 composes T008–T012; T013 is needed only by the cross-process evidence of US4.
- US2's tasks all change `execution.py` or `runner.py` and run in order.
- US3: T018 adds `health.py` and `__main__.py`; T019 adds the image files; T020 adds one test file using the outcomes of
  T014–T017; T021 builds the image of T019.
- US4 needs the Hermes image (T021), the MCP server CA setting (T006), and the platform completion checks (T013).
- T024 needs the implemented behavior to describe; T025 is last.

## Parallel Execution Examples

- **Foundational**: T002 (contract copies), T003 (configuration and logging), T004 (platform client and fixtures),
  T005 (platform contract), and T006 (MCP server setting) together; T007 after them.
- **US1**: T008 (intake), T009 (artifacts), T010 (providers), T011 (rendering and advice), T012 (evidence session),
  and T013 (platform completion checks) together; T014 after them.
- **US3**: T018 (entry point and health), T019 (image files), and T020 (redaction test) together; T021 after them.
- **Polish**: T023 (READMEs and `AGENTS.md`) and T024 (`docs/architecture/`) together; T025 last.

## Implementation Strategy

1. Create the project and its foundation, including the platform contract release and the MCP server setting
   (T001–T007).
2. Deliver US1 as the MVP: a direct specialist invocation executed end to end against fakes, with the platform's
   completion checks (T008–T014).
3. Harden with US2's failure, stale, and shutdown handling (T015–T017).
4. Make it operable and packaged with US3 (T018–T021).
5. Prove the cross-process path with US4 (T022).
6. Document the merged state and run the full validation (T023–T025).

Passing these tasks is development evidence only; it does not establish model quality, real provider behavior,
deployment support, or production readiness.
