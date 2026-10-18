# Environment Interface Contract: Agent Python Projects

This documents build-environment expectations, not a public product API or a machine-readable root contract. It extends the [interface contract of the merged environment feature](../../archive/20261017-163801-analyst-media-environment/contracts/environment-interface.md), which stays authoritative for everything not restated here.

## Existing Composite Action Interface

- `environment-setup`: inputs, outputs, steps, pins, cache behavior, and tools-disabled mode are unchanged. `SOCALYTICS_PYTHON` and `uv` serve both project areas.
- `environment-verify`: inputs `workspace`, `mode`, `changed-files`, `log-file` and outputs `checks`, `uncovered` are unchanged. The check order becomes: platform build and tests, Analyst Manager build and tests, Web UI build and tests, Analyst Python projects, **Agent Python projects**, Environment action tests, Markdown check. The first failing check stops the later ones; outputs are always written.
- The Analyst check keeps its name, selection, coverage, log lines, and failures.

## Agent Project Convention

Recognized independent project manifests:

```text
src/agents/<name>/pyproject.toml
```

Each recognized project supplies everything the Analyst convention requires: a unique normalized static `[project].name` (unique across Analyst and agent projects), a `requires-python` admitting the installed interpreter, its own committed consistent `uv.lock`, a `[build-system]` with `requires` and `build-backend` (no `[tool.uv].package = false`), its build backends and pytest in locked development dependencies, compatible published wheels for external dependencies, and at least one collectable test.

Local dependencies, where present, are project requirements with path-based `[tool.uv.sources]` entries that resolve to another recognized agent project and match its name. No shared uv workspace, git, URL, or index source, and no Analyst target. A default test run needs no network service, model provider, credential, or secret; verification supplies none.

Unsupported: `src/agents/pyproject.toml`, manifests nested below a project, and manifests in hidden, ignored, or symbolic-link folders (the latter are not searched). An unsupported manifest that is searched fails discovery; its source stays uncovered.

## Helper Contract

```text
analyst-projects.py select --area agents --root <workspace> --mode task|phase|finalize --changed-files <file>
```

`--area` defaults to `analysts`, whose output and messages are unchanged. Output on success: JSON `{"projects": [...selected, dependency order, each with reason], "covered": [...existing valid roots]}`. On failure: one line per violation `project <path>: <stage>: <problem>. Recommended action: <action>` on stderr, exit 1.

## Verification Command Contract

Identical to the Analyst stages, run from each selected agent project root in dependency order with `UV_PYTHON_DOWNLOADS=never` and `UV_PYTHON=<SOCALYTICS_PYTHON>`:

| Stage | Command |
| --- | --- |
| bootstrap sync | `uv sync --locked --all-groups --no-install-local --no-build` |
| project sync | `uv sync --locked --all-groups --no-build-isolation` |
| wheel build | `uv build --wheel --no-build-isolation --python .venv/bin/python` |
| tests | `uv run --no-sync pytest` |

The first failing stage ends the check. Log lines: `Agent Python project: <root>`, `Agent Python stage: <stage> (<root>)`, and on failure `Agent Python stage failed: <stage> in <root> (command: <command>). Recommended action: <action>`.

## Selection Contract (agents area)

| Condition | Selected agent projects | Source coverage |
| --- | --- | --- |
| No recognized agent project | None; the check does not run | No `src/agents/` scope added |
| Own project change in task or phase | That project and its reverse local-dependency closure | Existing valid agent roots only |
| Change under `contracts/<agent-…>/` or `contracts/common/` | All existing agent projects | Contracts stay covered by the platform scope |
| Change under `contracts/analysis/` or another contract folder | None | Unchanged |
| Change in either environment action folder | All existing agent projects | No blanket source coverage |
| Finalize mode | All existing agent projects | Existing valid agent roots only |
| Deleted manifest, area-root or nested manifest, unowned file | No check approves it | Stays uncovered |
| Change only in an Analyst project | None | Unchanged |
| Any change, while an existing agent project is invalid | Discovery fails `Agent Python projects` regardless of the changed paths | No agent root is covered |

Paths match by complete components; `contracts/<folder>/` matches the `agent-` family only when the folder name itself starts with `agent-`.

## Failure Guidance Contract

| Failure | Stage | One recommended action |
| --- | --- | --- |
| Malformed manifest, duplicate name, missing, invalid, cyclic, or outside-root local dependency | `project convention` | Correct the named manifest to the agent project convention, or author the required environment extension, before retrying |
| Local dependency on an Analyst project | `project convention` | Remove the dependency on the Analyst project or move the shared code into an agent project under `src/agents/<name>`, before retrying |
| Unsupported agents manifest location | `project convention` | Move the project to `src/agents/<name>`, or author the required environment extension, before retrying |
| `requires-python` excludes the interpreter | `runtime compatibility` | Correct `requires-python`, or author the required environment extension, before retrying |
| Missing `uv.lock` | `locked sync` | Commit the project's own `uv.lock` generated from its committed declarations, before retrying |
| Sync, build, or test failure | the failing stage | The same actions as the Analyst stages: correct and commit declarations, or reproduce the command in the named project and correct its source without weakening tests |
| Missing `SOCALYTICS_PYTHON` or `uv`, helper not runnable | `environment setup` / `discovery` | Run `environment-setup` with `install-tools: true`, then retry verification |

Never recommend bypassing a check, unlocking the verification, weakening tests, or merging without passing evidence. These actions post no pull request comments and change no workflow handoff.
