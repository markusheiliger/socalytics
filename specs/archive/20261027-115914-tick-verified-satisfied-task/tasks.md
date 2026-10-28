# Tasks: Tick a Verified, Already-Satisfied Task

**Input**: Design documents from `/specs/archive/20261027-115914-tick-verified-satisfied-task/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/task-result.md, quickstart.md

**Tests**: Test tasks are included: FR-008 requires automated tests, in `.github/scripts/speckit-implement.test.mjs` (real git repositories under `withRepos`, a fake GitHub client).

**Organization**: Tasks are grouped by user story. The plan's Architecture Updates section is `None`, so no `docs/architecture/` task exists.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Include exact file paths in descriptions

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: The pure helper every story depends on.

- [x] T001 Add and export a pure function `taskNamedFiles(text, { exists, isSafe, max })` in `.github/scripts/speckit-implement-core.mjs` that returns the distinct repository-relative paths a task's text names (backticked tokens and bare tokens with a path separator or a file extension), keeping only those for which `exists(path)` and `isSafe(path)` are true, in order of first mention, capped at `max` entries; it takes the predicates and cap as parameters so the core module does not import from `speckit-implement-work.mjs` (plan Design 1, research R2a) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)

**Checkpoint**: `taskNamedFiles` exists and is exported.

---

## Phase 2: User Story 1 - A task already satisfied by an earlier push is ticked (Priority: P1)

**Goal**: An empty agent change lands and ticks the task when the verification of the unchanged branch, selected for the files the task names, passed with at least one check on the very head the land job lands on.

**Independent Test**: Run the land path for a task whose agent change is empty, whose pushed work predates the first attempt with workflow commits on top, and whose recorded verification of the unchanged branch passed on the current head; the task is ticked and no attempt fails.

- [x] T002 [US1] In `runPackage` (task kind) of `.github/scripts/speckit-implement-work.mjs`, when `patchBytes === 0`, compute `result.taskFiles` with `taskNamedFiles` (task text from `tasks.md`, `exists` against the checkout, `isSafe` is `isSafeRelativePath`, `max` is `MAX_CHANGED_FILES`) and write those paths as the content of `changed-files.txt`; `result.changedFiles` stays `[]` and a non-empty patch behaves exactly as today (FR-005, research R2) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)
- [x] T003 [US1] In `runVerdict` of `.github/scripts/speckit-implement-work.mjs`, when `patchBytes === 0` and verification is configured, write `result.unchanged = { status, head, checks, uncovered, files }` per `data-model.md` (`status` is `passed`, `failed`, `not-run`, or `not-configured`; `head` is the result's `base`; `checks` the names of checks that ran and passed; `uncovered` the named files no check covers; `files` a copy of `taskFiles`) without adding the unchanged-branch verification failure to `result.reasons` and without changing `verified`, so the `doneByPush` path keeps today's outcomes (FR-004, research R3) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)
- [x] T004 [US1] In `runLand` (`kind === 'task'`) of `.github/scripts/speckit-implement-work.mjs`, add a helper `isVerifiedUnchanged(result, headNow)` that is true only when `result.unchanged.status === 'passed'`, at least one check qualifies (a check qualifies unless its name is exactly "Markdown check"), `uncovered` is empty, `files` is non-empty, at least one file in `files` that no other task in `tasks.md` names (the land job reads `tasks.md` at the checked-out head, calls `taskNamedFiles` for every other task with `exists` as existence in the checkout, the same `isSafeRelativePath`, and the same `MAX_CHANGED_FILES` cap, and subtracts that union from `result.unchanged.files`) differs from the base branch (the existing `branchPaths` diff against `origin/<base>` only; a file named by several tasks cannot show this task's work, and a task with no specific named file is refused with the cause "no named file is specific to this task"), and `unchanged.head` equals the pull request's current head (the tip fetched before the land checkout, i.e. the `rev-parse HEAD` taken as `tip` in the land loop, not the detached checkout of the work head, which always equals `unchanged.head`); set `allowEmpty = doneByPush(...) || isVerifiedUnchanged(...)` and remember which path allowed it as the boolean flag `verifiedUnchanged` for reporting (FR-001, FR-002, FR-004, research R4) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)
- [x] T005 [US1] In `pushTask` of `.github/scripts/speckit-implement-work.mjs`, skip the `git apply` of `changes.patch` when the patch file is empty so a tick-only landing survives a moved branch (research R5) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)
- [x] T006 [US1] In `runLand` of `.github/scripts/speckit-implement-work.mjs`, when the landing used the verified-unchanged path (not `doneByPush`), make the task comment, commit message, and check-run title say the task was already satisfied and ticked on the verification of the unchanged branch and name the passed checks; do not hold the pull request for review and leave the attempt marker format unchanged (FR-007, FR-006, research R6) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)
- [x] T007 [US1] In `.github/scripts/speckit-implement.test.mjs`, add tests for `taskNamedFiles` (backticked and path-like tokens, non-existent and unsafe paths dropped, distinct, capped) and for the land path: an empty change with passed verification, at least one check, no uncovered file, and the same head is ticked with no failed attempt, including the case of a non-workflow commit before the task's first attempt with workflow commits on top, and the comment, the commit message, and the check-run title all say it was already satisfied; also add a test where the first push is rejected because the branch moved and the empty patch is not re-applied (no `git apply` failure, the tick lands), for both the verified-unchanged path and the `doneByPush` path ; also add work-job tests: `runPackage` on an empty patch writes `result.taskFiles` and the same paths to `changed-files.txt` with `changedFiles` still `[]`, `runVerdict` on an empty patch with a failed or not-run verification writes `result.unchanged.status` `'failed'` or `'not-run'` while `result.reasons` and `result.verified` stay as they were, and a non-empty patch writes no `unchanged` object (FR-004, FR-007, FR-008, SC-001, SC-005) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)

**Checkpoint**: A verified, already-satisfied task is ticked (SC-001, SC-004).

---

## Phase 3: User Story 2 - An agent cannot skip a task whose work is missing (Priority: P1)

**Goal**: An empty change whose verification of the unchanged branch does not pass, does not cover the task, or ran on another head fails the attempt with a named cause and ticks nothing.

**Independent Test**: Run the land path for an empty change with a failing, missing, not-configured, or mismatched-head verification; the attempt fails, the task stays unticked, and the failure counts toward `MAX_TASK_ATTEMPTS`.

- [x] T008 [US2] In `runLand` of `.github/scripts/speckit-implement-work.mjs`, when an empty change is refused, extend the `the agent made no changes` reason with the first applicable cause: head mismatch (`unchanged.head` differs from the pull request's current head, as defined in T004), verification failed, verification not run or not configured, the task names no verifiable file, no qualifying check covered the task's files, no named file is specific to this task, or no specific named file differs from the base branch; the refusal is an ordinary failed attempt counted toward `MAX_TASK_ATTEMPTS` and does not requeue without consuming an attempt (FR-002, FR-003, FR-006) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)
- [x] T009 [US2] In `.github/scripts/speckit-implement.test.mjs`, add tests that an empty change fails with the named cause and leaves the task unticked for: failed verification, verification not run, verification not configured, a task naming no verifiable file, passed verification with no check or an uncovered named file, and a verification of a different head (the reason names the head mismatch and the attempt counts; the test moves the pull request's branch after the work job so the current head differs from `unchanged.head`, exercising the comparison against the pull request's current head, not the detached checkout); also add three cases: named files that exist unchanged on the base branch with a passed verification still fail the attempt, two tasks naming one file where an earlier task changed it and the agent skips the second task (refused with the cause "no named file is specific to this task"), and a task naming only existing Markdown files with a passed check named exactly "Markdown check" fails as an empty change; a task naming only an uncovered `.github/scripts` file is refused with the named cause (no qualifying check covered the task's files); assert `MAX_TASK_ATTEMPTS` and attempt counting are unchanged (FR-008, SC-002) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)

**Checkpoint**: No test ticks a task on a failing, missing, or mismatched verification.

---

## Phase 4: User Story 3 - Tasks that change files keep every existing rule (Priority: P2)

**Goal**: Changing tasks and the existing `doneByPush` exemption behave exactly as before.

**Independent Test**: Every existing land-path and `doneByPush` test passes with unchanged expectations.

- [x] T010 [US3] In `.github/scripts/speckit-implement.test.mjs`, add a test that an empty change with a failing named-file verification still lands through the existing `doneByPush` rule (a non-workflow commit at the head or after the first attempt), and confirm that the existing tests for changing tasks are unchanged; the full runs are left to T013 (FR-004, FR-005, SC-003) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)

**Checkpoint**: Existing results are unchanged.

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Documentation of the rule (FR-009) and final validation.

### Documentation

- [x] T011 [P] In `.github/workflows/README.md`, update the paragraph that states the `doneByPush` rule (find it by the phrase "lands without a change of its agent") to describe both ways an empty change lands (the non-workflow push at the head or after the task's first attempt, and the verified, already-satisfied path: passed verification with at least one check selected for the files the task names, on the head the land job lands on), that the comment and check run mark such a task as already satisfied, and that an agent that changes nothing for a task whose work is missing still fails and counts toward `MAX_TASK_ATTEMPTS` (FR-009) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)
- [x] T012 [P] In `AGENTS.md`, extend the sentence in the Spec Twins section beginning "A task may land without a change of its agent only when …" so it names the second, verified path next to `doneByPush`, and keep the rest of the file unchanged (research R7) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)

### Final validation

- [x] T013 Run `node --test .github/scripts/speckit-*.test.mjs` and `node .github/scripts/check-markdown.mjs` from the repository root; both MUST exit with code 0 with no finding (SC-003, quickstart) — implemented directly on main in 237a03c4 (repository tooling; not through the Spec Kit implementation)

---

## Dependencies & Execution Order

- Phase 1 (T001) blocks everything. T002–T006 and T008 all change `.github/scripts/speckit-implement-work.mjs`, so they run in order; T007, T009, and T010 all change `.github/scripts/speckit-implement.test.mjs`, so they run after their implementation tasks, in order.
- User Story 2 builds on the land-path change of User Story 1 (T004); User Story 3 only adds regression evidence.
- Phase 5: T011 and T012 touch different files and run in parallel; T013 runs after everything.

## Implementation Strategy

- Every task changes protected tooling paths under `.github/scripts/`, so this spec is implemented locally (`SPECKIT_IMPLEMENT_MODE=local`) or by a person's or Copilot's push, not by the unattended agent, and its pull request is held for review because those changes are covered by no check.

- MVP is User Story 1 (T001–T007): it lets tasks with a covered test or build check be ticked when their work was already pushed (tooling-only tasks under `.github/scripts/`, such as T004 and T005 of `specs/20261026-100223-environment-guard-archive-rewrites` (pull request #344), still need `doneByPush`). User Story 2 is required before the feature is safe to merge, because it keeps an agent from skipping a task whose work is missing.
