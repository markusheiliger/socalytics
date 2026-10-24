# Implementation Plan: Hermes Agent Runtime

**Branch**: `20261018-113729-hermes-runtime` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261018-113729-hermes-runtime/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

The intelligence plane gains its agent runtime: the independent locked uv project `src/agents/hermes`
(`socalytics-hermes`) on Microsoft Agent Framework (`agent-framework-core` 1.22.0). Hermes is its own agent workload
client with DPoP-bound tokens; it pulls the platform's agent work notifications from JetStream (with platform-issued
NATS credentials), claims each notified invocation, verifies the pinned agent version and artifacts read through the
attempt-scoped read (contract, RFC 8785 digest, group, route, prompt slots), renders the pinned prompt template with a
versioned runtime section for the safety policy and the shared response contract, opens one MCP session per attempt
with the MCP server and offers the model only that session's tools, renews the lease, records content-free
checkpoints, and completes with five-section advice and execution references, or fails with a bounded, classified
reason; stale dispositions stop the work without a report. A provider-neutral adapter serves exactly one configured
provider: the OpenAI API, GitHub Models, or the Development and Test scripted provider that serves the catalog's
`simulated` provider and drives every automated test. The platform's `attempt-report` contract gains release 1.1.0
(model-selection reference and resolved parameters), checked against the pins at completion. Hermes has `/alive`,
`/health`, content-free JSON logs, configuration from environment variables and files, and a Dockerfile under the
image rules. Cross-process evidence runs the API, MCP server, and Hermes images together over HTTPS with a test CA.
See [research.md](research.md).

## Technical Context

**Language/Version**: Python 3.12 (CPython 3.12.15 in verification; the base image's CPython 3.12 in the image) for
Hermes; C# on .NET 10 for the platform change and the image and cross-process tests.

**Primary Dependencies**: `agent-framework-core` 1.22.0 (with the `openai` SDK), `mcp` 2.3.0 (client), `httpx`,
`pyjwt[crypto]`, `jsonschema`, `rfc8785`, `nats-py`, `nkeys`, `starlette`, `uvicorn`; `hatchling` and `pytest` in the
locked `dev` group; all locked in `src/agents/hermes/uv.lock`. Platform: existing Agent Orchestration code, contracts,
and Testcontainers 4.15.0 in the image tests.

**Storage**: N/A for Hermes (memory only). Platform: existing `jsonb` columns of `agent_lineage_entries` and
`agent_accepted_advice`; no migration.

**Testing**: pytest in the project (fake platform over an ASGI transport, in-process fake MCP server on the official
SDK over an ASGI transport, in-memory work source, scripted provider, contract-copy conformance, log redaction,
configuration, health, shutdown, Dockerfile rules); contract tests for `attempt-report` 1.1.0; integration tests for
the completion checks and reads; Docker-backed `HermesImageTests` and `AgentRuntimeStackTests` in
`src/platform/Tests/SocAlytics.Platform.Images.Tests`.

**Target Platform**: Linux containers inside a stamp next to the platform API and the MCP server; developer machines for
local runs.

**Project Type**: Background worker service (agent runtime) with health endpoints, plus a small platform contract
change.

**Performance Goals**: A direct question answered within 60 seconds in the cross-process test (SC-001); readiness
within 10 seconds of start (SC-006); provider latency is outside Hermes' control and bounded by the model timeout.

**Constraints**: No durable state, database, or object storage access; exactly one configured provider and no
fallback; evidence only through the MCP session; no content in logs; development values only (concurrency 2, 8 model
turns, 60-second model timeout, 20-second shutdown); production values open.

**Scale/Scope**: One new project and image; one optional MCP server setting; one platform contract release with its
completion checks; two image test classes; documentation.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | Implements the Hermes parts of [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority), [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol), and [Platform Implementation Profile → Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime); decisions not yet stated there (project location, own workload client, per-attempt MCP session, provider set with a Development and Test scripted provider, completion contract 1.1.0, shutdown behavior) are applied now as decided but not implemented (see Architecture Updates). | Pass |
| II. Respect Source-Area Ownership | Hermes is an intelligence-agent runtime in `src/agents/hermes/`, as `src/agents/README.md` and [Source and Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline) assign; the completion check stays in the platform's Agent Orchestration folders; the contract change stays in `contracts/agent-orchestration/`; image and cross-process evidence join the existing image test project; the MCP server change stays in `src/agents/mcp-server/`. No new first-level area. | Pass |
| III. API-First Control Plane | Hermes uses only versioned `/api/v1` workload operations and the MCP server; it never touches databases or object storage; it validates against the authoritative JSON Schema contracts; it relays no media bytes. | Pass |
| IV. Evidence Over Claims | Project, contract, integration, image, and cross-process tests cover FR-029 and FR-030; passing tests are not presented as model quality, deployment support, or production readiness. | Pass |
| V. Focused, Minimal Changes | One project whose dependencies each serve a requirement; the platform change is one additive contract release and its check; the MCP server change is one optional setting; no AppHost change. | Pass |
| Technology and Tooling | CPython 3.12.15 and uv 0.13.1 come from the merged Analyst and Media Verification Environment; verification of `src/agents/<name>/` comes from [Agents Python Verification Environment](../archive/20261018-110526-agents-python-environment/spec.md), named as a dependency that must merge first; .NET 10, Docker, and Testcontainers for the platform and image tests are provided by the platform verification. No other tool is needed. | Pass |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Architecture Updates

**Narratives** (implemented or changed by this feature):

- [Intelligence and Agents → Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority):
  Hermes as the runtime, its adapters, provider set, no-fallback rule, and lineage of model execution.
- [Intelligence and Agents → Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol):
  Hermes as the workload that claims, renews, checkpoints, completes, and fails, and resolves the pinned artifacts.
- [Intelligence and Agents → Authorization, Lineage, And Lifecycle](../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle):
  stale dispositions and scope loss stop Hermes' work; lineage records model selection and parameters.
- [Intelligence Agent Catalog](../../docs/architecture/intelligence-agent-catalog.md) (introduction, Prompt And Advice
  Governance, Implementation Waves Wave 1): the runtime that executes direct specialists and the response contract.
- [Platform Implementation Profile → Source and Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline)
  and [Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime): `src/agents/hermes` and the
  completion contract change.
- [Tenancy and Technology → AI](../../docs/architecture/tenancy-and-technology.md#ai): Hermes technology and providers.
- [Architecture Overview](../../docs/architecture/overview.md) (current-state summary).
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md): `GOV-CRED-003` (Hermes'
  workload client and model provider credentials), `ACT-005` and `DAT-008` (active Hermes context as Cache).
- [Production Deployment and Operations](../../docs/architecture/production-operations.md): gate 7 (Hermes health and
  readiness) and the recoverable Hermes runtime statement.

**Design updates** (applied now to the owning narratives, worded as decided but not yet implemented; the
implementation turns each into present tense)

1. `intelligence-and-agents.md` Agent Orchestration Authority: a new paragraph after the provider paragraph on the first
   Hermes slice (`src/agents/hermes`, own workload client, notification intake, pinned artifact verification, rendering
   with a versioned runtime section, one MCP session per attempt, exactly one configured provider including a
   Development and Test scripted provider, model-selection reference and parameters checked at completion, classified
   failures, stale dispositions without report, shutdown failures, content-free logs, `/alive` and `/health`).
2. `platform-implementation.md` Agent Runtime: a new paragraph on Hermes' first slice as a separate Python OCI
   deployment, the `attempt-report` 1.1.0 release, and the HTTPS cross-process evidence.
3. `tenancy-and-technology.md` AI: the Hermes line names its first slice in `src/agents/hermes` (decided, not yet
   implemented), and a new line names the Development and Test scripted model provider.
4. `intelligence-agent-catalog.md` Implementation Waves, Wave 1: the Hermes runtime for direct conversations is decided
   and not yet implemented; the evaluation suites remain open.
5. `security-and-data-governance.md` `GOV-CRED-003`: Hermes' own workload client and its model provider credentials
   from mounted files are decided development scope, with production custody, rotation, and lifetime unresolved.

**Status statements at merge** (each must become true for the merged state)

- `intelligence-and-agents.md` Agent Orchestration Authority: "… executed in Development and Test by a simulated agent
  workload that stands in for Hermes" → the simulated workload remains the AppHost's Development and Test stand-in,
  and Hermes executes direct specialist invocations (development evidence); design update 1 loses "decided and not yet
  implemented".
- `intelligence-and-agents.md` Agent Orchestration Authority: "Hermes is a separate Python OCI service using Microsoft
  Agent Framework …" and Client, Transport, And Runtime Protocol "Hermes authenticates as a stamp-local workload …":
  confirm they describe the implemented runtime; delegated consultation and CA profile rendering stay later work.
- `platform-implementation.md` Agent Runtime: "Hermes is a planned separate Python OCI deployment …" → Hermes exists
  under `src/agents/hermes` for direct specialists; "MCP tools, prompt/profile resolution, model execution, and Web UI
  agent workflows remain future implementation work" (as left by earlier features) → prompt resolution and model
  execution for direct specialists exist; profile rendering, consultations, and any remaining Web UI agent workflows
  stay future; "… deterministic simulated advice in place of Hermes" → the simulated workload remains the AppHost's
  stand-in next to Hermes; design update 2 loses "decided and not yet implemented".
- `platform-implementation.md` Source and Runtime Baseline: the `src/agents` statement (as left by the MCP server) →
  names both executable projects, `mcp-server` and `hermes`.
- `overview.md` current-state summary: "… Python Analyst Containers, and Hermes as a separate Python OCI service are
  planned." (as left by earlier features) → Hermes exists as a separate Python service for direct specialists
  (development evidence); Coach Agents and consultations stay planned.
- `intelligence-agent-catalog.md` introduction: "… model execution, MCP evidence tools, Hermes orchestration runtime,
  and user experiences are likewise design inputs" (as left by earlier features) → model execution and the Hermes
  runtime exist for direct specialists as development evidence; prompts, profiles, and model choices stay Provisional
  design inputs; design update 4 loses "decided and not yet implemented".
- `tenancy-and-technology.md` AI: design update 3 loses "decided, not yet implemented".
- `security-and-data-governance.md` `GOV-CRED-003`: the Hermes development scope becomes implemented; production
  values stay unresolved. `ACT-005`, `DAT-008`, `POL-008`: keep their decision status; confirm "active Hermes Cache"
  matches a runtime that holds content only in memory for one attempt.
- `production-operations.md`: the recoverable Hermes statement and gate 7 ("… Hermes/MCP … pass their health and
  readiness checks"): confirm both hold now that Hermes has `/alive` and `/health`.
- `README.md` (architecture index) and `terminology-and-principles.md`: confirm that no statement calls Hermes planned or
  future beyond Coach Agents and consultations.
- Outside `docs/architecture/` (kept current by the same documentation task): `src/agents/README.md` names `hermes`
  next to `mcp-server` with its commands; `README.md` and `AGENTS.md` "Current State" and "Repository Setup" name
  Hermes' commands and development evidence and keep Coach Agents, consultations, real provider evidence, and
  production readiness among what does not exist.

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the listed
narratives and every status statement this feature affects in `docs/architecture/` true for the merged state,
re-reading the documents at implementation time (including `README.md`, `overview.md`,
`terminology-and-principles.md`, `production-operations.md`, and any other planned, future-tense, or "not implemented"
wording about Hermes, the agent runtime, model execution, model providers, or `src/agents`); the rewrites listed above
are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261018-113729-hermes-runtime/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── hermes-runtime.md
│   └── attempt-report-1.1.0.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/agents/
├── README.md                                    # changed: names hermes next to mcp-server
├── mcp-server/
│   ├── src/socalytics_mcp_server/configuration.py   # changed: optional SOCALYTICS_MCP_PLATFORM_CA_FILE
│   ├── src/socalytics_mcp_server/platform/client.py # changed: trusts the configured CA bundle
│   ├── tests/test_configuration.py                  # changed
│   └── README.md                                    # changed
└── hermes/                                      # new independent uv project
    ├── pyproject.toml                           # socalytics-hermes; hatchling, pytest in dev group
    ├── uv.lock
    ├── README.md                                # commands, configuration, providers, image build
    ├── Dockerfile
    ├── Dockerfile.dockerignore
    ├── docker/uv-requirements.txt               # uv 0.13.1 with per-wheel hashes
    ├── tools/sync_contracts.py                  # copies released schemas into package data
    ├── src/socalytics_hermes/
    │   ├── __init__.py
    │   ├── __main__.py                          # configuration, logging, runner, health server
    │   ├── configuration.py                     # SOCALYTICS_HERMES_* variables and files, validation
    │   ├── logging.py                           # JSON records with the permitted keys only
    │   ├── contracts/                           # package data: copied released schemas
    │   ├── contract_validation.py               # schema validators and RFC 8785 digests
    │   ├── platform/
    │   │   ├── proofs.py                        # client assertions and DPoP proofs (ES256)
    │   │   ├── tokens.py                        # workload token cache and renewal
    │   │   └── client.py                        # outbound path allowlist, workload operations, retries, CA bundle
    │   ├── intake/
    │   │   ├── work_source.py                   # work source protocol and in-memory source
    │   │   └── nats_source.py                   # JetStream pull consumer, queue credentials, NKey signing
    │   ├── artifacts.py                         # pinned-artifact resolution and validation
    │   ├── rendering.py                         # slots, runtime section, history, messages
    │   ├── providers/
    │   │   ├── base.py                          # ModelProvider protocol, ResolvedModel, error mapping
    │   │   ├── openai_compatible.py             # openai and github-models
    │   │   ├── scripted.py                      # simulated provider
    │   │   └── script.schema.json
    │   ├── evidence.py                          # MCP session per attempt, function tools, stale detection
    │   ├── advice.py                            # response contract check
    │   ├── execution.py                         # AttemptExecutor: lease, checkpoints, completion, failures
    │   ├── runner.py                            # concurrency, intake loop, shutdown
    │   └── health.py                            # /alive, /health
    └── tests/
        ├── conftest.py                          # keys, configuration, fake platform, fake MCP server
        ├── fakes/fake_platform.py               # ASGI fake: assertions, proofs, recorded workload calls
        ├── fakes/fake_mcp_server.py             # official SDK server with scripted tool answers
        ├── scripts/                             # scripted provider scripts
        ├── test_contract_copies.py
        ├── test_configuration.py
        ├── test_logging.py
        ├── test_platform_client.py
        ├── test_intake.py
        ├── test_artifacts.py
        ├── test_providers.py
        ├── test_rendering.py
        ├── test_evidence.py
        ├── test_advice.py
        ├── test_execution.py
        ├── test_failures.py
        ├── test_stale.py
        ├── test_shutdown.py
        ├── test_health.py
        ├── test_entrypoint.py
        ├── test_logging_redaction.py
        └── test_dockerfile.py

contracts/agent-orchestration/attempt-report/v1/
├── attempt-report.schema.json                   # changed: 1.1.0
├── releases/1.1.0.schema.json                   # new
└── examples/{valid,invalid}/                    # new fixtures of contracts/attempt-report-1.1.0.md

src/platform/
├── SocAlytics.Platform.Application/AgentOrchestration/   # changed: completion model-selection checks, read models
├── SocAlytics.Platform.Api/AgentOrchestration/           # changed: request and read schemas
├── SocAlytics.Platform.Api/openapi/v1.json               # refreshed snapshot
└── Tests/
    ├── SocAlytics.Platform.Contracts.Tests/              # attempt-report 1.1.0 fixtures
    ├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/ # completion checks and reads
    └── SocAlytics.Platform.Images.Tests/
        ├── HermesImageTests.cs                           # new
        ├── AgentRuntimeStackTests.cs                     # new: cross-process evidence
        └── Support/                                      # changed: Hermes image build, test CA, stack fixture

README.md, AGENTS.md                             # commands and Current State
```

**Structure Decision**: One independent agent project as the environment convention defines it, with a src layout so
tests import the installed package. Contract copies are package data because the image contains no repository files.
The platform change lives in the Agent Orchestration folders that own completions. Docker-backed evidence joins the
image test project of [Cloud-Neutral OCI Images](../archive/20261018-092503-oci-images/plan.md), which already builds images
and starts the API image with its dependencies, so the agent project's default test run stays free of Docker and
network services. This feature is implemented after the agents Python environment, Agent Orchestration Direct
Invocations, the Agent Catalog Registry, the Agent Evidence Tools API, the MCP Server, and the OCI images feature have
merged.
