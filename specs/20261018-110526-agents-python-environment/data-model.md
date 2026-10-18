# Data Model: Agents Python Verification Environment

These are runner-local verification records. There are no persistent domain entities, database migrations, or contract schemas.

## Project Area

| Area | Recognized manifests | Permitted local dependency targets | Shared contract triggers | Check |
| --- | --- | --- | --- | --- |
| `analysts` (unchanged) | `src/analysts/sdk/pyproject.toml`, `src/analysts/capabilities/<name>/pyproject.toml` | Analyst roots | `contracts/analysis/`, `contracts/common/` | `Analyst Python projects` |
| `agents` (new) | `src/agents/<name>/pyproject.toml` | Agent roots | `contracts/agent-*/`, `contracts/common/` | `Agent Python projects` |

Both areas: changes in either environment action folder and finalize mode select every existing project of the area. `<name>` is an immediate, non-hidden, non-symbolic-link folder that is not one of the ignored directories (`.venv`, `venv`, `node_modules`, `__pycache__`, `.git`, `build`, `dist`, `site-packages`).

## Agent Project

- Repository-relative root `src/agents/<name>` and its manifest.
- Normalized static project name, unique across both areas.
- `requires-python` admitting the installed interpreter; committed `uv.lock`; build system; locked backends and pytest in development dependencies.
- Declared local dependencies, each resolved to another recognized agent project.
- Owned source scope (its root) and its selection reason: `direct change`, `local-dependency change`, `shared contract change`, `environment change`, or `finalize`.

Lifecycle: absent → recognized → selected → bootstrap sync → project sync → wheel build → tests passed. A malformed, incompatible, duplicate, cyclic, or cross-area project fails discovery; it does not become absent. A deleted manifest removes its coverage.

## Unsupported Agents Manifest

Any `pyproject.toml` under `src/agents/` outside a recognized root and outside ignored, hidden, and symbolic-link folders: the area root manifest or a manifest nested below a project. Discovery fails naming it; its source stays uncovered.

## Stage Outcome

- Area label (`Analyst` or `Agent`), project or manifest, stage (`project convention`, `runtime compatibility`, `locked sync`, `bootstrap sync`, `project sync`, `wheel build`, `tests`, `environment setup`, `discovery`).
- Exit status and the existing log and summary evidence.
- Exactly one recommended corrective action on failure.

## Relationships

Both areas share the one installed interpreter and uv. Each area forms its own acyclic local-dependency graph; no edge crosses areas. One invocation checks each selected project at most once per area. Canonical contracts stay owned and covered by the platform contract tests.
