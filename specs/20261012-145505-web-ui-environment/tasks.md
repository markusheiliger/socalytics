---

description: "Task list for Web UI Verification Environment"
---

# Tasks: Web UI Verification Environment

**Input**: Design documents from `specs/20261012-145505-web-ui-environment/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/verify-web-ui.md](contracts/verify-web-ui.md), [quickstart.md](quickstart.md)

**Tests**: The composite actions have no unit-test harness. Evidence comes from the validation scenarios in [quickstart.md](quickstart.md), which the final task runs against a scratch copy outside the working tree and reports per scenario, and from the merge workflow's self-test of the branch's own actions (unchanged baseline, SC-002).

**Organization**: Tasks are grouped by user story. This is an environment feature: every task changes only `.github/actions/environment-setup/action.yml` or `.github/actions/environment-verify/action.yml` (FR-008). Keep existing check names, order, inputs, outputs, and uncovered reporting unchanged (FR-009).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1, US2)
- Every task names its exact file path

---

## Phase 1: User Story 1 - Web UI changes are verified automatically (Priority: P1) 🎯 MVP

**Goal**: A Web UI check that installs from the lockfile, builds, and runs unit and browser tests, active only once the Web UI project exists.

**Independent Test**: Quickstart scenarios 1–9 against a scratch copy with the probe project (contract rows 1–11).

- [x] T001 [US1] Web UI scope and trigger in `.github/actions/environment-verify/action.yml` (plan.md § Design 2; data-model.md § Verification scope; FR-001, FR-002, FR-005): in the `run` script of the `Run the checks` step, add `WEB_DIR=src/clients/web`, `WEB_SCOPE='^src/clients/web/'`, `WEB_TRIGGER='^(src/clients/web|src/platform/SocAlytics\.Platform\.Api)/'`, and `WEB_PLAYWRIGHT=1.63.0` (with a comment that it must equal the Playwright pin in `environment-setup`) next to the existing `PLATFORM_*` and `MANAGER_*` variables, and add `[ -f "$WORKSPACE/$WEB_DIR/package.json" ] && COVERED+=("$WEB_SCOPE")` after the Manager line. Platform API files stay covered only by `PLATFORM_SCOPE`. No behavior changes while `src/clients/web/package.json` does not exist.
- [x] T002 [US1] Web UI check block in `.github/actions/environment-verify/action.yml` (plan.md § Design 3; contracts/verify-web-ui.md; research.md § R2, § R3, § R6; FR-003, FR-004): after the Analyst Manager block and before the Markdown block, add a block that runs when `status` is 0, `applies "$WEB_TRIGGER"` holds, and `$WEB_DIR/package.json` exists; it appends the check name `Web UI build and tests` to `checks`, then in `$WEB_DIR` runs, each preceded by a log line `Web UI step: <name>` and through `step`: `install` = `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm ci`; `playwright pin` = read the installed `@playwright/test` version from `node_modules/@playwright/test/package.json` and fail with a message naming it and `WEB_PLAYWRIGHT` when they differ; `build` = `npm run build`; `unit tests` = `npm test`; `browser tests` = `npm run test:e2e`. Stop at the first failure, log `Web UI step failed: <name>`, and set `status=1`. Return to the workspace root afterwards.

**Checkpoint**: With a probe project, the Web UI check runs, covers its files, and names failing steps; without it, verification is unchanged.

---

## Phase 2: User Story 2 - The environment provides what the Web UI build and tests need (Priority: P2)

**Goal**: Kiota and the Playwright Chromium browser at pinned versions, independent of Web UI project files.

**Independent Test**: Quickstart scenario 10, plus scenario 2 showing no browser download during verification.

- [x] T003 [US2] Pinned tools in `.github/actions/environment-setup/action.yml` (plan.md § Design 1; research.md § R1, § R2, § R5; FR-006, FR-007, SC-005): after the existing steps, add the step `Install the Kiota client generator` (`dotnet tool install --global Microsoft.OpenApi.Kiota --version 1.36.0`, append `$HOME/.dotnet/tools` to `$GITHUB_PATH`, print `kiota --version`) and the step `Install the Playwright Chromium browser` (`npx --yes playwright@1.63.0 install --with-deps chromium`, with a comment that the version must equal `WEB_PLAYWRIGHT` in `environment-verify`). Neither step reads `src/clients/web/`. Keep the existing .NET, Markdown linter, and SoftHSM2 steps unchanged.

**Checkpoint**: Setup prints Kiota 1.36.0 and installs the Playwright 1.63.0 Chromium build.

---

## Phase 3: Polish & Cross-Cutting Concerns

**Purpose**: Self-describing actions and the validation evidence.

- [ ] T004 Descriptions of both actions (FR-010): update the `description` and the "Extending" comment of `.github/actions/environment-verify/action.yml` to add the check `Web UI build and tests` (trigger `src/clients/web/` and `src/platform/SocAlytics.Platform.Api/`, coverage only `src/clients/web/`, active only while `src/clients/web/package.json` exists, steps install, playwright pin, build, unit tests, browser tests, required npm scripts `build`, `test`, `test:e2e`, `@playwright/test` pinned to 1.63.0, full Web UI tests in every mode) and the current-coverage list; update the `description` of `.github/actions/environment-setup/action.yml` to name Kiota 1.36.0 and the Playwright 1.63.0 Chromium browser and why they are installed.
- [ ] T005 Run the validation scenarios in [quickstart.md](quickstart.md) in Bash against a scratch copy outside the working tree with the probe project, and report one line per scenario with the expected and actual `checks`, `uncovered`, and exit code in the final summary for the reviewer; fix the actions when a scenario fails. Leave no scratch file in the repository.

---

## Dependencies & Execution Order

- **Phase 1 (US1)**: T001 → T002 (same file).
- **Phase 2 (US2)**: T003 depends on nothing in Phase 1 but runs after it by phase order; its Playwright pin must equal `WEB_PLAYWRIGHT` from T001.
- **Phase 3**: T004 after T002 and T003 (it describes both); T005 last.

### Parallel opportunities

None within a phase: T001 and T002 change the same file; T004 changes both files.

## Implementation Strategy

1. Phase 1 (the check), then Phase 2 (the tools), then the descriptions and the validation scenarios.
2. The environment pull request is held for human review; the reviewer reads the scenario report from T005.
