# Feature Specification: Spec Kit Tooling Tests in Environment Verification

**Feature Branch**: `20261027-205629-environment-speckit-tooling-tests`

**Created**: 2026-10-27

**Status**: Draft

**Input**: User description: "Environment spec: change only `.github/actions/environment-setup` and `.github/actions/environment-verify` (plus the spec's own `tasks.md` ticks). Make environment-verify run the Spec Kit tooling tests and count the Spec Kit tooling files as covered by a test check. Today environment-verify has no check for `.github/scripts/`: the covered paths list only Markdown, `specs/`, repository metadata, the environment actions, and the discovered component scopes, and the scope rule reports 'is not Markdown' for `.github/scripts/*.mjs`. The Spec Kit tooling tests (`node --test` over `.github/scripts/speckit-*.test.mjs`) are therefore not part of any check, and the verification of an unchanged branch reports only the 'Markdown check' for such files. Add a check (for example 'Spec Kit tooling tests') that follows the pattern of 'Environment action tests'; add a `.github/scripts/` tooling scope so `speckit-*.mjs` and `speckit-*.test.mjs` count as covered by this test check without covering any other `.github/` path and without weakening existing rules; run it when a file under `.github/scripts/` changed and in finalize verification, and expose it in the checks the verification result reports; add self-tests in the action's own tests. The stopped spec `specs/archive/20261026-100223-environment-guard-archive-rewrites` (twin #342, pull request #344) needs it: its tasks T004 and T005 name `.github/scripts/speckit-implement-core.mjs` and `.github/scripts/speckit-implement-work.mjs`, whose work is already on its branch, so the tick-if-already-satisfied path refuses with 'no test or build check covered the task named files'. That spec depends on this one."

## Clarifications

### Session 2026-10-27

- Q: When verification checks an unchanged branch for a task that names `.github/scripts/speckit-implement-core.mjs` or `.github/scripts/speckit-implement-work.mjs` (so no file under `.github/scripts/` has changed), should the "Spec Kit tooling tests" check run because the task names those files? → A: Yes: the check runs when a changed file or a task-named `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs` file (and in finalize mode), so the unchanged-branch verification reports it and the already-satisfied task path can tick the tasks.
- Q: Which changed files under `.github/scripts/` should start the "Spec Kit tooling tests" check in task and phase mode? → A: Only changed `speckit-*.mjs` and `speckit-*.test.mjs` files, including deletions of them.

## User Scenarios & Testing *(mandatory)*

The users are contributors and the implementation automation. Today a change to the Spec Kit tooling under `.github/scripts/` is verified by no test: only the Markdown check looks at those files, so their tests never run in verification, and a task that names such files can never be recognized as verified without a change of its own.

### User Story 1 - Run the Spec Kit tooling tests as a verification check (Priority: P1)

A contributor or implementation agent changes a Spec Kit tooling script or its test. Verification runs the tooling's own tests with the Node.js that the caller provides and fails, with the command to reproduce and one recommended action, when a test fails.

**Why this priority**: Without the check, a broken tooling change passes verification, and no later story can rely on tooling files being covered.

**Independent Test**: Run verification against a fixture workspace with a changed tooling file, once with passing and once with failing tooling tests, and compare the reported checks, the outcome, and the failure message.

**Acceptance Scenarios**:

1. **Given** a changed `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs` file (a deletion included) or a task-named `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs` file, **When** verification runs in task or phase mode, **Then** a check named "Spec Kit tooling tests" runs every `speckit-*.test.mjs` file of `.github/scripts/` and appears in the checks the result reports; a task-named non-`speckit-` file under `.github/scripts/` does not start the check.
2. **Given** a failing tooling test, **When** the check runs, **Then** verification fails and its message names the failed check, the exact command to reproduce, and one recommended action, like the "Environment action tests" failure.
3. **Given** verification runs in finalize mode, **When** no tooling file changed and no task names one, **Then** the check still runs.
4. **Given** the check runs inside a test that is itself run by verification, **When** the nested-run guard of the environment tests is set, **Then** the check does not start again, so verification never recurses.
5. **Given** a change that touches no `speckit-*.mjs` or `speckit-*.test.mjs` file, names no task `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs` file, and a mode other than finalize, **When** verification runs, **Then** the check is skipped and does not appear in the reported checks.

---

### User Story 2 - Count the Spec Kit tooling files as covered by a test check (Priority: P1)

A maintainer or the implementation automation reads the verification result for a branch. Files `.github/scripts/speckit-*.mjs` and `.github/scripts/speckit-*.test.mjs` are reported as covered by the new test check instead of only by the Markdown check, so that a task naming such files is seen as verified by a check that is not the Markdown check.

**Why this priority**: The verification of an unchanged branch is what lets the already-satisfied task path tick tasks T004 and T005 of the stopped spec; without coverage by a test check it keeps refusing.

**Independent Test**: Run the scope and plan rules for task-named files in fixtures and assert the covered, uncovered, and protected outcomes and the reported checks.

**Acceptance Scenarios**:

1. **Given** a task or change naming `.github/scripts/speckit-implement-core.mjs` or `.github/scripts/speckit-implement-work.mjs`, **When** verification plans or runs, **Then** the plan reports the files as covered and the action reports the check "Spec Kit tooling tests" in its checks, a check other than "Markdown check".
2. **Given** a change to a `speckit-*.test.mjs` file, **When** verification plans, **Then** the file is covered by the same check.
3. **Given** a changed `.github/` file that is not a Spec Kit tooling script or test (for example a workflow, another script, a skill, or a composite action other than the two environment actions), **When** verification plans, **Then** it stays uncovered and holds the pull request for review as before.
4. **Given** a changed path that is protected for implementation tasks, **When** verification plans, **Then** it stays protected; being covered never lifts protection.
5. **Given** a changed Markdown file under `.github/scripts/`, **When** verification plans, **Then** the Markdown check keeps covering it as before.

---

### User Story 3 - Keep the new coverage proven by the action's own tests (Priority: P2)

A maintainer of the environment actions changes the verification rules later. The action's own tests fail when the Spec Kit tooling check, its triggers, or its coverage change by accident.

**Why this priority**: The coverage rules decide what merges without review, so they must not drift silently.

**Independent Test**: Run the Node tests of the environment actions and confirm the new cases exist and fail when the corresponding rule is removed.

**Acceptance Scenarios**:

1. **Given** the action's own tests, **When** they run, **Then** they show that a change or task naming `.github/scripts/speckit-*.mjs` is covered by the new check, that the check runs on a changed tooling file, on a task-named file under `.github/scripts/`, and in finalize mode, that it is skipped when nothing relevant changed, and that unrelated `.github/` files stay uncovered.
2. **Given** the action's README, **When** a maintainer reads it, **Then** it documents the new check, its trigger, its command, its failure message, and the new covered scope.

---

### Edge Cases

- The caller provides no Node.js: the check fails with a message that names the missing tool, like the other Node-based checks, instead of passing silently.
- No `speckit-*.test.mjs` file exists: the check fails instead of passing an empty run.
- A change touches only the environment action folders: the existing "Environment action tests" check runs as before; this check runs only if its own trigger applies.
- A tooling file (`speckit-*.mjs` or `speckit-*.test.mjs`) is deleted: the deletion still counts as a change and runs the check.
- A file under `.github/scripts/` that does not start with `speckit-` (for example a helper that is not Spec Kit tooling) stays uncovered, except Markdown, which the Markdown check covers; changing it alone does not start the check.
- An unchanged branch is verified for a task that names a `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs` file (so no file changed): the check still runs, because the task names the file; a task-named non-`speckit-` file under `.github/scripts/` does not start it.
- The check is nested inside the environment tests: the nested-run guard prevents a second run.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Verification MUST provide a check named "Spec Kit tooling tests" that runs the Node test runner over every `.github/scripts/speckit-*.test.mjs` file with the Node.js the caller provides, and installs or downloads nothing.
- **FR-002**: The check MUST fail verification when a test fails, when no test file exists, or when Node.js is unavailable, and its failure message MUST name the check, the command to reproduce, and one recommended action, following "Environment action tests".
- **FR-003**: The check MUST run in task and phase mode when a changed file is a `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs` file (a deletion included) or when a task-named file is a `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs` file, MUST run in finalize mode, and MUST NOT run otherwise; other changed files under `.github/scripts/` do not start it.
- **FR-004**: The check MUST honor the nested-run guard of the existing environment tests so that it never runs from within a verification-run test.
- **FR-005**: The check MUST appear in the list of checks that the verification result reports whenever it ran, so that a consumer that looks for a check other than "Markdown check" sees it for files covered by it.
- **FR-006**: A tooling scope for `.github/scripts/` MUST add `speckit-*.mjs` and `speckit-*.test.mjs` to the covered paths, covered by this check.
- **FR-007**: The tooling scope MUST NOT make any other `.github/` path covered, MUST NOT weaken protected paths, and MUST keep uncovered changed files holding a pull request for review.
- **FR-008**: The scope rule that reports a changed file as not covered MUST no longer say "is not Markdown" for the covered tooling files, and MUST keep its message for every other uncovered file.
- **FR-009**: The verification of an unchanged branch for task-named files `.github/scripts/speckit-implement-core.mjs` and `.github/scripts/speckit-implement-work.mjs` MUST run and report the new check (because the task names `.github/scripts/speckit-*.mjs` files, although no file changed) and no uncovered files.
- **FR-010**: The action's own tests MUST cover: coverage of a change and of task-named files by the new check, the run on a changed tooling file (deletion included), on a task-named file under `.github/scripts/` with no change, and in finalize mode, the skip when nothing relevant changed (including a changed non-`speckit-` file under `.github/scripts/`), and unrelated `.github/` files staying uncovered; the action fixtures MUST exercise the check's success, failure, and skip.
- **FR-011**: The action's README MUST document the new check, its trigger, command, failure message, and the covered tooling scope.
- **FR-012**: Every existing check, input, output, mode rule, cache boundary, and result field MUST be preserved.
- **FR-013**: The change MUST be limited to `.github/actions/environment-setup`, `.github/actions/environment-verify`, and the spec's own `tasks.md` ticks.

### Key Entities

- **Spec Kit tooling tests check**: The verification check that runs the tooling's tests and is named in the reported checks.
- **Tooling scope**: The covered-path declaration for `.github/scripts/speckit-*.mjs` and `speckit-*.test.mjs`, tied to the check.
- **Verification result**: The reported statuses, covered and uncovered files, and checks that consumers such as the already-satisfied task path inspect.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For 100% of changes to `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs`, verification runs the tooling tests and reports them as covered by a check other than the Markdown check.
- **SC-002**: 0 other `.github/` paths become covered by the change, and every previously uncovered or protected path keeps its status.
- **SC-003**: A task naming only the two tooling files of the stopped spec, whose work is already on its branch, is no longer refused with "no test or build check covered the task named files" because of missing coverage.
- **SC-004**: A failing tooling test is reported with the reproduce command and one recommended action in 100% of failures.
- **SC-005**: All existing environment-action tests pass unchanged, and the new tests fail when the check, its triggers, or its scope are removed.

## Assumptions

- The caller (setup or the workflow job) provides the Node.js that the existing Node-based checks use; this change adds no tool installation.
- The tooling's tests need no network, credentials, or services.
- The merge of this environment spec is held for a person's review, as for every environment spec, and the stopped spec `specs/archive/20261026-100223-environment-guard-archive-rewrites` names it as a dependency.
- Plan: Architecture Updates are expected to be `None`, because the change affects only the environment's verification coverage and no product architecture.
- Out of scope: changing the Spec Kit tooling itself, the workflows, `verifiedUnchangedCheck`, other `.github/` paths, or the stopped spec's tasks.
