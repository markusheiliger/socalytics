---

description: "Task list for Spec Kit Tooling Tests in Environment Verification"
---

# Tasks: Spec Kit Tooling Tests in Environment Verification

**Input**: Design documents from `/specs/20261027-205629-environment-speckit-tooling-tests/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md)

**Tests**: Requested by FR-010 and SC-005: every rule is proven by the action's own Node tests, and no existing test changes behavior.

**Organization**: Tasks are grouped by user story. This is an environment spec: only `.github/actions/environment-setup`, `.github/actions/environment-verify`, and this spec's own `tasks.md` ticks may change (FR-013). No file under `.github/actions/environment-setup` is expected to change.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Paths are relative to the repository root; `.github/actions/environment-verify/` is abbreviated below only where the full path is written out

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: The shared tooling-file pattern that the check, the plan, and the scope reason all use

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T001 Precondition: before changing anything, run `node --test .github/actions/environment-verify/*.test.mjs .github/actions/environment-setup/toolchain.test.mjs` and confirm the existing environment action tests pass. Then, in `.github/actions/environment-verify/verification-scope.mjs`, export `TOOLING_FILE = /^\.github\/scripts\/speckit-[^/]+\.mjs$/` (matches `speckit-*.mjs` and `speckit-*.test.mjs` directly under `.github/scripts/` only) and change `classify` so the first non-Markdown changed file that matches it reports `` `${file} is a Spec Kit tooling file covered by the Spec Kit tooling tests` `` with `documentationOnly: false`, while every other non-Markdown file keeps `` `${file} is not Markdown` ``

**Checkpoint**: Pattern and scope reason exist; the existing tests still pass

---

## Phase 2: User Story 2 - Count the Spec Kit tooling files as covered by a test check (Priority: P1)

**Goal**: `.github/scripts/speckit-*.mjs` and `speckit-*.test.mjs` are covered by the new test check and no other `.github/` path becomes covered. This phase runs before User Story 1 because the User Story 1 fixtures assert the coverage of task-named files (FR-009).

**Independent Test**: Run the scope and plan rules for changed and task-named files and assert the covered, uncovered, and protected outcomes ([quickstart.md](quickstart.md)). Each new case asserts the exact rule outcome, so it fails when its rule is removed (SC-005).

- [x] T002 [US2] In `.github/actions/environment-verify/verification-plan.mjs`, import `TOOLING_FILE` from `./verification-scope.mjs` and add it to the `covered` list next to `...STATIC_COVERED` so tooling files are absent from `uncovered`, without changing `STATIC_COVERED`, protected-path handling, or any other `.github/` path
- [x] T003 [P] [US2] In `.github/actions/environment-verify/verification-scope.test.mjs`, add cases for the `classify` reason, each asserting the exact reason text and `documentationOnly` value so the case fails when the tooling reason is removed: `.github/scripts/speckit-implement-core.mjs` and `speckit-implement-core.test.mjs` report the tooling reason with `documentationOnly: false`; a mixed list reports whichever non-Markdown file comes first; `.github/scripts/helper.mjs`, `.github/scripts/sub/speckit-x.mjs`, and `.github/scripts/speckit-x.sh` keep "is not Markdown"
- [x] T004 [P] [US2] In `.github/actions/environment-verify/verification-plan.test.mjs`, add plan cases, each asserting the exact `covered`/`uncovered`/protected outcome so the case fails when the `covered` entry is removed: `speckit-implement-core.mjs`, `speckit-implement-work.mjs`, and a `speckit-*.test.mjs` file are covered and not `uncovered`, also as the task-named change list with no change (asserting only the files, the tooling scope text, `uncovered` empty, and the protected outcome; the plan has no `checks` field, so the check list is asserted in the action fixtures of T006; FR-009); a workflow, another `.github/scripts/` file, a skill, and another composite action stay in `uncovered`; a protected path stays protected; a changed Markdown file under `.github/scripts/` stays covered as before

**Checkpoint**: Pattern, scope reason, and coverage exist; the existing tests still pass

---

## Phase 3: User Story 1 - Run the Spec Kit tooling tests as a verification check (Priority: P1) 🎯 MVP

**Goal**: Verification runs `node --test` over the `speckit-*.test.mjs` files of `.github/scripts/` as the check "Spec Kit tooling tests" and fails with the reproduce command and one recommended action when a test fails.

**Independent Test**: Run the action against a fixture workspace with a changed tooling file, once with passing and once with failing tooling tests, and compare the reported checks, the outcome, and the failure message ([quickstart.md](quickstart.md)).

- [x] T005 [US1] In `.github/actions/environment-verify/action.yml`, add the check step "Spec Kit tooling tests" after the "Environment action tests" step and before the "Markdown check" step, and set the env `SPECKIT_TOOLING_TESTS` to the workspace-relative `.github/scripts` folder (unset in fixtures, like `ENVIRONMENT_TESTS`); the variable gates the step and its value is the tooling folder the step uses to locate the tests, so the file glob, the command, and the reproduce command in the failure message all use `<value>/speckit-*.test.mjs` (`.github/scripts/speckit-*.test.mjs` in the action). The step runs when `status` is 0, `MAIN` is 1, `SPECKIT_TOOLING_TESTS` is set, `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` is empty, and `applies '^\.github/scripts/speckit-[^/]+\.mjs$'` holds (finalize mode, a changed path including a deletion, or a task-named path). It appends "Spec Kit tooling tests" to `checks`; fails with a message naming the check when `node` is missing or no `speckit-*.test.mjs` file exists; and otherwise runs `step env SOCALYTICS_ENVIRONMENT_TESTS_RUNNING=1 node --test "$SPECKIT_TOOLING_TESTS"/speckit-*.test.mjs`, failing with `Spec Kit tooling tests failed (command: node --test <files>). Recommended action: reproduce this command, correct the failing Spec Kit tooling script or its test without weakening the test, and retry verification`. Also add the check to the action `description` order sentence (between "Environment action tests" and "Markdown check"). Also add "Spec Kit tooling tests" to the shard log line ("also the Python projects, environment action tests, Spec Kit tooling tests, and Markdown check"), to the `shard` input description, and to the comments in `action.yml` that enumerate the checks the first shard owns (the comment above the "Environment action tests" step and the header comments), because the check runs only when `MAIN` is 1
- [x] T006 [US1] In `.github/actions/environment-verify/analyst-projects.test.mjs`, add fixtures next to the "Environment action tests" ones (stub `.github/scripts/speckit-*.test.mjs` files, `SPECKIT_TOOLING_TESTS` set to the workspace-relative stub folder that holds the stub tests, because the value locates the tests) proving: success on a changed `speckit-*.mjs` and on a changed `speckit-*.test.mjs`; run on a deleted tooling file; run on the task-named change list `.github/scripts/speckit-implement-core.mjs` and `.github/scripts/speckit-implement-work.mjs` with no change, asserting that `checks` includes "Spec Kit tooling tests" and `uncovered` is empty; run in finalize mode with nothing changed; failure with the exact one-line message and the reproduce command; failure when no `speckit-*.test.mjs` file exists; no run when the nested-run marker `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` is set; skip, with the check absent from `checks`, for an unrelated change, a changed non-`speckit-` file under `.github/scripts/`, and a documentation-only change; no run on a shard other than the first (the check belongs to the first shard only); and update the check-order assertion (platform, Manager, Web, Analyst Python, Agent Python, "Environment action tests", "Spec Kit tooling tests", Markdown check) while the fixtures that leave `SPECKIT_TOOLING_TESTS` unset keep their existing check lists

**Checkpoint**: User Story 1 is complete: the check runs, fails, and skips as specified

---

## Phase 4: User Story 3 - Keep the new coverage proven by the action's own tests (Priority: P2)

**Goal**: A maintainer can read the check, its trigger, command, failure message, and covered scope in the README, and the action's tests fail if the rules drift.

**Independent Test**: Run the Node tests of the environment actions and confirm each new case asserts the exact rule outcome, so it fails when its rule is removed.

- [x] T007 [US3] In `.github/actions/environment-verify/README.md`, add a "Spec Kit tooling tests" section after "Environment action tests" documenting the check, its trigger (a changed or deleted `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs`, a task-named `.github/scripts/speckit-*.mjs` or `speckit-*.test.mjs` file, and finalize mode), its command `node --test .github/scripts/speckit-*.test.mjs`, the nested-run marker, the failure message, that the check belongs to the first shard only, and the covered tooling scope that covers no other `.github/` path; add the check to the order sentence ("… Environment action tests, Spec Kit tooling tests, and Markdown check") and update the sentence on the scope rule's "is not Markdown" reason where it names `.github/scripts/`. Closing steps: run `node --test .github/actions/environment-verify/*.test.mjs .github/actions/environment-setup/toolchain.test.mjs` and `node --test .github/scripts/speckit-*.test.mjs` (recording that it passes without installing anything or using credentials, SC-005), walk the scenarios of [quickstart.md](quickstart.md) against the action fixtures, run `node .github/scripts/check-markdown.mjs --files .github/actions/environment-verify/README.md`, and confirm `git status` lists only files under `.github/actions/environment-verify/` and this spec folder (FR-013, SC-002)

**Checkpoint**: All user stories are complete and proven

---

## Dependencies & Execution Order

- Phase 1 (T001) first; it blocks every story because T002, T005, and the tests use the pattern and the reason (T001 also holds the baseline test run as its precondition).
- US2 (T002 → T003, T004) comes before US1 (T005 → T006) because the T006 fixtures assert the coverage of task-named files (FR-009), which T002 provides; within each story the implementation precedes its tests.
- US3 (T007) follows US1 and US2 and holds the final test runs, the quickstart walkthrough, the Markdown check, and the scope check.

### Parallel Opportunities

- T003 and T004 form the only parallel group: they change different test files (`verification-scope.test.mjs` and `verification-plan.test.mjs`) and both run after T002. All other tasks run one after another because they share `action.yml`, `analyst-projects.test.mjs`, `verification-plan.mjs`, or `README.md`, or depend on earlier results.

## Implementation Strategy

- **MVP**: Phases 1–3 (coverage, then the check running, failing, and skipping). Phase 2 unblocks the stopped spec's T004 and T005; US3 completes the documentation and final proof.
- Keep every change inside the two environment action folders and never weaken an existing test.
