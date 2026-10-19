# Implementation Plan: Agents Python Verification Environment

**Branch**: `main` (authoring) | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261018-110526-agents-python-environment/spec.md`

## Summary

Extend only the verification action so independent, locked Python projects directly under `src/agents/<name>/` are recognized, selected, built, and tested like the Analyst Python projects, as a separate check named `Agent Python projects`. Reuse the merged environment's CPython 3.12.15 and uv 0.13.1 toolchain, its project convention, and its four-stage locked command sequence; add no download, pin, cache, or setup step. Keep every existing check, its log lines, the action inputs and outputs, and the trusted cache boundaries unchanged.

No agent project, workflow, contract, credential, or product behavior is introduced.

## Technical Context

**Language/Version**: Existing composite-action YAML and Bash; the existing standard-library Python helper `analyst-projects.py`, run with the installed CPython 3.12.15 (`SOCALYTICS_PYTHON`); Node.js 24 built-in tests, supplied by the caller.

**Primary Dependencies**: The pinned CPython 3.12.15 and uv 0.13.1 from [the merged environment feature](../20261017-163801-analyst-media-environment/research.md), unchanged. No new tool, package, or archive.

**Storage**: Runner-local project dependency environments and build output only, as for Analyst projects. No database, contract, or cache.

**Testing**: Node's built-in test runner over the extended `analyst-projects.test.mjs` with isolated fixture workspaces and stub `uv`; the existing real-`uv` fixture case extended to an agents-area package when a real `uv` is available; the existing merge-time environment self-verification.

**Target Platform**: The existing `ubuntu-latest` Linux x64 verification job; no new host support.

**Project Type**: Build and verification environment extension, not a product application.

**Performance Goals**: Each selected agent project is checked once; task and phase mode check only affected agent projects; finalize checks all. With no agent project, the new check adds one helper invocation and no command.

**Constraints**: Implementation may change only `.github/actions/environment-setup/` and `.github/actions/environment-verify/` and its own task markers. No new download, pin, Python auto-download, lockfile regeneration, credential, network isolation, workflow, or cache. The Analyst check's selection, messages, coverage, and failures stay byte-for-byte compatible with its existing tests.

**Scale/Scope**: Two planned agent projects (the MCP Server and the Hermes agent runtime) and any later independent agent project or shared agent library directly under `src/agents/`.

## Constitution Check

| Gate | Before research | After design |
| --- | --- | --- |
| I. Architecture authority | Pass: follows the adopted `src/agents` ownership and the Python Hermes direction; no design changes | Pass: Architecture Updates is `None` with a reason; no status statement becomes false |
| II. Source-area ownership | Pass: no source directory is created; agent projects stay under `src/agents/` | Pass: recognized roots are immediate children of `src/agents/`; Analyst and agent ownership stay separate (no cross-area local dependencies) |
| III. API-first and contract authority | Pass: no product API or contract changes | Pass: agent contract changes trigger consumer checks; the platform contract tests keep owning contract coverage |
| IV. Evidence over claims | Pass: fixture and self-verification evidence required | Pass: every selection, failure, and regression case is an executable test; no product readiness is claimed |
| V. Focused changes | Pass: standalone environment feature | Pass: one helper extended through an area option, one action block, documentation in the same folders |
| Tooling prerequisite | Pass: this is the environment feature for the agents area | Pass: the existing actions already provide every tool this feature's tasks need (Bash, Node.js, the installed CPython and uv) |

No gate exception is needed.

## Architecture Updates

None: this standalone environment feature changes only the two environment action folders. Recognizing `src/agents/<name>/` Python projects in verification is environment-local executable guidance; it introduces no product component, topology, protocol, canonical contract, model provider, or production profile.

The architecture review checked [Source and Runtime Baseline](../../../docs/architecture/platform-implementation.md#source-and-runtime-baseline), [Intelligence and Agents](../../../docs/architecture/intelligence-and-agents.md) (Hermes as a separate Python OCI service; the MCP server process as future work), [Analytics](../../../docs/architecture/tenancy-and-technology.md#analytics), and [Contracts and Compatibility](../../../docs/architecture/contracts-and-compatibility.md). The statements that `src/agents` owns future intelligence-agent runtimes, that the MCP server process is future work, and that Hermes is planned remain true when tooling alone merges; [src/agents/README.md](../../../src/agents/README.md) stays accurate because the area remains non-executable. The Engineering Baseline line of [Tenancy and Technology](../../../docs/architecture/tenancy-and-technology.md#engineering-baseline) that names `uv` workspaces predates this feature and is not made false by it: like the merged Analyst convention, agent projects stay independently locked, and no status statement changes.

No architecture file changes and no `docs/architecture/` documentation task are required. The MCP Server and Hermes Agent Runtime features correct their own status statements and the agents README when they add the first projects.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261018-110526-agents-python-environment/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/environment-interface.md
├── checklists/requirements.md
└── tasks.md              # created by /speckit-tasks
```

The contract documents the verification interface and the agent project convention; it is not a canonical product schema.

### Implementation Allow-List

```text
.github/actions/environment-verify/
├── action.yml                    # new "Agent Python projects" block; header and extension comment
├── analyst-projects.py           # --area analysts|agents (default analysts, unchanged behavior)
├── analyst-projects.test.mjs      # agent discovery, selection, execution, isolation, regression fixtures
└── README.md                     # agent project convention, selection, coverage, corrective actions
.github/actions/environment-setup/
└── README.md                     # one statement that the same interpreter and uv serve agent projects
```

No other file in either folder changes. `action.yml`, `install-analyst-media.sh`, `analyst-media-toolchain.json`, `cache-keys.mjs`, and `toolchain.test.mjs` of the setup action stay unchanged.

**Structure Decision**: Extend the existing helper and its test file instead of adding parallel files, so the discovery, convention, and dependency rules stay one implementation and the environment action tests keep running the same two test files (`ENVIRONMENT_TESTS` unchanged).

## Design Decisions

### One helper, two project areas

`analyst-projects.py` gains `--area analysts|agents`, default `analysts`. With the default, its behavior, output, and error texts are unchanged. The `agents` area recognizes `src/agents/<name>/pyproject.toml` for every immediate child that is not hidden, not in the ignored directory set, and not a symbolic link. Any other `pyproject.toml` under `src/agents/` (the area root or nested below a project) fails discovery as an unsupported layout with an agents-area recommended action. Project convention validation, locked-lock presence, runtime compatibility, duplicate identities, cycles, ordering, and component-wise path matching reuse the existing functions.

Local path dependencies of an agent project must resolve to a recognized agent root. A target under `src/analysts/` fails with a specific message that agent projects may not depend on Analyst projects; other targets fail as outside the permitted roots. The Analyst area keeps rejecting every target outside its own roots, which already includes agent projects. Rationale: the Analyst SDK serves Analyst containers, while agents consume platform APIs and MCP; no planned agent needs Analyst code, and a cross-area dependency would couple separate runtimes without an architecture decision.

The `agents` area also loads the names of the Analyst projects (reading only their static `[project].name`) and fails when an agent project repeats one, naming both projects. An Analyst manifest without a readable static name is skipped for this comparison, because the Analyst check reports it. The Analyst area does not read agent manifests, so the Analyst check is unchanged by agent projects.

### Selection and coverage

For the `agents` area: finalize selects every project; a change in either environment action folder selects every project; a change in a folder directly under `contracts/` whose name starts with `agent-`, or in `contracts/common/`, selects every project; otherwise own-source changes select the project and its reverse local-dependency closure. `contracts/analysis/` and other contract folders select no agent project. The covered roots are the existing valid agent projects; nothing else under `src/agents/` is covered.

### Action integration

A new block runs after `Analyst Python projects` and before `Environment action tests`, with the check name `Agent Python projects`. Its discovery runs at the start of the script beside the Analyst discovery so its covered roots join `COVERED` even when an earlier check fails. It reuses the `python_project` stage list through a label parameter: the Analyst block keeps its exact `Analyst Python …` log lines, and the agent block logs `Agent Python project`, `Agent Python stage`, and `Agent Python stage failed: <stage> in <root> (command: …). Recommended action: …`. Missing `SOCALYTICS_PYTHON` or `uv` and helper failures produce the same single recommendation, with the agent label. Fail-fast order, `status`, the summary table, and the `checks` and `uncovered` outputs keep their semantics.

The stages, commands, and environment are identical to the Analyst stages (see [environment-interface.md](contracts/environment-interface.md)). The block sets no credential, provider, or network variable.

### Tests and documentation

Extend `analyst-projects.test.mjs` with agents-area cases for discovery, validation, cross-area isolation and duplicates, selection triggers including `contracts/agent-*` and the `contracts/agentic` non-match, coverage, every failing stage through the action with stub `uv`, the check order, and the absent-project state; keep every existing case unchanged. Extend the real-`uv` fixture case with an agents-area package under the same skip condition. Document the agent convention, the no-network and no-secret test expectation, selection, coverage, and corrective actions in the verify README, and one sentence in the setup README.

## Dependencies And Downstream Handoff

- [Analyst and Media Verification Environment](../20261017-163801-analyst-media-environment/spec.md) is implemented and provides everything this feature uses.
- The MCP Server and Hermes Agent Runtime features of this batch must depend on this feature and follow its agent project convention or amend this environment through a new environment spec.
- [Research](research.md) recorded that the planned agent frameworks admit the installed interpreter; their full locked dependency trees and wheel availability remain evidence the consuming features must produce.

## Complexity Tracking

No constitution violation needs justification.
