# Tasks: Agents Python Verification Environment

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [environment interface](contracts/environment-interface.md), and [quickstart.md](quickstart.md).

**Tests**: Required by FR-016 and the validation guide. Each implementation task includes its directly related tests in `.github/actions/environment-verify/analyst-projects.test.mjs`, so a task never lands deliberately failing tests.

**Organization**: The shared area option first, then one phase per user story, then environment-local documentation and final acceptance.

## Format And Boundaries

- Tasks use `- [ ] Tnnn [P?] [USn?] Description`.
- Consecutive unchecked `[P]` tasks under one heading run in parallel, each from the same commit; their predicted file changes must be disjoint and neither may depend on the other.
- Implementation changes are limited to `.github/actions/environment-setup/` and `.github/actions/environment-verify/`, plus this feature's own task markers. Within them, only the files named in the plan's implementation allow-list change.
- Do not create an agent project, a source directory, a contract, a workflow, a cache, a tool pin, or a credential; do not change `ENVIRONMENT_TESTS` or any existing test case.
- Do not tick another task, infer production readiness, or weaken an existing check to make this feature pass.
- The plan's Architecture Updates outcome is `None`; no architecture-file change is required or permitted by this environment implementation.

## Phase 1: Foundational

**Purpose**: Give the existing helper an agents area that reuses the Analyst project convention, without changing the Analyst area.

- [x] T001 Add the option `--area analysts|agents` (default `analysts`) to `.github/actions/environment-verify/analyst-projects.py` with passing tests in `.github/actions/environment-verify/analyst-projects.test.mjs`. With the default, output, ordering, and every error text stay unchanged, and every existing test case passes unmodified. The `agents` area recognizes `src/agents/<name>/pyproject.toml` for every immediate child that is not hidden, not one of the ignored directories (`.venv`, `venv`, `node_modules`, `__pycache__`, `.git`, `build`, `dist`, `site-packages`), and not a symbolic link; reuses the existing convention checks (static unique name, `requires-python` admitting the interpreter, committed `uv.lock`, build system, no `[tool.uv].package = false`, no workspace, git, URL, or index sources, sources declared in requirements, cycles); and fails `src/agents/pyproject.toml` and manifests nested below a project as an unsupported layout at the `project convention` stage with the action "move the project to `src/agents/<name>`, or author the required environment extension, before retrying". Test: no agents area or no project yields nothing; several agent projects are recognized and ordered dependencies first; hidden, ignored, and symbolic-link folders are skipped; each convention violation fails naming the agent project.

**Checkpoint**: The helper discovers and validates agent projects in isolation; nothing selects or runs them yet.

## Phase 2: User Story 1 - Verify agent project changes without a manual coverage exception (Priority: P1)

**Goal**: Select affected agent projects, run the locked stages for them as a separate check, and cover their roots.

**Independent Test**: With isolated agents-area fixtures and a stub `uv`, own-source, local-dependency, shared contract, environment, and finalize triggers select the right projects once, and each run executes the four stages or fails at the responsible one.

- [x] T002 [US1] Add agents-area selection to `.github/actions/environment-verify/analyst-projects.py` with passing tests in `.github/actions/environment-verify/analyst-projects.test.mjs`, per the selection contract in `contracts/environment-interface.md`: finalize and either environment action folder select every agent project; a change in a folder directly under `contracts/` whose name starts with `agent-`, or in `contracts/common/`, selects every agent project; otherwise an own-source change selects the project and its reverse local-dependency closure, each once, in dependency order with its reason. `covered` lists the existing valid agent roots. Test `contracts/agent-catalog/`, `contracts/agent-orchestration/`, `contracts/agent-tools/`, a new `contracts/agent-x/`, and `contracts/common/` selecting all; `contracts/agentic/`, a root file `contracts/agent-x.md`, `contracts/analysis/`, Analyst, Manager, and unrelated changes selecting none; prefix-similar project folders (`mcp`, `mcp-server`) never matching each other; all three modes.
- [x] T003 [US1] Add the check `Agent Python projects` to `.github/actions/environment-verify/action.yml` with passing execution fixtures in `.github/actions/environment-verify/analyst-projects.test.mjs`: run the helper with `--area agents` at the start beside the Analyst discovery, add the agent covered roots to `COVERED`, and place the check after `Analyst Python projects` and before `Environment action tests`. Give `python_project` a label parameter so the Analyst block keeps its exact `Analyst Python …` log lines and the agent block logs `Agent Python project: <root>`, `Agent Python stage: <stage> (<root>)`, and `Agent Python stage failed: <stage> in <root> (command: <command>). Recommended action: <action>`; run the four stages of the contract with only `UV_PYTHON_DOWNLOADS=never` and `UV_PYTHON` set, and no credential, provider, or network variable. Test the exact stage order and environment, each failing stage stopping the check with the project, stage, command, and one recommendation, dependencies first, stop at the first failing project, missing `SOCALYTICS_PYTHON` or `uv` failing explicitly, a covered agent root in task mode, and no agent check when no agent project exists; extend the existing real-`uv` fixture case (same skip condition) with an isolated agents-area package that passes every stage without changing its declarations.

**Checkpoint**: Agent projects are verified and covered like Analyst projects, against isolated fixtures only.

## Phase 3: User Story 2 - Keep agent and Analyst verification independent (Priority: P2)

**Goal**: Keep the two project areas separate in dependencies, names, selection, and failures.

**Independent Test**: Fixtures with both Analyst and agent projects show cross-area dependencies and agent names repeating Analyst names failing in the agents area only, and changes in one area selecting only that area's check.

- [x] T004 [US2] Enforce area isolation in `.github/actions/environment-verify/analyst-projects.py` and `.github/actions/environment-verify/action.yml` with passing tests in `.github/actions/environment-verify/analyst-projects.test.mjs`: an agent local dependency resolving under `src/analysts/` fails at `project convention` with the action "remove the dependency on the Analyst project or move the shared code into an agent project under `src/agents/<name>`, before retrying"; an Analyst local dependency on an agent project still fails as before; the agents area reads only the static `[project].name` of the Analyst projects (skipping an Analyst manifest without a readable static name, which the Analyst check reports) and fails an agent project whose normalized name repeats one, naming both projects; `--area analysts` output and errors stay unchanged while agent projects (valid or invalid) exist. Through the action, an agent-only change runs only `Agent Python projects`, an Analyst-only change runs only `Analyst Python projects`, a `contracts/analysis/` change selects no agent project, and an agent discovery failure fails `Agent Python projects` (named in the log with one recommendation) while the Analyst check keeps its name and result.

**Checkpoint**: Neither area's projects change the other's verification.

## Phase 4: User Story 3 - Keep coverage and recovery guidance honest (Priority: P3)

**Goal**: Report agents-area coverage exactly and give one concrete action for every new failure while preserving every existing behavior.

**Independent Test**: Absent, present, deleted, area-root, nested, and unowned agents-area paths and each injected failure produce the expected coverage, failed status, recommendation, and preserved outputs.

- [x] T005 [US3] Complete coverage and regression evidence in `.github/actions/environment-verify/analyst-projects.test.mjs`, making only necessary corrections in `.github/actions/environment-verify/action.yml` and `.github/actions/environment-verify/analyst-projects.py`: a deleted agent manifest, `src/agents/pyproject.toml`, nested manifests, and unowned `src/agents/` files stay uncovered; `src/agents/README.md` is covered only through the Markdown rule; every new failure names the agent project or manifest, the stage, and exactly one recommended action and never advises bypassing a check, unlocking verification, or weakening tests; an agent failure still writes the log, `checks`, and `uncovered` outputs; an invalid existing agent project fails `Agent Python projects` in every mode even when the changed paths do not touch it, and then no agent root is covered; the full check order in finalize mode is platform, Analyst Manager, Web UI, Analyst Python projects, Agent Python projects, Environment action tests, Markdown check; and every existing cross-check case passes unchanged. Update the header description and the extension comment of `.github/actions/environment-verify/action.yml` to name the new check, its triggers, and its coverage.

**Checkpoint**: Checked, failed, and uncovered agent work are distinguished, with actionable recovery.

## Phase 5: Polish And Cross-Cutting Acceptance

### Parallel Environment Documentation

- [x] T006 [P] Document in `.github/actions/environment-verify/README.md` the implemented agent project layout (`src/agents/<name>/pyproject.toml`), the shared convention, agent-only local dependencies and names unique across areas, the requirement that a default test run needs no network service, model provider, credential, or secret, the selection triggers (including the `contracts/agent-*` family and `contracts/common/`), coverage, the new check's place in the check order, and its corrective actions next to the Analyst Python section; advertise no product command for an agent project that does not exist.
- [x] T007 [P] State in `.github/actions/environment-setup/README.md` that the installed `SOCALYTICS_PYTHON` and `uv` also serve the agent Python projects that `environment-verify` checks, without changing any pin, step, or other statement.

### Final Acceptance

- [ ] T008 Execute the complete `quickstart.md` matrix against `.github/actions/environment-verify/` and `.github/actions/environment-setup/`: run both environment-local test files, the real-`uv` agents fixture where a real `uv` is available, the existing environment-feature self-verification, and `node .github/scripts/check-markdown.mjs`. Correct missing evidence only inside those two folders; confirm that the setup action's `action.yml`, scripts, toolchain declaration, cache keys, and tests are unchanged, that no product project or production claim was introduced, and that the environment change is held for a person's review before merge.

## Dependencies And Execution Order

```text
T001 -> T002 -> T003     US1
           T003 -> T004  US2
                T004 -> T005  US3
T005 -> T006 and T007 -> T008
```

T001 is the shared foundation. US1 delivers the first verifiable increment; US2 and US3 extend the same helper, action, and test file and therefore run serially after it. All tasks remain unchecked until their implementation workflow completes them.

## Parallel Execution Examples

- **Foundational, US1, US2, US3**: None. Every task changes `.github/actions/environment-verify/analyst-projects.test.mjs` and the helper or action, so they stay serialized.
- **Polish**: T006 and T007 run together because they change different README files. T008 waits for both.

## Implementation Strategy

1. Add the agents area to the helper without changing the Analyst area (T001).
2. Deliver US1 as the MVP: agent projects selected, run, and covered (T002, T003).
3. Isolate the areas (US2) and complete coverage, guidance, and regression evidence (US3).
4. Document the implemented behavior, pass all final gates, and present the environment change for a person's review before merge.

The technical MVP is T001 to T003. It is not permission to merge an incomplete environment feature, request downstream implementation, or claim that the MCP Server or Hermes works.
