# Quickstart: Hermes Agent Runtime

Validation guide for the feature. Commands run from the repository root; the agent project commands are those of the
[agent project convention](../archive/20261018-110526-agents-python-environment/contracts/environment-interface.md).

## Prerequisites

- CPython 3.12 and uv 0.13.1 (as provisioned by the environment actions), the .NET 10 SDK, Node.js 24, and a running
  Docker-compatible container runtime.
- The dependencies of this feature are merged: Agent Orchestration Direct Invocations, Agent Catalog Registry, Agent
  Evidence Tools API, MCP Server, Agents Python Verification Environment, and Cloud-Neutral OCI Images.

## 1. Hermes project tests (no network services, no secrets)

```powershell
cd src/agents/hermes
uv sync --locked --all-groups
uv run --no-sync pytest
```

Expected: all tests pass with the scripted provider, the fake platform, and the fake MCP server, covering work intake,
artifact validation, provider selection, rendering, the per-attempt MCP session, lease and checkpoints, completion
references, every failure reason of [data-model.md](data-model.md#failure-classification), stale dispositions,
shutdown, configuration, health, log redaction, contract copies, and the Dockerfile rules.

## 2. Platform contract and completion check

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
dotnet build src/platform/SocAlytics.Platform.slnx
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~AgentOrchestration"
```

Expected: the `attempt-report` 1.1.0 fixtures validate as listed in
[contracts/attempt-report-1.1.0.md](contracts/attempt-report-1.1.0.md); completions with a mismatched provider, model,
deployment, model-selection reference, or parameters are refused and change nothing; the owner's reads show the
resolved parameters.

## 3. Image and cross-process evidence

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests --filter "FullyQualifiedName~HermesImageTests|FullyQualifiedName~AgentRuntimeStackTests"
```

Expected: the Hermes image builds with the documented command, runs as UID 65532 on a read-only root filesystem,
answers `/alive`, reports `/health` not ready without a platform, carries the labels, and holds no secrets; the stack
test starts the API (HTTPS with a test CA), the MCP server, and Hermes with the scripted provider, and a Coach's
question to `specialist-tactics` succeeds within 60 seconds with one `get_match` tool-call entry in its lineage and
execution references equal to the pins.

## 4. Manual local run (optional)

Run the platform without the simulated agent workload (it consumes the same work queue), start the MCP server as its
README describes, then:

```powershell
cd src/agents/hermes
$env:SOCALYTICS_HERMES_ENVIRONMENT = 'Development'
$env:SOCALYTICS_HERMES_MODEL_PROVIDER = 'simulated'
$env:SOCALYTICS_HERMES_MODEL_SCRIPT_FILE = 'tests/scripts/get-match-then-answer.json'
# plus the platform, workload key, NATS, and MCP settings of data-model.md
uv run python -m socalytics_hermes
```

Using `openai` or `github-models` needs a credential file and sends conversation content to that provider; it is a
manual, explicitly selected choice and never part of automated tests.
