# Environment verify

`environment-verify` is the optional composite action the Spec Kit workflows run to verify a change. This page describes only what the action executes today. It is build-environment behavior, not product documentation, and it neither approves a product nor shows production readiness.

The action keeps its inputs (`workspace`, `mode`, `changed-files`, `log-file`) and outputs (`checks`, `uncovered`). It runs in a read-only job without token environment variables, because the checks execute agent-written code. Its header in [`action.yml`](action.yml) is the authoritative description of the platform, Analyst Manager, and Web UI checks; this page adds the Analyst Python check, the environment action tests, and the shared coverage rules.

## Analyst Python projects

No Analyst SDK or capability exists yet, so the check selects nothing and adds no coverage. The action creates no project, lock file, or declaration. It only verifies projects that later specs add, and this page advertises no product command for them.

### Project layout

Only these manifests are recognized, each as an independent project:

```text
src/analysts/sdk/pyproject.toml
src/analysts/capabilities/<name>/pyproject.toml
```

Each project needs:

- a unique normalized static `[project].name` and a `requires-python` that admits the installed interpreter (`SOCALYTICS_PYTHON`);
- its own committed `uv.lock`, generated from its committed declarations;
- a `[build-system]` with `requires` and `build-backend`, and no `[tool.uv].package = false`;
- pytest in its locked development dependencies and at least one collectable test.

The Analyst Manager (`src/analysts/manager/`), dependency environments (`.venv`, `venv`, `node_modules`, `site-packages`), generated output (`build`, `dist`, `__pycache__`), hidden folders, and symbolic links are never recognized as projects. A shared uv workspace (`[tool.uv.workspace]`, a `workspace` source) and an alternate interpreter are not supported.

### Local dependencies

A project may depend on another recognized project. It declares the dependency in its requirements and with a path-based `[tool.uv.sources]` entry. The path must resolve to a recognized project, stay inside the repository, and name the project that the source key names. A missing project, a path outside `src/analysts/sdk` and `src/analysts/capabilities/<name>`, a git, URL, or index source, a duplicate project name, and a dependency cycle each fail discovery. Selected projects run in dependency order, dependencies first.

### Locked backends and external wheels

- Every build backend that the project and its local dependencies need must appear in the project's locked development dependencies, because the build runs without isolation and cannot resolve a backend of its own.
- External dependencies must be available as compatible published wheels. The check never builds an external source distribution, and Python downloads are disabled (`UV_PYTHON_DOWNLOADS=never`).
- The check never changes a committed declaration or lock file.

### Command stages

Each selected project runs these stages in order, from its own folder, with `UV_PYTHON_DOWNLOADS=never` and `UV_PYTHON` set to `SOCALYTICS_PYTHON`. The first failing stage ends the check:

| Stage | Command |
| --- | --- |
| bootstrap sync | `uv sync --locked --all-groups --no-install-local --no-build` |
| project sync | `uv sync --locked --all-groups --no-build-isolation` |
| wheel build | `uv build --wheel --no-build-isolation --python .venv/bin/python` |
| tests | `uv run --no-sync pytest` |

The bootstrap sync installs locked external wheels and backends without building local packages. The project sync installs the project and its local dependencies using those backends. The tests stage runs the whole project in every mode, and pytest's no-tests exit fails it. The check requires `SOCALYTICS_PYTHON` and `uv` from [`environment-setup`](../environment-setup/action.yml) run with `install-tools: true`, and it fails otherwise instead of skipping.

### When it runs

[`analyst-projects.py`](analyst-projects.py) validates every existing project and selects projects by mode and changed paths, matching complete path components rather than name prefixes:

| Condition | Selected projects |
| --- | --- |
| `finalize` mode | every existing project |
| a change in either environment action folder | every existing project |
| a change in `contracts/analysis/` or `contracts/common/` | every existing project |
| a change in a project's own folder (`task` and `phase`) | that project and every project that depends on it, directly or transitively |
| unrelated changes, or no project exists | none |

Discovery always validates the existing manifests. A malformed or unsupported project fails the check with the project, the stage, and one recommended action, even when the changed files do not touch it.

## Environment action tests

The check "Environment action tests" runs `node --test` over [`analyst-projects.test.mjs`](analyst-projects.test.mjs) and [`../environment-setup/toolchain.test.mjs`](../environment-setup/toolchain.test.mjs). It runs when a file under `.github/actions/environment-setup/` or `.github/actions/environment-verify/` changed, and in `finalize` mode, whether or not an Analyst Python project exists. It uses the Node.js that the caller provides. The marker `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` makes a nested run of the action skip this check, so the test fixtures never start the self-test again. The tests stub external commands and never connect to a database. They use isolated fixtures, not product projects.

## Check order and coverage

Checks run in this order and stop at the first failing check: platform build and tests, Analyst Manager build and tests, Web UI build and tests, Analyst Python projects, Environment action tests, and Markdown check. The `checks` output lists the checks that ran, and after a failure the last one failed.

The `uncovered` output lists changed files that no check covers, and the workflows hold such changes for review. These paths are covered:

- Markdown files, `specs/`, and the repository metadata files `.gitignore`, `.gitattributes`, and `.editorconfig`;
- the two environment action folders, which the merge self-tests;
- `src/platform/` and `contracts/` while the platform solution exists, `src/analysts/manager/` while the Manager solution exists, and `src/clients/web/` while its `package.json` exists;
- the root of each existing recognized Analyst Python project.

The source of a deleted or unrecognized Analyst project stays uncovered, because no check can approve a project that does not exist. Contracts stay covered by the platform scope. Other paths under `src/analysts/` and `src/clients/` stay uncovered.

## Corrective actions

Every failure names the project or tool, the stage, and one recommended action in the log:

| Failure | Action |
| --- | --- |
| malformed manifest, unsupported layout, duplicate name, missing or cyclic local dependency | correct the named manifest to the project convention above, or author the required environment extension, then retry |
| `requires-python` excludes the installed interpreter | correct `requires-python` of the named project, or author the required environment extension, then retry |
| missing or stale `uv.lock`, missing external wheel, missing locked backend (a sync stage) | correct and commit the consuming project's declarations and lock; verification never repairs or unlocks them |
| failed wheel build | reproduce the command in the named project and correct the package or build configuration |
| failed or empty tests | reproduce the command in the named project and correct the tests or the source they cover without weakening them |
| missing `SOCALYTICS_PYTHON` or `uv` | run `environment-setup` with `install-tools: true`, then retry |
| failed environment action test | reproduce the `node --test` command, correct the action or its test without weakening the test, then retry |

Never bypass an integrity check, unlock the verification, weaken a test, or merge without passing evidence. An environment change goes through a standalone environment spec, as the header of [`action.yml`](action.yml) describes.
