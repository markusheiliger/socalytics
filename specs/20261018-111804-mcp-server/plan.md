# Implementation Plan: MCP Server

**Branch**: `20261018-111804-mcp-server` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261018-111804-mcp-server/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

The intelligence plane gains its MCP server: the first executable project of `src/agents/`, the independent locked uv
project `src/agents/mcp-server` (`socalytics-mcp-server`) on the official MCP Python SDK `mcp` 2.3.0. It serves MCP over
Streamable HTTP at `/mcp` only to the stamp's agent runtime (shared secret from a mounted file), binds every session at
`initialize` to one attempt (attempt identifier and fencing token headers) after confirming it through the platform's
attempt-scoped pinned-artifact read, from which it also takes the session's tool policy, and exposes exactly the eleven
tools of `contracts/agent-tools/tool-allowlist/v1` with input and output schemas taken from the platform's OpenAPI
description through a committed, generated tool catalog that a conformance test keeps equal to both sources. Each
permitted call is validated against its schema, given a fresh call identifier, and forwarded as one DPoP-authenticated
request with the server's own workload client to its `agentTool*` operation; the platform's body or problem is
returned unchanged. The server persists, caches, and logs no evidence, exposes `/alive` and `/health`, reads its
configuration from environment variables and files, and ships a Dockerfile that follows the platform image rules. See
[research.md](research.md).

## Technical Context

**Language/Version**: Python 3.12 (CPython 3.12.15 in verification; the base image's CPython 3.12 in the image).

**Primary Dependencies**: `mcp` 2.3.0 (official MCP Python SDK) with its Starlette, uvicorn, `httpx2`, `jsonschema`,
`pydantic`, and `pyjwt[crypto]` dependencies, declared directly where the server imports them; `hatchling` and `pytest`
in the locked `dev` group. All locked in `src/agents/mcp-server/uv.lock`.

**Storage**: N/A (no persistence, no files; in-memory sessions and workload token only).

**Testing**: pytest in the project (in-process fake platform over an ASGI transport, MCP client over an ASGI transport,
catalog conformance against the repository's contract and OpenAPI snapshot, log redaction, configuration, health,
Dockerfile rules); Docker-backed image evidence in `src/platform/Tests/SocAlytics.Platform.Images.Tests`
(`McpServerImageTests.cs`).

**Target Platform**: Linux containers inside a stamp, next to the platform API; developer machines for local runs.

**Project Type**: Web service (MCP server, separate deployable of the intelligence plane).

**Performance Goals**: One platform request per tool call with no added work beyond schema validation and proof
signing; readiness within 10 seconds of start when the platform is reachable (SC-005).

**Constraints**: Read-only forwarding of the eleven allowlisted tools only; no evidence derivation, caching, or
persistence; no evidence, links, tokens, or secrets in logs; only the agent runtime as client; only the token,
pinned-artifact, and agent tool operations as outbound calls; production values open.

**Scale/Scope**: One server per stamp; up to 32 concurrent sessions (development); eleven tools; one new project and
image; one image test class.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | Implements [MCP Layer](../../docs/architecture/intelligence-and-agents.md#mcp-layer) and the server side of [MCP Tools](../../docs/architecture/intelligence-and-agents.md#mcp-tools); decisions not yet stated there (separate Python OCI service, Streamable HTTP, runtime secret, per-attempt sessions confirmed through the pinned-artifact read, own workload client, faithful forwarding) are applied now as decided but not implemented (see Architecture Updates). | Pass |
| II. Respect Source-Area Ownership | The server is intelligence-plane tooling and lives in `src/agents/mcp-server/`, its first executable project, as `src/agents/README.md` and [Source and Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline) assign; only the image evidence is added to the platform's existing image test project. No new first-level area. | Pass |
| III. API-First Control Plane | The server reaches evidence only through `/api/v1` operations in the OpenAPI document and never touches databases, object storage, or transport; its tool schemas come from the authoritative allowlist contract and OpenAPI description; it relays links, never media bytes. | Pass |
| IV. Evidence Over Claims | Project tests and image tests cover FR-020; the deferred cross-process test is recorded as open evidence (research R10), and passing tests are not presented as Hermes support or production readiness. | Pass |
| V. Focused, Minimal Changes | One project whose runtime dependencies are the SDK and libraries the SDK already brings; no new platform code, operation, or contract; one test class in an existing test project. | Pass |
| Technology and Tooling | CPython 3.12.15 and uv 0.13.1 come from the merged Analyst and Media Verification Environment; verification of `src/agents/<name>/` projects comes from [Agents Python Verification Environment](../20261018-110526-agents-python-environment/spec.md), named as a dependency that must merge first; the image test needs .NET 10 and Docker, which the platform verification provides. | Pass |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Architecture Updates

**Narratives** (implemented or changed by this feature):

- [Intelligence and Agents → MCP Layer](../../docs/architecture/intelligence-and-agents.md#mcp-layer): the MCP server
  as a separate deployable, its clients, sessions, and outbound calls.
- [Intelligence and Agents → MCP Tools](../../docs/architecture/intelligence-and-agents.md#mcp-tools): the MCP side of
  the one-to-one tool mapping.
- [Intelligence and Agents → Authorization, Lineage, And Lifecycle](../../docs/architecture/intelligence-and-agents.md#authorization-lineage-and-lifecycle):
  MCP's refusal of tools outside the pinned policy; the API stays authoritative.
- [Intelligence Agent Catalog → MCP Tool Policy](../../docs/architecture/intelligence-agent-catalog.md#mcp-tool-policy):
  the MCP server's enforcement of the pinned tool policy.
- [Platform Implementation Profile → Source and Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline)
  and [Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime): `src/agents/mcp-server` as the
  first executable intelligence-plane project.
- [Tenancy and Technology → AI](../../docs/architecture/tenancy-and-technology.md#ai): the MCP server technology.
- [Architecture Overview](../../docs/architecture/overview.md) (current-state summary and Intelligence Plane).
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md): `GOV-CRED-003` (the server's
  own workload client) and `BND-006`/`CTL-007` (MCP boundary).

**Design updates** (applied now to the owning narratives, worded as decided but not yet implemented; the
implementation turns each into present tense)

1. `intelligence-and-agents.md` MCP Layer: a paragraph on the MCP server: a separate Python OCI service in
   `src/agents/mcp-server` on the official MCP Python SDK, Streamable HTTP inside the stamp only, the agent runtime
   authenticated by a configured shared secret, one session per attempt bound by attempt identifier and fencing token
   and confirmed through the attempt-scoped pinned-artifact read that also supplies the session's tool policy, its own
   workload client with DPoP-bound tokens, one forwarded request per call with a server-generated call identifier,
   the platform's answers returned unchanged, no evidence caching, persistence, or logging, and `/alive` and `/health`.
2. `intelligence-and-agents.md` MCP Tools, last paragraph: "The MCP server process that exposes these operations as MCP
   tools is future work." → the server's tools are generated from the allowlist contract and the API description and
   mirror the operations one-to-one (decided, not yet implemented).
3. `intelligence-and-agents.md` Authorization, Lineage, And Lifecycle, evidence tool paragraph: the MCP server also
   refuses tools outside the pinned policy before forwarding, as defense in depth; the API remains authoritative.
4. `intelligence-agent-catalog.md` MCP Tool Policy: the MCP server exposes only the pinned policy's tools (decided, not
   yet implemented).
5. `platform-implementation.md` Agent Runtime: a paragraph on the MCP server as a separate Python OCI deployment under
   `src/agents/mcp-server` with no database access, authenticated as its own stamp-local workload client.
6. `tenancy-and-technology.md` AI: "MCP server as a separate Python OCI service on the official MCP Python SDK" next to
   the Hermes line (decided, not yet implemented).
7. `security-and-data-governance.md` `GOV-CRED-003`: the MCP server's own workload client and the agent runtime's
   shared secret for the MCP server are decided development scope with production custody, rotation, and lifetime
   unresolved.

**Status statements at merge** (each must become true for the merged state)

- `intelligence-and-agents.md` MCP Tools, `get_video_clip()` bullet: "… the MCP tool is future work" (or the wording the
  Agent Evidence Tools API leaves) → the MCP tool exists; only Hermes calling it remains future work.
- `intelligence-and-agents.md` MCP Tools, last paragraph and design updates 1–3: lose "future work" and "decided, not
  yet implemented" for the MCP server.
- `analyst-capability-catalog.md` evidence clips paragraph: "… the MCP tool remains future work." → the MCP tool exists.
- `analysts-models-and-hardware.md` Capability-Scoped Result Access: "This is the API side of `get_video_clip()`; the
  MCP tool remains future work." → the MCP server exposes it.
- `intelligence-agent-catalog.md` introduction: "… MCP evidence tools, Hermes orchestration runtime, … are likewise
  design inputs" (as left by the earlier agent features) → the MCP server exists as development evidence; Hermes,
  model execution, and user experiences remain design inputs. MCP Tool Policy: design update 4 loses "decided, not
  yet implemented".
- `platform-implementation.md` Source and Runtime Baseline: "`src/agents` owns future intelligence-agent runtimes and
  agent-specific integration code" → `src/agents` owns intelligence-agent runtimes and integration code; the MCP
  server is executable under `src/agents/mcp-server`, Hermes remains future.
- `platform-implementation.md` Agent Runtime: "… MCP tools … remain future implementation work" (as left by the Agent
  Evidence Tools API) → the MCP server exists; prompt and profile rendering, model execution, Hermes, and Web UI agent
  workflows remain future work; design update 5 loses "decided, not yet implemented".
- `overview.md` current-state summary: "… Python Analyst Containers, and Hermes as a separate Python OCI service are
  planned." → names the MCP server as an implemented separate Python service (development evidence) and keeps Hermes
  planned.
- `tenancy-and-technology.md` AI: design update 6 loses "decided, not yet implemented".
- `security-and-data-governance.md` `GOV-CRED-003`: keep the production values unresolved; the development scope
  becomes implemented. `CTL-007` (Provisional), `THR-007`/`GOV-THR-007` (Open / Blocking), `BND-006`: keep their
  decision status; confirm at merge that nothing claims the MCP boundary is unimplemented.
- `production-operations.md` gate 7 ("… Hermes/MCP … pass their health and readiness checks"): confirm it still holds
  now that the server has `/alive` and `/health`.
- `terminology-and-principles.md` (MCP Server in the agent diagram) and `README.md`: confirm that no statement calls
  the MCP server planned or future.
- Outside `docs/architecture/` (kept current by the same documentation task): `src/agents/README.md` ("This scaffold
  contains no services … This directory is non-executable") → names `mcp-server` as its first executable project with
  its commands; `README.md` and `AGENTS.md` "Current State" and "Repository Setup" name the server's commands and
  development evidence and keep Hermes among what does not exist.

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the listed
narratives and every status statement this feature affects in `docs/architecture/` true for the merged state,
re-reading the documents at implementation time (including `README.md`, `overview.md`,
`terminology-and-principles.md`, and any other planned, future-tense, or "not implemented" wording about the MCP
server, MCP tools, or `src/agents`); the rewrites listed above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261018-111804-mcp-server/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── mcp-server.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/agents/
├── README.md                                   # changed: first executable project
└── mcp-server/                                 # new independent uv project
    ├── pyproject.toml                          # socalytics-mcp-server; mcp==2.3.0; hatchling, pytest in dev group
    ├── uv.lock
    ├── README.md                               # commands, configuration, image build
    ├── Dockerfile
    ├── Dockerfile.dockerignore
    ├── docker/uv-requirements.txt              # uv 0.13.1 with per-wheel hashes
    ├── tools/generate_tool_catalog.py          # allowlist + OpenAPI -> tool_catalog.json
    ├── src/socalytics_mcp_server/
    │   ├── __init__.py
    │   ├── __main__.py                         # configuration, logging, uvicorn
    │   ├── configuration.py                    # SOCALYTICS_MCP_* variables and files, validation
    │   ├── logging.py                          # JSON records with the permitted keys only
    │   ├── tool_catalog.json                   # generated package data
    │   ├── catalog.py                          # loads the catalog, schema validators
    │   ├── platform/
    │   │   ├── proofs.py                       # client assertion and DPoP proofs (ES256)
    │   │   ├── tokens.py                       # workload token cache and renewal
    │   │   └── client.py                       # outbound path allowlist, forwarding, timeouts
    │   ├── sessions.py                         # runtime secret, binding, pinned tool policy, limits, idle timeout
    │   ├── tools.py                            # tools/list and tools/call handlers, results and tool errors
    │   └── app.py                              # ASGI app: /mcp, /alive, /health
    └── tests/
        ├── conftest.py                         # keys, configuration, in-process fake platform
        ├── fake_platform.py                    # ASGI fake: assertion and proof checks, OpenAPI-validated fixtures
        ├── test_catalog_conformance.py
        ├── test_sessions.py
        ├── test_tools_forwarding.py
        ├── test_tools_refusals.py
        ├── test_platform_credentials.py
        ├── test_logging_redaction.py
        ├── test_configuration.py
        ├── test_health.py
        └── test_dockerfile.py

src/platform/Tests/SocAlytics.Platform.Images.Tests/
└── McpServerImageTests.cs                      # new: build, read-only start, /alive, /health not ready, UID, labels, layer scan

README.md, AGENTS.md                            # commands and Current State
```

**Structure Decision**: One independent agent project as the environment convention defines it, with a src layout so
tests import the installed package. The tool catalog is generated into the package because the image contains no
repository files. Docker-backed image evidence joins the image test project of
[Cloud-Neutral OCI Images](../20261018-092503-oci-images/plan.md) instead of making the agent project's default test
run depend on Docker. This feature is implemented after the agents Python environment, the Agent Evidence Tools API
(its allowlist contract and refreshed OpenAPI snapshot), the Agent Catalog Registry (pinned-artifact read), the
Agent Orchestration invocations (workload token), and the OCI images feature (image test project) have merged.
