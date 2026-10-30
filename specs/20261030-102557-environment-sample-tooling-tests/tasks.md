# Tasks: Sample Tooling Tests in Environment Verification

**Input**: Design documents from `specs/20261030-102557-environment-sample-tooling-tests/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/sample-tooling-check.md](contracts/sample-tooling-check.md), [quickstart.md](quickstart.md)

**Tests**: Requested by the specification (FR-010, User Story 4): the action's own tests fail when the check, its triggers, its coverage, or its sharding change.

**Organization**: Grouped by user story. Only `.github/actions/environment-verify/` changes (plus this spec's own `tasks.md` ticks); `.github/actions/environment-setup`, `.github/scripts/`, workflows, and `tools/` stay unchanged (FR-009, FR-013). The check follows the existing "Spec Kit tooling tests" block in `action.yml`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel: consecutive `[P]` tasks under one heading run in parallel, each from the same commit, so their files are disjoint
- **[Story]**: User story the task belongs to (US1 to US4)

## Phase 1: Setup

- [x] T001 Run the existing action tests as a baseline with `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING=1 node --test .github/actions/environment-verify/verification-scope.test.mjs .github/actions/environment-verify/verification-plan.test.mjs .github/actions/environment-verify/analyst-projects.test.mjs` and `node --test tools/samples.test.mjs`, and confirm they pass before any change (no file changes)

---

## Phase 2: User Story 1 - Run the sample tooling tests as a verification check (Priority: P1) 🎯 MVP

**Goal**: A named check "Sample tooling tests" runs `node --test tools/samples.test.mjs` when one of exactly four `tools/` files changed (deletions included) and always in finalize mode, fails with the command and one recommended action, fails visibly when the test file or `node` is missing, and honors the nested-run guard.

**Independent Test**: Run the shell-level fixtures of `analyst-projects.test.mjs` with passing tests, failing tests, a missing `tools/samples.test.mjs`, the nested-run marker, finalize mode, and a change of an unrelated path.

- [x] T002 [US1] In `.github/actions/environment-verify/action.yml`, add `SAMPLE_TOOLING_TESTS: tools/samples.test.mjs` to the step `env` next to `SPECKIT_TOOLING_TESTS`, and add a block between "Spec Kit tooling tests" and "Markdown check" gated by `status` 0, `MAIN=1`, `SAMPLE_TOOLING_TESTS` set, `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` unset, and `applies '^tools/(samples\.test|samples-lib|upload-samples|download-samples)\.mjs$'`; it appends `Sample tooling tests` to `checks` before running, fails with `Sample tooling tests: node is required. Recommended action: run environment-setup before verification` when `node` is missing, with `Sample tooling tests: tools/samples.test.mjs does not exist. Recommended action: restore tools/samples.test.mjs and retry verification` when the file is missing, and runs `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING=1 node --test tools/samples.test.mjs`, printing `Sample tooling tests failed (command: node --test tools/samples.test.mjs). Recommended action: reproduce this command, correct the failing sample tooling file or its test without weakening the test, and retry verification` and setting `status=1` on failure; installs or downloads nothing; update the header text listing the checks (line 21) and the check order comment accordingly
- [x] T003 [US1] In `.github/actions/environment-verify/analyst-projects.test.mjs`, beside the "Spec Kit tooling tests" fixtures, add a `describe('Sample tooling tests')` block using `SAMPLE_TOOLING_TESTS` with a fixture workspace and tool bin: runs the exact command and lists the check for each of the four paths (a deletion included); fails with the reproduce-command message when the test fails; fails visibly with the missing-file message while another of the four files changes; fails with the `node is required` message without Node.js; skips and does not list the check for `tools/other.mjs`, `tools/samples.test.mjs.bak`, `tools/sub/samples-lib.mjs`, and `tools/Samples-Lib.mjs` in task and phase mode; runs in finalize mode without a changed sample tooling file; does not run while the nested-run marker is set or `SAMPLE_TOOLING_TESTS` is unset; and update the check-order assertion (line 1271) to include `Sample tooling tests` before the Markdown check

**Checkpoint**: The check runs, fails, and is skipped as specified; coverage is not yet claimed.

---

## Phase 3: User Story 2 - Count exactly the four sample tooling files as covered (Priority: P1)

**Goal**: A change of only the four files (plus a spec folder) reports no uncovered file; every other `tools/` path stays uncovered; protected paths stay protected.

**Independent Test**: Run the scope and plan rules on fixture change lists and assert covered, uncovered, and scope-reason outcomes.

- [x] T004 [US2] In `.github/actions/environment-verify/verification-scope.mjs`, export `SAMPLE_TOOLING_FILE = /^tools\/(?:samples\.test|samples-lib|upload-samples|download-samples)\.mjs$/` (anchored, case-sensitive) and make the scope reason for such a path read `<path> is a sample tooling file covered by the Sample tooling tests` instead of "is not Markdown"; `tools/*.md` stays covered by the Markdown rule and every other `tools/` path is not matched
- [x] T005 [US2] In `.github/actions/environment-verify/verification-plan.mjs`, import `SAMPLE_TOOLING_FILE` and add it to the covered patterns next to `TOOLING_FILE` (line 326) so the four paths are never listed in `uncovered`; add no `tools/` glob and leave protected-path handling untouched

### Scope and plan tests

- [x] T006 [P] [US2] In `.github/actions/environment-verify/verification-scope.test.mjs`, add cases for the exact four paths (reason names the Sample tooling tests), the non-matching paths `tools/other.mjs`, `tools/samples.test.mjs.bak`, `tools/sub/samples-lib.mjs`, and `tools/Samples-Lib.mjs`, and Markdown under `tools/` staying covered by the Markdown rule
- [x] T007 [P] [US2] In `.github/actions/environment-verify/verification-plan.test.mjs`, add cases that a change of only `tools/samples.test.mjs` plus a spec-folder file reports `uncovered` as `[]`, that each of the other three files is covered, that a mixed change with `tools/other.mjs` lists only that path as uncovered, and that `SAMPLE_TOOLING_FILE` and the `grep -E` pattern extracted from the `applies` call in `action.yml` (read the action text) give the same match result (behavioral equivalence, since their syntax differs) for the four paths and for `tools/other.mjs`, `tools/samples.test.mjs.bak`, `tools/sub/samples-lib.mjs`, and `tools/Samples-Lib.mjs`; protection is decided outside the plan module, so no protected-path case is asserted here

**Checkpoint**: Coverage matches exactly the four files, each backed by the check from User Story 1.

---

## Phase 4: User Story 3 - Fit the planning, scope, and sharding model (Priority: P2)

**Goal**: The check is a shared check of the first shard with its own budget, runs in exactly one shard, and a failure in any shard fails the verification.

**Independent Test**: Plan a sharded verification in a fixture and assert the budget, the single placement, and the failure aggregation.

- [x] T008 [US3] In `.github/actions/environment-verify/verification-plan.mjs`, raise `MAIN_MINUTES` from 5 to 6 with a comment that it includes the Sample tooling tests, so `packShards` accounts for the check; in `.github/actions/environment-verify/action.yml`, extend the shard log text (line 206) to `also the Python projects, environment action tests, Spec Kit tooling tests, Sample tooling tests, and Markdown check` and the shared-check comment (line 203)
- [x] T009 [US3] In `.github/actions/environment-verify/verification-plan.test.mjs`, update the shard-log assertion (line 652) to the new text, keep the budget assertions following `MAIN_MINUTES`, and add cases that the first shard's minutes include the sample tooling budget (`MAIN_MINUTES` is 6) and that the check text appears only in the shard `1/<count>` log; in `.github/actions/environment-verify/analyst-projects.test.mjs`, add a sharded fixture asserting the check runs in shard 1 only and that a failure in the check fails the step status

**Checkpoint**: Sharded runs neither drop nor duplicate the check.

---

## Phase 5: User Story 4 - Keep the new coverage proven and documented (Priority: P2)

**Goal**: The README documents the check, and the action's tests assert each rule.

**Independent Test**: Read the README section; check that the tests contain assertions for the pattern, the budget, and the check block, and that the action's test suite passes (T011); the assertions encode each rule, so a removed rule fails its case, which T011 and T013 do not mutate-test.

- [x] T010 [US4] In `.github/actions/environment-verify/README.md`, add a section "Sample tooling tests" after "Spec Kit tooling tests" documenting the check name, the command `node --test tools/samples.test.mjs`, the trigger (exactly the four files changed or deleted, always in finalize mode, never otherwise), the nested-run marker, the first-shard-only placement and its budget, the three failure messages, and the covered scope (exactly the four files, no broader `tools/` glob); update the check-order sentence (line 194), the `checks` description, the scope reason mention (line 71), and the covered-paths list (line 202) to include the four files and state that every other `tools/` path stays uncovered
- [x] T011 [US4] Verification only: run the environment-verify action in task mode over the files `.github/actions/environment-verify/analyst-projects.test.mjs`, `.github/actions/environment-verify/verification-scope.test.mjs`, and `.github/actions/environment-verify/verification-plan.test.mjs`, whose check Environment action tests runs the action's `node --test` suite, and confirm that the verification passes, that the check Environment action tests applied, and that no file is left uncovered; no file changes and no mutation run (completion claimed as verified) — already done by a push to the implementation branch (verified; no change by this task)

---

## Phase 6: Polish & Cross-Cutting Concerns

- [ ] T012 Update the documentation to the implemented state, only if needed: re-read `docs/architecture/` for `environment-verify`, verification coverage, and `tools/` statements, and only if a statement about `environment-verify`, verification coverage, or the `tools/` sample tooling became false through this feature, correct that statement; the plan expects `None`, so otherwise this task changes nothing and its completion is claimed as not applicable
- [ ] T013 Run `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING=1 node --test .github/actions/environment-verify/*.test.mjs`, `node --test tools/samples.test.mjs`, `node --test .github/scripts/speckit-*.test.mjs`, and `node .github/scripts/check-markdown.mjs --files .github/actions/environment-verify/README.md`; then run the quickstart plan check for `tools/samples.test.mjs` plus a spec-folder file and confirm `"uncovered": []`, and confirm `git status` shows changes only below `.github/actions/environment-verify/` and this spec folder (no change to `environment-setup`, `.github/scripts/`, workflows, or `tools/`)

---

## Dependencies & Execution Order

- Phase 1 (T001) first. User Story 1 (T002, T003) next: the check must run before any path is counted covered (FR-005). User Story 2 (T004 to T007) follows; T005 needs T004's export, and T007 needs T002 and T005. User Story 3 (T008, T009) after User Story 2 because both edit `verification-plan.mjs` and its test. User Story 4 (T010, T011) after the code tasks; T011 only verifies, without a file change, that the action's tests pass through the action's own Environment action tests check; T013 runs the raw `node --test` commands. Polish (T012, T013) last.
- Tasks are sequential because they share `action.yml`, `verification-plan.mjs`, and the test files; except the group "Scope and plan tests" (T006, T007), which runs in parallel: T006 changes only `verification-scope.test.mjs` and T007 only `verification-plan.test.mjs` (it reads `action.yml` without changing it).

## Implementation Strategy

1. MVP: User Story 1 (check runs and fails correctly) with its fixtures.
2. Add coverage (User Story 2), then the budget and shard text (User Story 3).
3. Document and prove the rules (User Story 4), then run the full validation (Polish).
