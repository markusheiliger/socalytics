# Quickstart: MCP Server

Validation guide for [spec.md](spec.md). Interfaces: [contracts/mcp-server.md](contracts/mcp-server.md); entities and
configuration: [data-model.md](data-model.md).

## Prerequisites

- CPython 3.12 and uv 0.13.1 (provisioned by the environment; see `AGENTS.md`).
- For the image evidence: the .NET 10 SDK and a Docker-compatible runtime with a Linux engine.
- The [Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/spec.md) merged (its allowlist contract and
  the refreshed OpenAPI snapshot exist).

## 1. Project checks (no network beyond locked dependencies)

From `src/agents/mcp-server/`, with the environment's locked sequence:

```powershell
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation
uv run --no-sync pytest
```

Expected: all tests pass, including the catalog conformance test (eleven tools equal to the allowlist and the API
description), forwarding and faithful results against the fake platform for every tool and problem, session
authentication and binding, local refusals with no platform request, the credential and proof flow, log redaction,
configuration, health, and the Dockerfile rules.

## 2. Regenerate the tool catalog after a contract or API change

```powershell
uv run python tools/generate_tool_catalog.py
```

Expected: `src/socalytics_mcp_server/tool_catalog.json` changes only when the allowlist or the agent tool operations
in `src/platform/SocAlytics.Platform.Api/openapi/v1.json` changed; the conformance test then passes again.

## 3. Run locally against a platform

Create a P-256 key and a runtime secret outside the repository, register the public JWK as an enabled workload client
in the API's `AgentOrchestration:Workloads:Clients`, then:

```powershell
$env:SOCALYTICS_MCP_PLATFORM_URL = 'https://localhost:<api port>'
$env:SOCALYTICS_MCP_WORKLOAD_CLIENT_ID = 'mcp-server'
$env:SOCALYTICS_MCP_WORKLOAD_KEY_FILE = '<path>\mcp-server-key.pem'
$env:SOCALYTICS_MCP_RUNTIME_SECRET_FILE = '<path>\runtime-secret'
uv run python -m socalytics_mcp_server
```

Expected: `GET http://localhost:8080/alive` answers `200`; `GET /health` answers `200` once the token is issued and
`503` when the client is disabled. Starting without `SOCALYTICS_MCP_WORKLOAD_CLIENT_ID` exits with code 2 naming it.

## 4. Image

From the repository root:

```powershell
$rev = git rev-parse HEAD
$ver = (Get-Date).ToUniversalTime().ToString('yyyy.MMdd.HHmmss')
docker build -f src/agents/mcp-server/Dockerfile --build-arg SOCALYTICS_REVISION=$rev --build-arg SOCALYTICS_VERSION=$ver -t socalytics-mcp-server:local .
dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests --filter McpServerImageTests
```

Expected: the build fails without either argument; the image tests pass (read-only start, `/alive`, `/health` not
ready without a platform, UID 65532, labels, no secrets in layers).

## 5. Documentation

```powershell
node .github/scripts/check-markdown.mjs
```
