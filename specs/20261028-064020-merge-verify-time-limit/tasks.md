# Tasks: Merge Verification of Environment Specs Within Its Time Limit

**Input**: Design documents from `/specs/20261028-064020-merge-verify-time-limit/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/environment-verify-inputs.md, quickstart.md

**Tests**: Requested by FR-006: the narrowed self-test behavior is covered by a test in the environment action's own tests (`analyst-projects.test.mjs`).

**Organization**: Tasks are grouped by user story. Every task changes only the environment action `environment-verify` (an environment spec), because implementation agents may not change `.github/workflows/`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies)
- **[Story]**: The user story the task belongs to (US1, US2, US3)

> **Ordering gate**: Do not request `/speckit implement` for this spec until the workflow edit of the Scope Note below is on the default branch. No task or check enforces this order; the handoff comment states it as a precondition.

## Scope Note: Changes Outside the Tasks

These two edits are required by the spec but cannot be tasks, because automation never changes `.github/workflows/` and a task that targets it can never land. They are made directly on the default branch by a person, together with this spec's review (plan.md, "Source Code"):

- `.github/workflows/speckit-implement-merge.yml`: in the job `verify`, raise `timeout-minutes` from 90 to 150 (US1, FR-001), and pass `environment-tests-only: "true"` in the step `Self-test the changed environment verification` only (US2, FR-002). The step `Verify the merged result` does not pass it.
- `.github/workflows/README.md`: describe the 150-minute limit of the merge verification and the narrowed self-test (US3, FR-007). The passages to change are the row "Group and phase verification, repair reverification, archive, merge verification, squash merge" of the table of where AI runs (the merge verification description) and the sentence on merged environment-action repairs and "their self-tests, which ordinary `finalize` verification does not run" (the self-test scope); whoever makes the edit can check it by reading those two passages.

Ordering: the workflow edit (`timeout-minutes` 150 and `environment-tests-only` on the self-test step) must be on the default branch before `/speckit implement` is requested. With the old workflow (90 minutes, no input) this spec's own merge verification still repeats the full verification and is cancelled again. An older workspace action ignores the unknown input, so passing it there is harmless but the self-test is not narrowed.

## Phase 1: Setup

No setup is needed: no new framework, SDK, tool, or dependency (plan.md, Constitution Check).

## Phase 2: Foundational

No blocking prerequisite exists beyond the action itself, which is changed in Phase 3.

## Phase 3: User Story 2 - The self-test of the changed verification does not repeat the component tests (Priority: P1)

**Goal**: The action runs only its own tests and fixtures when `environment-tests-only` is `"true"`, so the self-test takes minutes and also fits the raised time limit of US1.

**Independent Test**: Run `node --test .github/actions/environment-verify/*.test.mjs .github/actions/environment-setup/toolchain.test.mjs`: the new tests show that a `finalize` run of a full fixture repository with the input runs only "Environment action tests".

- [x] T001 [US2] In `.github/actions/environment-verify/action.yml`, add the optional input `environment-tests-only` (default `"false"`; description: `"true"` runs only the check "Environment action tests" and skips the verification plan, the components, the Python projects, and the Markdown check) and pass it to the script as `ENVIRONMENT_TESTS_ONLY`. With `"true"`, the script runs only the Node tests of `ENVIRONMENT_TESTS` with `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING=1` (same command, same single "Environment action tests failed … Recommended action:" line on failure, `checks` is `Environment action tests`), regardless of mode, changed files, `shard`, and the `MAIN` and `ENVIRONMENT_TRIGGER` conditions that the existing "Environment action tests" check sits behind (a sharded caller runs the tests too); fails with exactly one "Recommended action" line when `ENVIRONMENT_TESTS` is empty so a self-test never succeeds without running a test, except in a nested run, which the marker deliberately skips; skips the tests without failing when `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` is already set (nested run); skips the plan invocation (the early step that computes the plan, writes `plan=` to `GITHUB_OUTPUT`, and writes the 'Verification scope' summary) and writes no `plan` output, logs one scope line `Verification scope: environment action tests only`, writes an empty `flaky`, and `uncovered` as every line of `changed-files` (empty when not given). Any other value than `"true"` keeps today's behavior unchanged. Update the header comment of the action that describes the checks and inputs. This task changes only `action.yml` (the test helper change belongs to T002).

### Tests and documentation of the action

- [ ] T002 [P] [US2] In `.github/actions/environment-verify/analyst-projects.test.mjs`, inside the `environment action tests` describe block, add tests using `fixtureTests()` and the existing `run`, `fullRepository`, and `toolBin` helpers (T002 owns the change to the existing run helper in `analyst-projects.test.mjs` so it passes `ENVIRONMENT_TESTS_ONLY` through `env`; T001 stays limited to `action.yml`). Add a test `runs the changed action script for every component kind with stubbed tools` that runs the action script with stubbed tools for each kind (verification plan, .NET, Node, Python, Markdown) and asserts that the kind's check name appears in `checks` (`Verification plan` through an invalid declaration, `platform build and tests` and `Analyst Manager build and tests` for .NET, `Web UI build and tests` for Node, `Analyst Python projects`, and `Markdown check`), so a regression in the changed action still fails the self-test. The existing `cross-check regression` tests already cover .NET, Node, Python, and Markdown in `finalize` mode (see `keeps the established checks, order, and sequential platform tests in finalize mode`) and `verification-scope.test.mjs` covers the `Verification plan` check; add only the kinds that no existing test asserts in `checks`. Tests: (1) with `ENVIRONMENT_TESTS_ONLY=true` in `finalize` mode on `fullRepository()`, `checks` is exactly `Environment action tests`, both fixture files ran with the marker `1`, no tool call was recorded (`result.calls.length` is 0), no `plan` output is written, and the log contains `Verification scope: environment action tests only`; (2) a failing fixture fails the step with exactly one `Recommended action:` line; (3) an empty `ENVIRONMENT_TESTS` fails with exactly one `Recommended action:` line and runs nothing; (4) a nested run (marker already set) passes without running the tests; (5) `uncovered` lists every changed file given in `changed-files`; (6) `"false"` and empty values still run the unchanged full verification; (6a) with `ENVIRONMENT_TESTS_ONLY=true` and a non-empty `shard` (a shard other than the first, in a mode other than `finalize`), the tests still run and `checks` is `Environment action tests`; (7) the declared input `environment-tests-only` has default `"false"` in `action.yml` and the workflow-facing outputs stay `checks`, `uncovered`, `flaky`, and `plan`.
- [ ] T003 [P] [US3] In `.github/actions/environment-verify/README.md`, document the input `environment-tests-only` under "Environment action tests" (what runs and what is skipped, the failure when the tests are not configured, the empty `plan` and `flaky`, `uncovered` as every changed file, the nested-run marker), state that the merge's self-test of the changed environment actions runs only the action's own tests and fixtures from the workspace version while the full verification of the merged result with the trusted action runs every component test, and correct "the two environment action folders, which the merge self-tests" in "Check order and coverage" so it matches. The README paragraph on "Environment action tests" must also list all four test files that `ENVIRONMENT_TESTS` in `action.yml` runs (`analyst-projects`, `verification-scope`, `verification-plan`, and `../environment-setup/toolchain`), so the described self-test scope matches the implemented one.

## Phase 4: Polish and Verification

User Story 1 (the 150-minute limit) is delivered by the direct workflow edit of the Scope Note and has no task: a task that targets `.github/workflows/` can never land, and a test that asserts the edit would fail on the implementation branch.

- [ ] T004 Run the validation of quickstart.md from the repository root: `node --test .github/actions/environment-verify/*.test.mjs .github/actions/environment-setup/toolchain.test.mjs`, `node --test .github/scripts/speckit-*.test.mjs`, and `node .github/scripts/check-markdown.mjs`; fix any failure in the files changed by T001 to T003 without weakening a test.

## Dependencies and Execution Order

- T001 first: T002 and T003 depend on the implemented input.
- T002 and T003 touch different files and run in parallel (the group above).
- T004 runs last.
- US1 and US2 are both P1 and needed together (research.md, Decision 2): the limit alone does not fit a repeated full verification.

## Parallel Example

```text
T002 (analyst-projects.test.mjs)  ||  T003 (environment-verify/README.md)   after T001
```

## Implementation Strategy

1. MVP: T001 and T002 deliver the narrowed self-test with its evidence.
2. T003 corrects the documentation of the action; the workflows README and the workflow itself are edited directly (Scope Note).
3. T004 verifies everything.
