# Research: Agents Python Verification Environment

**Date**: 2026-10-18

## Decision 1 - Reuse the installed toolchain; no new download

**Decision**: Verify agent projects with the CPython 3.12.15 interpreter (`SOCALYTICS_PYTHON`) and uv 0.13.1 that `environment-setup` already installs, verifies, and publishes. Add no pin, archive, cache, or setup step.

**Rationale**: The planned agent runtimes are Python projects with the same packaging needs as Analyst projects. The current PyPI metadata, checked on 2026-10-18, admits the installed interpreter for the planned frameworks: `agent-framework` 1.22.0 and `agent-framework-core` 1.22.0 declare `requires-python >=3.10`, and the MCP Python SDK `mcp` 2.3.0 declares `requires-python >=3.10`. A second interpreter or tool would add setup cost and a second integrity boundary without a need.

**Evidence boundary**: Only the published `requires-python` metadata was checked. Whether every locked transitive dependency of the MCP Server and Hermes ships compatible CPython 3.12 Linux x64 wheels is evidence those features must produce through this check; an incompatible need requires a later environment change, not an unlocked fallback.

**Alternatives considered**: A separate agents toolchain (another Python or uv version): no requirement calls for it. Reading agent project files in setup to choose versions: forbidden by the merged environment convention (setup is independent of project files).

## Decision 2 - Extend the existing helper with an area option

**Decision**: Add `--area analysts|agents` (default `analysts`) to `analyst-projects.py` instead of a second helper.

**Rationale**: Convention validation, runtime compatibility, dependency resolution, cycle detection, ordering, and path matching are identical; one implementation keeps them from drifting. The default keeps the Analyst invocation, output, and error texts unchanged, so the existing tests remain the regression evidence. The test file keeps its name, so `ENVIRONMENT_TESTS` in the action stays unchanged.

**Alternatives considered**: Renaming the helper to a neutral name: churn in the action, README, and tests with no behavioral gain. A copied `agent-projects.py`: duplicated rules that would diverge. One invocation returning both areas: couples the Analyst check's failure to agent manifests, which FR-013 forbids.

## Decision 3 - A separate check named `Agent Python projects`

**Decision**: Report agent verification as its own check, after `Analyst Python projects` and before `Environment action tests`.

**Rationale**: The `checks` output names the last check that ran after a failure; a separate name tells maintainers and the diagnosis which area failed, and keeps the existing check order as a prefix of the new order. Fail-fast semantics are unchanged: a failing earlier check stops later checks, as for every existing check.

**Alternatives considered**: Folding agent projects into the Analyst check: hides the failing area and changes that check's selection.

## Decision 4 - Agent contract triggers by folder family

**Decision**: A change under any folder directly below `contracts/` whose name starts with `agent-`, or under `contracts/common/`, selects every existing agent project (clarification of 2026-10-18).

**Rationale**: The agents consume `contracts/agent-catalog/` ([Agent Catalog Registry](../../20261018-101517-agent-catalog-registry/plan.md)), `contracts/agent-tools/` ([Agent Evidence Tools](../../20261018-104924-agent-evidence-tools/plan.md)), and `contracts/agent-orchestration/` ([Agent Orchestration Invocations](../20261017-204332-agent-orchestration-invocations/spec.md)); a later agent contract family should not need an environment change. The match is on the first folder name below `contracts/` (`agent-` prefix), so `contracts/agentic/` or a file named `contracts/agent-x.md` at the root of `contracts/` does not match. `contracts/analysis/` stays an Analyst trigger only.

**Alternatives considered**: An explicit folder list: every new agent contract family would need an environment spec. All of `contracts/`: needless agent runs for Analyst Manager and recording contracts.

## Decision 5 - No cross-area local dependencies; unique names across areas

**Decision**: Agent projects may depend only on agent projects. The agents area fails when an agent project name repeats an Analyst project name; the Analyst area does not read agent manifests.

**Rationale**: The architecture separates the Analyst runtime (Analyst SDK and capabilities in Analyst containers) from the intelligence plane, which consumes platform APIs and MCP ([Intelligence and Agents](../../../docs/architecture/intelligence-and-agents.md)). No planned agent needs Analyst code. Unique names keep a project identity unambiguous in logs and diagnoses; reporting the collision only in the newer area keeps the Analyst check unchanged.

**Alternatives considered**: Allowing agents to depend on the Analyst SDK: no consumer needs it, and it would make Analyst SDK changes select agent projects. Reporting duplicates in both checks: changes the Analyst check's behavior because of agent files.

## Decision 6 - No credentials, providers, or network isolation

**Decision**: The agent block sets only `UV_PYTHON_DOWNLOADS=never` and `UV_PYTHON`, like the Analyst block; it supplies no provider configuration or secret and adds no network isolation (clarification of 2026-10-18). The README states that an agent project's default test run must not depend on network services, model providers, or secrets.

**Rationale**: The verification job already runs without tokens; network isolation would be new infrastructure outside the two action folders' scope. OpenAI and GitHub Models access belong to Hermes configuration, not to verification.

## Evidence Boundary

The PyPI metadata above was retrieved during authoring. No tool was installed, and no fixture or verification was executed in this planning step. The implementation must pass the extended environment action tests and the environment self-verification before merge.
