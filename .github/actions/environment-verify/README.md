# Environment verify

`environment-verify` is the optional composite action the Spec Kit workflows run to verify a change. This page describes only what the action executes today. It is build-environment behavior, not product documentation, and it neither approves a product nor shows production readiness.

The action keeps its inputs (`workspace`, `mode`, `changed-files`, `log-file`) and outputs (`checks`, `uncovered`, `flaky`). It runs in a read-only job without token environment variables, because the checks execute agent-written code. Its header in [`action.yml`](action.yml) is the authoritative description of the platform, Analyst Manager, and Web UI checks; this page adds the Analyst Python check, the Agent Python check, the environment action tests, the rerun of failed tests, and the shared coverage rules.

## Rerun of failed tests

Every .NET test project runs with a TRX log (`--logger trx --results-directory <temporary folder>`), in task mode with the task's test filter. When a project fails, the action reads the fully qualified names of its failed tests from the log (the `className` and `name` of each failed test's definition). When at most 10 tests failed, only those run once more with `dotnet test <project> --no-build --filter "FullyQualifiedName=A|FullyQualifiedName=B"` (filter special characters escaped). When that rerun passes, the project passes and the tests are recorded as flaky; otherwise the project fails as before. More than 10 failed tests, or a log that names none, fail the project without a rerun. A failing project still does not stop the later projects.

The Web UI browser tests run as `npm run test:e2e -- --retries=1`, so Playwright retries a failed test once; the tests that its list or line reporter shows under `N flaky` are recorded as flaky.

The `flaky` output lists the recorded tests once each, comma-separated (a comma inside a name becomes a semicolon), and the log and the step summary list them too. The Spec Kit workflows show them in their verification comments and track tests that are flaky in several implementations.

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

The Analyst Manager (`src/analysts/manager/`), dependency environments (`.venv`, `venv`, `node_modules`, `site-packages`), generated output (`build`, `dist`, `__pycache__`), hidden folders, and symbolic links are never recognized as projects and are not searched. Any other `pyproject.toml` under `src/analysts/` (a manifest nested in a capability, or one in an unrecognized folder such as `src/analysts/<other>/`) fails discovery as an unsupported layout at the `project convention` stage, naming the manifest, so it cannot silently pass finalize verification; its source stays uncovered. A shared uv workspace (`[tool.uv.workspace]`, a `workspace` source) and an alternate interpreter are not supported.

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

## Agent Python projects

The check "Agent Python projects" verifies agent projects with the same helper, convention, and command stages as the Analyst check, run separately as its own check. No agent project exists yet, so the check selects nothing and adds no coverage. The action creates no project, lock file, or declaration, and this page advertises no product command for an agent project.

### Agent project layout

Only this manifest is recognized, for each immediate child of `src/agents/`, as an independent project:

```text
src/agents/<name>/pyproject.toml
```

Each project follows the [Analyst project convention](#project-layout): a unique static `[project].name`, a `requires-python` that admits `SOCALYTICS_PYTHON`, its own committed `uv.lock`, a `[build-system]` with `requires` and `build-backend` and no `[tool.uv].package = false`, locked backends and pytest, published wheels for external dependencies, and at least one collectable test. Hidden folders, symbolic links, and the dependency and generated folders `.venv`, `venv`, `node_modules`, `__pycache__`, `.git`, `build`, `dist`, and `site-packages` are not recognized and not searched. `src/agents/pyproject.toml` and any manifest nested below a project fail discovery as an unsupported layout at the `project convention` stage, naming the manifest, and their source stays uncovered. A shared uv workspace and a git, URL, or index source are not supported.

A default test run of an agent project needs no network service, model provider, credential, or secret, and verification supplies none: only `UV_PYTHON_DOWNLOADS=never` and `UV_PYTHON` are set.

### Agent dependencies and names

An agent project may depend only on another recognized agent project, declared in its requirements and with a path-based `[tool.uv.sources]` entry that resolves under `src/agents/<name>`, stays inside the repository, and names the project that the source key names. A dependency on an Analyst project fails discovery in the agents area; an Analyst project's dependency on an agent project still fails in the Analyst area. The agents area reads only the static `[project].name` of the Analyst projects and fails an agent project whose normalized name repeats one, naming both projects, so names are unique across both areas. The Analyst check's output and failures do not change while agent projects exist. Selected projects run in dependency order, dependencies first, and the log lines are `Agent Python project: <root>`, `Agent Python stage: <stage> (<root>)`, and on failure `Agent Python stage failed: <stage> in <root> (command: <command>). Recommended action: <action>`.

### When the agent check runs

The stages are those of the [Analyst check](#command-stages), run from each selected agent project's folder. `analyst-projects.py select --area agents` validates every existing agent project and selects projects by mode and changed paths, matching complete path components:

| Condition | Selected agent projects |
| --- | --- |
| `finalize` mode | every existing agent project |
| a change in either environment action folder | every existing agent project |
| a change in a folder directly under `contracts/` whose name starts with `agent-`, or in `contracts/common/` | every existing agent project |
| a change in an agent project's own folder (`task` and `phase`) | that project and every agent project that depends on it, directly or transitively |
| `contracts/agentic/`, a root file such as `contracts/agent-x.md`, `contracts/analysis/`, Analyst, Manager, or unrelated changes, or no agent project exists | none |

Discovery always validates the existing agent manifests. A malformed or unsupported agent project fails the check in every mode with the project, the stage, and one recommended action, even when the changed files do not touch it, and then no agent root is covered. Agent-only changes do not run the Analyst check and Analyst-only changes do not run the agent check.

## Environment action tests

The check "Environment action tests" runs `node --test` over [`analyst-projects.test.mjs`](analyst-projects.test.mjs) and [`../environment-setup/toolchain.test.mjs`](../environment-setup/toolchain.test.mjs). It runs when a file under `.github/actions/environment-setup/` or `.github/actions/environment-verify/` changed, and in `finalize` mode, whether or not an Analyst Python project exists. It uses the Node.js that the caller provides. The marker `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` makes a nested run of the action skip this check, so the test fixtures never start the self-test again. The tests stub external commands and never connect to a database. They use isolated fixtures, not product projects.

## Check order and coverage

Checks run in this order and stop at the first failing check: platform build and tests, Analyst Manager build and tests, Web UI build and tests, Analyst Python projects, Agent Python projects, Environment action tests, and Markdown check. The `checks` output lists the checks that ran, and after a failure the last one failed.

The Markdown check runs when a Markdown file changed and in `finalize` mode. In `task` mode (a task or a parallel group) it checks only the changed Markdown files (`check-markdown.mjs --files`), so a broken file elsewhere on the base branch does not fail the task; `phase` and `finalize` check every Markdown file of the repository.

The `uncovered` output lists changed files that no check covers, and the workflows hold such changes for review. These paths are covered:

- Markdown files, `specs/`, and the repository metadata files `.gitignore`, `.gitattributes`, and `.editorconfig`;
- the two environment action folders, which the merge self-tests;
- `src/platform/` and `contracts/` while the platform solution exists, `src/analysts/manager/` while the Manager solution exists, and `src/clients/web/` while its `package.json` exists;
- the root of each existing recognized Analyst Python project;
- the root of each existing valid agent Python project (`src/agents/<name>`); `src/agents/README.md` is covered only by the Markdown rule.

The source of a deleted or unrecognized Analyst or agent project stays uncovered, because no check can approve a project that does not exist; this includes `src/agents/pyproject.toml`, nested agent manifests, and unowned files directly in `src/agents/`. Contracts stay covered by the platform scope, and the `contracts/agent-*` and `contracts/common/` triggers run the agent check without covering those files. Other paths under `src/analysts/`, `src/agents/`, and `src/clients/` stay uncovered.

## Corrective actions

Every failure names the project or tool, the stage, and one recommended action in the log. The table applies to Analyst and agent projects alike, with the agent additions below it:

| Failure | Action |
| --- | --- |
| malformed manifest, unsupported layout, duplicate name, missing or cyclic local dependency | correct the named manifest to the project convention above, or author the required environment extension, then retry |
| `requires-python` excludes the installed interpreter | correct `requires-python` of the named project, or author the required environment extension, then retry |
| missing or stale `uv.lock`, missing external wheel, missing locked backend (a sync stage) | correct and commit the consuming project's declarations and lock; verification never repairs or unlocks them |
| failed wheel build | reproduce the command in the named project and correct the package or build configuration |
| failed or empty tests | reproduce the command in the named project and correct the tests or the source they cover without weakening them |
| missing `SOCALYTICS_PYTHON` or `uv` | run `environment-setup` with `install-tools: true`, then retry |
| failed environment action test | reproduce the `node --test` command, correct the action or its test without weakening the test, then retry |

Agent projects add these corrective actions at the `project convention` stage:

| Failure | Action |
| --- | --- |
| agent manifest or local dependency outside the convention | correct the named manifest to the agent project convention, or author the required environment extension, before retrying |
| local dependency on an Analyst project | remove the dependency on the Analyst project or move the shared code into an agent project under `src/agents/<name>`, before retrying |
| unsupported agents manifest location (`src/agents/pyproject.toml`, a nested manifest) | move the project to `src/agents/<name>`, or author the required environment extension, before retrying |

Never bypass an integrity check, unlock the verification, weaken a test, or merge without passing evidence. An environment change goes through a standalone environment spec, as the header of [`action.yml`](action.yml) describes.
