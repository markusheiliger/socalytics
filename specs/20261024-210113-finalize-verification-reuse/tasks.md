---

description: "Task list for Finalize Verification Without Repeated Work"
---

# Tasks: Finalize Verification Without Repeated Work

**Input**: Design documents from `/specs/20261024-210113-finalize-verification-reuse/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/finalize-verification.md](contracts/finalize-verification.md), [quickstart.md](quickstart.md)

**Tests**: Requested by FR-011 and SC-005. They extend the existing `verification execution` block of the action's Node test file.

**Organization**: Tasks are grouped by user story. The whole change is confined to three files of `.github/actions/environment-verify/` (FR-010): `action.yml` (the "Run the checks" step script), `README.md`, and `analyst-projects.test.mjs`. Tasks that change the same file are sequential.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1 to US4)
- Include exact file paths in descriptions

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Establish the baseline the change must not break

No task in this phase: the baseline of the existing checks (SC-005, FR-009) is the verification the automation runs for every task and phase, and a task that changes no file cannot succeed there (T001 is retired).

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared helpers in the "Run the checks" script that every story uses

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T002 In `.github/actions/environment-verify/action.yml` ("Run the checks" script, before the Analyst Python helper is called), add the normalized changed-files helper per research R4 (strip `\r`, drop blank lines, sort unique, join with `\n`) with its `sha256` through `node -e` (Node `crypto`), kept in a variable next to the existing `changed` value; the existing `changed` variable and every existing use of it stay unchanged
- [x] T003 In `.github/actions/environment-verify/action.yml`, add the record-location helper per research R2 and the data model: `$RUNNER_TEMP/socalytics-environment-verify/finalize-<GITHUB_RUN_ID>-<GITHUB_RUN_ATTEMPT>-<GITHUB_JOB>.json`, each name part reduced to `[A-Za-z0-9_.-]` (any other character becomes `_`); the record is disabled (no read, no write) when `MODE` is not `finalize`, when `RUNNER_TEMP`, the run id, the attempt, or the job id is empty, or when `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` is set; when it is disabled because `RUNNER_TEMP`, the run id, the attempt, or the job id is empty, log that reuse was not possible; when it is disabled because `MODE` is not `finalize` or `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` is set, stay silent (FR-001, FR-014)

---

## Phase 3: User Story 1 - A conflict resolution that also self-tests the synchronized environment finishes in time (Priority: P1) 🎯 MVP

**Goal**: A later finalize run in the same job reuses the result of an earlier passing finalize run for the identical workspace state, mode, changed files, and scope, and executes no check.

**Independent Test**: Run the action in finalize mode twice in one job for an identical workspace; the second run executes no stub command, reports "Reused result", and ends with the same `checks`, `uncovered`, and `flaky` outputs and exit 0.

### Implementation for User Story 1

- [x] T004 [US1] In `.github/actions/environment-verify/action.yml`, add the workspace state identity function per research R3: `sha256(HEAD commit + "\n" + tree)` where the tree is `git write-tree` of a throw-away index (`GIT_INDEX_FILE` in a private temporary folder) filled by `git read-tree HEAD` and `git add -A -- . ':(exclude)<path>'` for the caller's `log-file` and `changed-files` when they lie inside the workspace; the real index and working tree are never touched; the identity is uncertain (empty value) when `git` fails, when the workspace is not the top level of a Git work tree, or when `git ls-files --others --ignored --exclude-standard --directory` lists an ignored path none of whose components is one of `bin`, `obj`, `node_modules`, `.venv`, `venv`, `__pycache__`, `dist`, `build`, `TestResults`, `test-results`, `playwright-report`, `.vs`, `.pytest_cache`, or `.uv-cache`, and that ends with none of `src/clients/web/src/generated`, `.specify/workflows/runs`, `.specify/feature.json`, `.specify/.workflow-install.lock`, or `.specify/extensions/*/local-config.yml` (matched as path suffixes, not as a bare component `generated`; the `.specify` paths are Spec Kit housekeeping, tooling state outside the build); an uncertain identity logs that reuse was not possible and disables the record (FR-001, FR-014)
- [x] T005 [US1] In `.github/actions/environment-verify/action.yml`, add the reuse decision at the start of a finalize run with a certain identity, before the Analyst Python helper runs: read the record and reuse it only when it parses, has `version` `1`, `passed` `true`, string `identity`, `mode`, `changedFiles`, `scope`, `checks`, `uncovered`, `flaky`, and a `source` object, and its `identity`, `mode`, `changedFiles`, and `scope` equal the run's own (research R6; until T011 introduces the reduction, the run's own scope is always `full`); a reused run executes no check, writes the record's `checks`, `uncovered`, and `flaky` to `GITHUB_OUTPUT`, writes a "Reused result" block to the log and a "Reused result" section to `GITHUB_STEP_SUMMARY` naming the earlier run (`source`: run id, attempt, job, record time, sequence), and exits 0; any read or parse failure falls through to a full run of the scope (FR-002, FR-003, FR-004)
- [x] T006 [US1] In `.github/actions/environment-verify/action.yml`, add the record write at the end of the script per research R6 and the data model: after the checks, recompute the identity (`E`) and, only when the run passed and `E` equals the start identity (`S`), write the record (`version` `1`, `passed` `true`, `identity`, `mode` `finalize`, `changedFiles`, `scope` (`full` until T011 introduces the reduction), `checks`, `uncovered`, `flaky`, `source` with `runId`, `attempt`, `job`, UTC ISO `recordedAt`, and `sequence` = the `sequence` of the valid record being replaced plus 1, and 1 when there is no valid record) to a temporary file in the record folder and `mv` it over the record; a passing run whose identity changed logs that no record was written and deletes an existing record; a failing run deletes an existing record and never writes one (FR-001, FR-005)
- [x] T007 [US1] In `.github/actions/environment-verify/analyst-projects.test.mjs`, inside `describe('verification execution')`, add a nested `describe('finalize reuse')` with its fixture helpers: turn the fixture repository into a Git repository (committed baseline, local user config), pass `RUNNER_TEMP` (a private folder), `GITHUB_RUN_ID`, `GITHUB_RUN_ATTEMPT`, and `GITHUB_JOB` through the existing `run` helper's `env` option, and count the stub `uv` calls (research R8); existing tests set none of these values and stay unchanged
- [x] T008 [US1] In the `finalize reuse` group of `.github/actions/environment-verify/analyst-projects.test.mjs`, add the tests for a reused result: a passing first finalize run followed by a second run with identical workspace, changed-files list, and scope executes no stub command, logs and summarizes "Reused result" naming the first run, exits 0, and reports the same `checks`, `uncovered`, and `flaky` outputs (including uncovered paths and a flaky test of the first run); the "executes no stub command" assertion is the executable evidence for FR-013 and SC-001; a changed-files list with the same files in another order, with CRLF line endings, and with blank lines is reused (FR-011, SC-001)

**Checkpoint**: Reuse works for the conflict resolution scenario; run the action tests.

---

## Phase 4: User Story 2 - A reused or reduced run never turns a failure or a gap into a pass (Priority: P1)

**Goal**: Anything uncertain, failed, missing, damaged, or mismatching is repeated in full and reports its own result.

**Independent Test**: With fixtures for a failed earlier run, no record, another workspace state, another mode, other changed files, and a damaged record, every finalize run executes every check of its scope and its verdict is its own.

### Tests for User Story 2

- [ ] T009 [US2] In the `finalize reuse` group of `.github/actions/environment-verify/analyst-projects.test.mjs`, add tests that every check runs in full and no result is reported as reused after: a failed first run (via `FAIL_STAGE`; the second run passes or fails on its own result, and the failed run leaves no record); no record; a record for a different mode (`task` and `phase` runs neither read nor write a record); a record for other changed-files content; a record for another scope; and a damaged record (empty, truncated JSON, `version` other than `1`, `passed` not `true`, a missing field) (FR-004, FR-005, FR-011, SC-003)
- [ ] T010 [US2] In the `finalize reuse` group of `.github/actions/environment-verify/analyst-projects.test.mjs`, add tests that a change of a single file (edited, added, removed, or committed between the runs) changes the identity and prevents reuse, and that an unchanged workspace after a first run is reused; add tests that an empty or missing `RUNNER_TEMP`, run id, attempt, or job id, a workspace without Git metadata, an ignored non-build file (for example an ignored configuration file), and `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` each run every check, write and read no record, and (for the first four) log that reuse was not possible; add a test that an ignored build folder such as `bin/` or `node_modules/` does not prevent reuse, that an ignored `src/clients/web/src/generated/` build output of the Web UI and an ignored `.specify/workflows/runs/` folder (the Spec Kit run folder of the resolve step) do not prevent reuse, that an ignored `.specify/feature.json` (Spec Kit housekeeping) does not prevent reuse, while an ignored configuration file still does, and that a record of another run id, attempt, or job is not reused (FR-001, FR-014, FR-011)

**Checkpoint**: A shortcut never produces a pass from a failed or missing result.

---

## Phase 5: User Story 3 - An environment-only change is verified by the environment checks alone (Priority: P2)

**Goal**: A finalize run whose changed files all lie in the two environment action folders runs only the Environment action tests and the Markdown check.

**Independent Test**: Finalize mode with changed files only in the two folders runs only those two checks and reports an empty `uncovered`; one outside file, a lookalike, a leaving path, or an empty list keeps the full scope.

### Implementation for User Story 3

- [ ] T011 [US3] In `.github/actions/environment-verify/action.yml`, add the scope decision per research R5 before the Analyst Python helper runs: scope `environment` only in `finalize` mode when the normalized list is non-empty and every path matches `^\.github/actions/environment-(setup|verify)/[^/]` and has no `.` or `..` component, no backslash, no leading `/`, and no empty component; otherwise scope `full` (task and phase modes never reduce); in the `environment` scope skip the Analyst Python helper call and the Analyst Python, platform, Analyst Manager, and Web UI checks, keep the Environment action tests and the Markdown check in their existing order, and write a log line and step summary section saying the scope was reduced to the environment actions and why; `checks` lists only checks that ran and `uncovered` stays computed from `COVERED` (FR-006, FR-007, FR-008)
- [ ] T012 [US3] In `.github/actions/environment-verify/action.yml`, replace the literal `full` scope in the record write of T006 and in the reuse comparison of T005 with the variable set by the scope decision of T011, so a reduced run's record is reused only by a later run of the same scope and otherwise the later run runs in full for its own scope (FR-002, edge case "both shortcuts apply")
- [ ] T013 [US3] In `.github/actions/environment-verify/analyst-projects.test.mjs`, inside `describe('verification execution')`, add a nested `describe('finalize scope')` with tests: changed files only in `.github/actions/environment-setup/` and `.github/actions/environment-verify/` run only Environment action tests and the Markdown check (no stub `uv` call, `checks` names exactly those two, `uncovered` empty, the log says the scope was reduced and why); a failing environment action test or Markdown check in the reduced scope fails and names the check; a file outside the folders, `.github/actions/environment-setup-x/file`, `.github/actions/environment-verify/../x`, an absolute path, a backslash path, and an empty or missing list each keep the full scope with the same checks and order as before; task and phase modes are not reduced; a reduced run's record is not reused by a full-scope run and the reverse, and is reused by a later reduced run (FR-011, SC-002, SC-004)

**Checkpoint**: Environment-only changes run the reduced scope; run the action tests.

---

## Phase 6: User Story 4 - Everything else about verification stays as it is (Priority: P2)

**Goal**: Unreduced, unreused runs and all unrelated checks behave exactly as before.

**Independent Test**: The existing Node tests of the action pass unchanged together with the new ones.

No task in this phase (T014 is retired, since a task that changes no file cannot succeed): the existing Node tests of the action run unedited in the verification of every task and phase (FR-009, SC-005); a failure there is corrected in `.github/actions/environment-verify/action.yml`, never in an existing test.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Documentation and final validation

- [ ] T015 [P] Update `.github/actions/environment-verify/README.md` per FR-012: describe the finalize reuse (what identifies a reusable result: workspace state identity, mode, changed-files content, and scope; where the record lives, `$RUNNER_TEMP/socalytics-environment-verify/finalize-<run id>-<attempt>-<job>.json`, and that it dies with the job, and that the record names no action copy so a self-test of another copy of the action reuses the earlier result), the reduced `environment` scope (the two folders, the two checks that run, the Agent Python projects check skipped, when the full scope runs), what is reported ("Reused result", reduced scope), when reuse is not possible, and that a failed, missing, damaged, or mismatching earlier result is always repeated in full; keep every other statement of the README as it is and state no production-readiness claim; in the same change, search `docs/architecture/` for `environment-verify` and `finalize` (the plan's Architecture Updates section is `None`) and, in the task's result summary, list the statements of the root `README.md` that the reuse and the reduced scope make approximate (the finalize mode running every check with all tests) as a follow-up for a documentation spec, without changing any file outside the action folder, and run the confinement check of `quickstart.md` section 3 (FR-010)

---

## Dependencies & Execution Order

- Phase 2 (T002, T003) blocks everything; all `action.yml` tasks (T002 to T006, T011, T012) are sequential because they change the same script, and the test tasks (T007 to T010, T013) are sequential because they change the same test file.
- US1 (T004 to T008) delivers the MVP. US2 (T009, T010) tests the safety of US1 and needs T004 to T006. US3 (T011 to T013) needs T005 and T006 (T012 extends them). US4 is covered by the verification of every task and phase, which runs the existing tests unedited.
- Polish: T015 (README and architecture check) needs the final behavior and comes last.

## Parallel Example

```text
Every task changes action.yml or the test file, so the phases run sequentially. T015 (README.md) is the only [P] task:
it is a group of one in Phase 7 and shares no file with its neighbours.
```

## Implementation Strategy

- **MVP**: Phases 1 to 3 (US1) remove the repeated run that exceeds the 90-minute job limit.
- Then add the safety tests (US2), the reduced scope (US3), the regression check (US4), and finish with the README and validation.
