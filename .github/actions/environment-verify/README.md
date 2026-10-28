# Environment verify

`environment-verify` is the optional composite action the Spec Kit workflows run to verify a change. This page describes only what the action executes today. It is build-environment behavior, not product documentation, and it neither approves a product nor shows production readiness.

The action keeps its inputs (`workspace`, `mode`, `changed-files`, `log-file`) and outputs (`checks`, `uncovered`, `flaky`), and adds the output `plan`. It runs in a read-only job without token environment variables, because the checks execute agent-written code. Its header in [`action.yml`](action.yml) summarizes every check; this page describes the components and their declarations, documentation-only changes, the environment action tests, the rerun of failed tests, and the shared coverage rules.

## Components and declarations

[`verification-plan.mjs`](verification-plan.mjs) is the only selection logic. It writes the verification plan (JSON, the `plan` output, next to the log file) and the action executes it.

Components are discovered by convention under `src/` (hidden, dependency, and output folders such as `node_modules`, `bin`, and `obj` are skipped):

| Kind | Found by | Runs, from the component folder |
| --- | --- | --- |
| `dotnet-solution` | exactly one `*.slnx` or `*.sln` in a folder | `dotnet restore`, `dotnet build --no-restore`, then the test projects (`*Tests.csproj`) as [selected](#test-selection) |
| `npm-package` | `package.json` in a folder | `npm ci` without a browser download, the Playwright pin check when it uses `@playwright/test`, and its `build`, `test`, and `test:e2e` scripts when declared |
| Python projects | the [Analyst](#project-layout) and [agent](#agent-project-layout) conventions | the [command stages](#command-stages) |

Several solutions in one folder, or a solution and a `package.json` in one folder, fail the check "Verification plan". Other manifests (for example the Manager's `Package.swift`) are no component; their files are covered by the component that contains them.

A component is selected in finalize mode, when a changed path lies in its folder, a covered folder, or a trigger folder, or when its declaration changed. In task mode it tests the affected tests, otherwise all of them. Its folder and covered folders are covered; files no component, Python project, or static rule (Markdown, `specs/`, `.gitignore`, `.gitattributes`, `.editorconfig`, the two environment action folders) covers stay uncovered.

### Declarations

A component may declare more in a `verification.json` next to its manifest:

| Setting | Meaning |
| --- | --- |
| `version` | `1` (required) |
| `check` | Check name; `{tests}` becomes `affected tests` or `tests` (default: `<folder> build and {tests}`) |
| `order` | Check order, lower first (default 100; ties by folder) |
| `covers` | Folders under `src/` or `contracts/`, each ending with `/`, that select the component and that it covers |
| `triggers` | Folders under `src/` or `contracts/` that select the component without being covered by it |
| `alwaysRun` | .NET only: test project files, relative to the component folder, that every task runs in full |
| `sdkPinOf` | .NET only: a `global.json` whose `sdk` object the component's own `global.json` must equal |
| `browserTests` | npm only: the browser test folder relative to the component folder, ending with `/`, which enables [touched browser tests](#test-selection) in task mode |
| `budgetMinutes` | Estimated minutes of the component's tests outside its declared shards, used to pack [shards](#shards) (default 10) |
| `tools` | Tool groups the component needs: `dotnet`, `node`, `docker`, `softhsm`, `kiota`, `playwright`, `analyst-media`, `markdown` |
| `shards` | Named groups of test projects (`name`, `tests`, `runner` out of `linux-x64`, `linux-x64-gpu`, `windows-x64`, `macos-arm64`, `docker`, `budgetMinutes`); the remaining tests form the shard `default` |

Unknown settings and invalid values fail the check "Verification plan" with the file and the problem, and nothing else runs. Any change to a `verification.json` under `src/` (added, changed, or deleted) selects its component with all tests in every mode and stays uncovered, so the workflows hold it for a person's review: a declaration can narrow verification, so it never passes unreviewed. The repository declares [`src/platform`](../../../src/platform/verification.json), [`src/analysts/manager`](../../../src/analysts/manager/verification.json), and [`src/clients/web`](../../../src/clients/web/verification.json).

### Test selection

All tests (phase, finalize, or a changed declaration): every test project, one after another in sorted order, because Docker-backed projects must never share the one daemon. Affected tests (task): the `alwaysRun` projects; the test classes declared in the changed `.cs` files of a test project; the whole test project when other files of it changed. A test project the task did not touch is skipped; the phase verification and the merge run it.

An npm component always builds and runs all its unit tests (`test`). Its browser tests (`test:e2e`, with `--retries=1`) follow the same rule when it declares `browserTests`: with affected tests, only the existing spec files (`*.spec.ts` and similar) under that folder that the task changed run, passed to `npm run test:e2e -- --retries=1 <files>`, so Playwright also runs the projects they depend on; a task that changed no spec runs no browser test. A change to anything else under the folder (support files, fixtures), the Playwright configuration, `package.json`, or `package-lock.json` runs every browser test, as do all tests and a component without `browserTests`. The Web UI's simulated browser project takes about 88 % of its browser test time on its single worker, so this is the tier that keeps a task fast; the phase verification runs every spec before the phase ends.

### Shards

With the input `shard` (`<index>/<count>`, at most 20 shards), the action runs only one shard of the work, so a workflow can run the shards side by side on their own runners, each with its own Docker daemon. The plan packs the selected work deterministically: each declared shard of a .NET component with all tests is one unit (its `budgetMinutes`, default 10), the rest of each component another (the component's `budgetMinutes`, default 10; a component with affected tests stays one unit), and the units go, largest first, to the shard with the least estimated work so far. The first shard starts with 5 minutes for the work only it runs: the Python projects, the environment action tests, and the Markdown check. Every shard plans the same, so each test project runs in exactly one shard; a shard builds only the components it tests, and a shard without work passes without a check. An invalid shard input fails the check "Verification plan". The log names the shard's units (`Verification shard 2/4: src/platform#integration (25 min)`), and the plan lists every shard.

Tests of a shard declared for a `runner` other than `linux-x64` never run here, sharded or not: the log names them (`Not run: …`), and the component's `verification.json` stays uncovered, so the change is held for a person's review.

Spec Kit implement verify plans the phase verification's shards in a job of its own (`verification-plan.mjs --matrix 4`, which prints one matrix entry per shard with work, the first shard always), runs them as a matrix whose jobs GitHub names after their work, for example `Verify the phase (platform integration, 3/4)`, starts no runner for an empty shard, and combines the shards (see [its workflow](../../workflows/speckit-implement-verify.yml)); the task, merge, and conflict-resolution verifications still run in one job.

## Documentation-only changes

Before any check, [`verification-scope.mjs`](verification-scope.mjs) decides whether the change is documentation-only. When it is, every mode (task, phase, and finalize) runs only the Markdown check: task mode checks the changed Markdown files, phase and finalize mode the whole repository. No restore, build, test, Web UI step, Python project stage, or environment action test runs. Python project discovery still runs, so an invalid existing project still fails its check. The log and the step summary state the decision and its reason (`Verification scope: documentation-only: …` or `Verification scope: full: …`).

A change is documentation-only when every changed path ends in `.md` and none of those files may be read by a build, a package, or a test. A Markdown file counts as read when a file under `src/` or `contracts/`, or a file at the repository root, that is not itself Markdown:

- is a project file (`.csproj`, `.fsproj`, `.vbproj`, `.props`, `.targets`) with an `Include` or `Update` item that matches it, for example `contracts\**\*` copied into a test project's output;
- is a `pyproject.toml` with a quoted path or glob that names it or a folder containing it (`readme`, `packages`, `include`, a local path dependency);
- names its repository path when that path has a folder, also behind `../`, for example `"docs/guide.md"`;
- names its file name when no other Markdown file in the repository has that name, for example `RepositoryFile("docs", "architecture", "intelligence-agent-catalog.md")`;
- names its file name and lies in the same folder, for example a `readme = "README.md"`.

Files under `specs/`, `.specify/`, and `.github/`, and generated or installed folders (`node_modules`, `bin`, `obj`, `.venv`, `dist`, `build`, and similar), never count, because they neither build nor test the product. Comments count like code, so a mention can only keep the full verification, never skip it.

Anything uncertain keeps the full verification: no change list, an unreadable or empty change list, any path that is not Markdown (images, JSON, or `README.MD` included), or a Markdown file the rules find a reader for. A deleted Markdown file is judged by the references that remain. A test that reads a Markdown file by a file name shared with other Markdown files, from another folder and without its path, is not detected; name the path in such a test, or include the file through its project file.

## Rerun of failed tests

Every .NET test project runs with a TRX log (`--logger trx --results-directory <temporary folder>`), in task mode with the task's test filter. When a project fails, the action reads the fully qualified names of its failed tests from the log (the `className` and `name` of each failed test's definition). When at most 10 tests failed, only those run once more with `dotnet test <project> --no-build --filter "FullyQualifiedName=A|FullyQualifiedName=B"` (filter special characters escaped). When that rerun passes, the project passes and the tests are recorded as flaky; otherwise the project fails as before. More than 10 failed tests, or a log that names none, fail the project without a rerun. A failing project still does not stop the later projects.

The Web UI browser tests run as `npm run test:e2e -- --retries=1`, so Playwright retries a failed test once; the tests that its list or line reporter shows under `N flaky` are recorded as flaky.

The `flaky` output lists the recorded tests once each, comma-separated (a comma inside a name becomes a semicolon), and the log and the step summary list them too. The Spec Kit workflows show them in their verification comments and track tests that are flaky in several implementations.

After each .NET test project, the log lists its ten slowest tests from its TRX logs (`Slowest tests of <project>:`), and the step summary collects them in collapsed sections after the command timings, so phase and finalize runs show where the test time goes.

## Analyst Python projects

The check verifies the Analyst SDK (`src/analysts/sdk`) and every capability project under `src/analysts/capabilities/<name>` that exists, each as an independent project, and adds each existing project's root to the covered paths. It selects nothing and adds no coverage while no project exists. The action creates no project, lock file, or declaration, and this page advertises no product command for a project.

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

Each selected project runs these stages in order, from its own folder, with `UV_PYTHON_DOWNLOADS=never` and `UV_PYTHON` set to `SOCALYTICS_PYTHON`. The first failing stage ends the check. The `library pin` stage runs first, and only for Analyst projects (never for agent projects):

| Stage | Command |
| --- | --- |
| library pin | `python3 analyst-library-pins.py --declaration ../environment-setup/analyst-cv-libraries.json --lock uv.lock --project <root>` (with `SOCALYTICS_PYTHON`) |
| bootstrap sync | `uv sync --locked --all-groups --no-install-local --no-build` |
| project sync | `uv sync --locked --all-groups --no-build-isolation` |
| wheel build | `uv build --wheel --no-build-isolation --python .venv/bin/python` |
| tests | `uv run --no-sync pytest` |

The bootstrap sync installs locked external wheels and backends without building local packages. The project sync installs the project and its local dependencies using those backends. The tests stage runs the whole project in every mode, and pytest's no-tests exit fails it. The check requires `SOCALYTICS_PYTHON` and `uv` from [`environment-setup`](../environment-setup/action.yml) run with `install-tools: true`, and it fails otherwise instead of skipping.

### Library pin stage

[`analyst-library-pins.py`](analyst-library-pins.py) only reads the project's committed `uv.lock` and compares it with the single pinned declaration [`analyst-cv-libraries.json`](../environment-setup/analyst-cv-libraries.json) of [`environment-setup`](../environment-setup/README.md). It never writes a file and needs no network. A package matching the declaration's `deniedPackages` is rejected in every Analyst project. The whole pinned closure (the `library` wheels `opencv-python-headless`, `numpy`, and `onnxruntime`, and the `dependency` wheels) is enforced only when the lock contains at least one `library` wheel, so a project that locks none of the libraries (such as the SDK or `reference-media-probe`) passes. The first finding ends the project's sequence as one line:

```text
project <root>: library pin: <library>: <kind>: locked <value>, pinned <value>. Recommended action: <action>
```

| Finding kind | Meaning |
| --- | --- |
| `version` | the locked version differs from the pinned version |
| `platform-tag` | the locked wheel file is not the pinned wheel file (another platform or Python tag) |
| `digest` | the locked SHA-256 differs from the pinned digest |
| `source-distribution` | the lock offers only a source distribution for the pinned version |
| `missing-wheel` | the lock entry of a pinned package at the pinned version lists neither a wheel nor a source distribution (a pinned package that the lock does not contain is not a finding) |
| `disallowed-package` | the lock contains a denied package (for example a GPU, CUDA, or non-headless OpenCV variant) |

An unreadable lock or declaration is reported as `uv.lock: unreadable` at the same stage. The failure propagates through the log line `Analyst Python stage failed: library pin in <root> ...` and ends the check with status 1.

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

The check "Environment action tests" runs `node --test` over [`analyst-projects.test.mjs`](analyst-projects.test.mjs), [`../environment-setup/toolchain.test.mjs`](../environment-setup/toolchain.test.mjs), and [`../environment-setup/cv-libraries.test.mjs`](../environment-setup/cv-libraries.test.mjs) (the pinned libraries, the licence and SBOM check, and the library pin stage). It runs when a file under `.github/actions/environment-setup/` or `.github/actions/environment-verify/` changed, and in `finalize` mode, whether or not an Analyst Python project exists. It uses the Node.js that the caller provides. The marker `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` makes a nested run of the action skip this check, so the test fixtures never start the self-test again. The tests stub external commands and never connect to a database. They use isolated fixtures, not product projects.

## Check order and coverage

Checks run in this order and stop at the first failing check: platform build and tests, Analyst Manager build and tests, Web UI build and tests, Analyst Python projects, Agent Python projects, Environment action tests, and Markdown check. The `checks` output lists the checks that ran, and after a failure the last one failed.

The Markdown check runs when a Markdown file changed and in `finalize` mode. In `task` mode (a task or a parallel group) it checks only the changed Markdown files (`check-markdown.mjs --files`), so a broken file elsewhere on the base branch does not fail the task; `phase` and `finalize` check every Markdown file of the repository.

The `uncovered` output lists changed files that no check covers, and the workflows hold such changes for review. These paths are covered:

- Markdown files, `specs/`, and the repository metadata files `.gitignore`, `.gitattributes`, and `.editorconfig`;
- the two environment action folders, which the merge self-tests;
- `src/platform/` and `contracts/` while the platform solution exists, `src/analysts/manager/` while the Manager solution exists, and `src/clients/web/` while its `package.json` exists;
- the root of each existing recognized Analyst Python project, which includes `src/analysts/capabilities/<name>/` only while that project's `pyproject.toml` exists (a deleted capability's source stays uncovered);
- the root of each existing valid agent Python project (`src/agents/<name>`); `src/agents/README.md` is covered only by the Markdown rule.

The source of a deleted or unrecognized Analyst or agent project stays uncovered, because no check can approve a project that does not exist; this includes `src/agents/pyproject.toml`, nested agent manifests, and unowned files directly in `src/agents/`. Contracts stay covered by the platform scope, and the `contracts/agent-*` and `contracts/common/` triggers run the agent check without covering those files. Other paths under `src/analysts/`, `src/agents/`, and `src/clients/` stay uncovered.

## Corrective actions

Every failure names the project or tool, the stage, and one recommended action in the log. The table applies to Analyst and agent projects alike, with the agent additions below it:

| Failure | Action |
| --- | --- |
| malformed manifest, unsupported layout, duplicate name, missing or cyclic local dependency | correct the named manifest to the project convention above, or author the required environment extension, then retry |
| `requires-python` excludes the installed interpreter | correct `requires-python` of the named project, or author the required environment extension, then retry |
| `library pin` finding of kind `version`, `platform-tag`, `digest`, `source-distribution`, `missing-wheel`, or `disallowed-package` (the consuming project's `uv.lock` is at fault) | align the project's `uv.lock` with the pinned library set or remove the disallowed package, then retry; verification never changes the lock, and a change of a pin is a standalone environment change |
| `library pin` reports an unreadable declaration or lock, or the pin set itself is wrong (the environment is broken, not the consuming project) | run `environment-setup` with `install-tools: true`, or correct the declaration in a standalone environment change, then retry |
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
