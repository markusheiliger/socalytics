# Tasks: Remove the Remaining Flakiness of the Club Structure Browser Test

**Input**: Design documents from `specs/20261026-014846-fix-club-structure-flake/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md), [contracts/README.md](contracts/README.md)

**One task**: the implementation lands a task only with a committed change (an empty change fails the attempt, as T001 did twice), and baseline, reproduction, proof, and cleanup change no committed file. They are therefore numbered steps of T008, the one task that commits a change.

**Tests**: No new test files are added. The existing test `tests/e2e/platform/club-structure.spec.ts` is the subject; its evidence is the repeated runs and probes of SC-001 and SC-002.

**Organization**: One user story (US1). All commands run in `src/clients/web` (Docker running, Node.js 24, Playwright 1.63.0 Chromium, Kiota 1.36.0 on `PATH`). Temporary scratch files and probes are removed in the last steps of T008. Nothing is written under `specs/` by this task.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies); this spec has none, because its one task shares one AppHost and the same few files
- **[Story]**: US1

## Phase 1: Fix the Flakiness (User Story 1, Priority: P1) 🎯 MVP

**Goal**: The club structure test passes whenever a Club Admin can create and activate a season, create two teams, and edit one, however loaded the runner is and in whatever order the platform projects run, and fails only when that behavior is broken.

**Independent Test**: 20 consecutive passing runs of the project alone, 5 under CPU load, 5 after the platform projects and in the whole `platform` chain; each of the three probes of SC-002 fails the unchanged assertions without hanging.

**Rules**: Wait on the awaited condition or control the racing input; no retries, skipping, fixed sleeps, new or longer timeouts, `networkidle`, or removed or loosened assertions (FR-002, FR-003). No fix starts before step 7 names the cause, or states that it was not reproduced. Apply only the fix step 7 names (8a, 8b, or 8c) and say in the pull request notes which are not applicable. Steps 1 to 7 change no committed file; scratch files are removed in step 14. Record the findings of steps 2 to 7 (failing step, network order, why the earlier hardening did not prevent it) for the code comment of step 8a.

- [x] T008 [US1] Reproduce, fix, and prove the club structure test in `src/clients/web`, committing only the files the cause lives in; the committed change always includes the updated comment block above `beforeEach` in `src/clients/web/tests/e2e/platform/club-structure.spec.ts` (FR-001, SC-003). Run the steps in order:

  Baseline and reproduction:

  1. Confirm the baseline in `src/clients/web`: `npm ci`, `npm run build`, and `npm test` pass before any change (no file changes)
  2. Read the recorded failures of the implementation pull requests #235, #284, and #314 with `gh run view --log` and `gh run download` (where still available), list the candidates their traces and logs support, and note the failing step, network order, and logs found there; they decide which candidates step 6 widens first
  3. Reproduce alone: start the AppHost once and keep it running in a separate terminal (`npm run build`, then `SOCALYTICS_APPHOST_MODE=e2e dotnet run --project ../../platform/SocAlytics.Platform.AppHost` in `src/clients/web`, with the same `SOCALYTICS_E2E_ADMIN_PASSWORD` as the Playwright config, until `http://127.0.0.1:4174` answers; with `CI` unset, Playwright reuses it), run the dependency projects once with `npm run test:e2e -- --project platform-club-structure --trace on`, then, against that same running AppHost, repeat only the target with `npm run test:e2e -- --project platform-club-structure --no-deps --repeat-each 20 --trace on`, and keep the traces of every failing run. Capture the AppHost and platform resource logs of every failing run next to its Playwright traces. When the 20 repeats finish, stop the kept-running AppHost with `Ctrl+C`.
  4. Reproduce under CPU load: start the busy-loop processes of the CPU load recipe in [quickstart.md](quickstart.md), run `npm run test:e2e -- --project platform-club-structure --trace on` at least 5 times, stop the processes with `kill` of the recorded PIDs; each run starts and stops its own AppHost on a fresh platform (no AppHost listening on 4174). Capture the AppHost and platform resource logs of every failing run next to its Playwright traces.
  5. Reproduce after its predecessors: run the whole platform chain with `npm run test:e2e -- --project platform --trace on` at least 5 times and read the result of `tests/e2e/platform/club-structure.spec.ts` in each run; each run starts and stops its own AppHost on a fresh platform (no AppHost listening on 4174, as in step 3); these `--project platform` runs satisfy both the "after its predecessors" and the "whole platform chain" measures of SC-001. Capture the AppHost and platform resource logs of every failing run next to its Playwright traces.
  6. Widen the candidate races of research.md in a scratch copy `src/clients/web/tests/e2e/platform/scratch/club-structure.spec.ts` (relative imports adjusted, so the project's `testMatch` picks it up; removed in step 14): a temporary `page.route` delay on `POST /api/v1/seasons/*/teams` (candidate 1), on the earlier team list GET (candidate 2), on the seasons list GET (candidate 4), and a service worker `controllerchange` reload check from the traces (candidate 3); run each against the unchanged assertions with `npm run test:e2e -- --project platform-club-structure`, on a fresh platform with no AppHost listening on 4174; each run must show the scratch test executed (not "no tests found")
  7. Decide from steps 2 to 6 the evidenced cause (or that the failure was not reproduced and which candidates the recorded failures support), where it lives (test, `src/clients/web/tests/e2e/support/platformState.ts`, `src/clients/web/tests/e2e/support/fixtures.ts`, `src/clients/web/src/features/teams/`, `src/clients/web/src/features/seasons/`, `src/clients/web/src/serviceWorker/`, or platform code), and why the earlier hardening (per-attempt names, archiving active seasons, the absence assertion after the refreshed list response) did not remove it. Check that the team and season names of the test include the attempt-unique parts (`Date.now` and `repeatEachIndex`) and that the step 3 `--repeat-each 20` run leaves no list assertion depending on other attempts' data. Give a one-line verdict per research.md candidate 1 to 6 (confirmed, rejected with the evidence, or untestable). If the failure is not reproduced and the recorded logs of #235, #284, and #314 are no longer available or support no candidate, the implementation changes no behavior and only updates the code comment in `src/clients/web/tests/e2e/platform/club-structure.spec.ts` to state that the failure was not reproduced and that no logs were available; steps 9 to 13 then run as confirmation of the unchanged test

  Fix only the evidenced cause (FR-004, FR-005, FR-007):

  8a. Fix the evidenced test-side cause in `src/clients/web/tests/e2e/platform/club-structure.spec.ts`, for example: observe the first team in the list (or wait for its creation response) before the second creation, so the `The team was created.` alert of the first iteration cannot satisfy the second iteration's assertion (candidate 1); register the post-rename list wait only after the save was shown and match the team list GET of the chosen season id (candidate 2); wait for the service worker to control the page before the test acts (candidate 3); or wait for the activated season option in the seasons list before `selectOption` (candidate 4). Keep every assertion and every existing `timeout` unchanged, and update the comment block above `beforeEach`: keep the still-valid evidence of the earlier hardening and add the evidenced cause (or the not-reproduced statement with the candidates supported by #235, #284, and #314) and why the earlier hardening did not remove it; if the fix lives in another file (step 8b, step 8c), that file gets the code comment next to its fix and this block only points to it (FR-001, SC-003)
  8b. Only if step 7 puts the cause in shared state: isolate it in `src/clients/web/tests/e2e/support/platformState.ts` (and `src/clients/web/tests/e2e/support/fixtures.ts` only if the cause is there) so that, through the club structure operations of the platform, no season is active when the test starts and the cleanup that runs after the test, also after a failure, leaves the club without an active season (FR-007); keep the behavior of the other platform specs that use these helpers unchanged (FR-005); and record the evidenced cause and why the earlier hardening did not remove it in a code comment next to the change (FR-001, SC-003)
  8c. Only if step 7 shows that a Web UI or platform file produces the race: fix it in the named file under `src/clients/web/src/features/teams/`, `src/clients/web/src/features/seasons/`, `src/clients/web/src/serviceWorker/`, or the platform code, without changing what a member sees or can do on the season and team screens or the club structure operations' documented results, concurrency, or audit behavior (FR-004); and record the evidenced cause and why the earlier hardening did not remove it in a code comment next to the change (FR-001, SC-003)

  Prove the fix:

  Step 9: Start the AppHost once and keep it running in a separate terminal exactly as in step 3, run the dependency projects once with `npm run test:e2e -- --project platform-club-structure --trace on`, then, against that same running AppHost, run `npm run test:e2e -- --project platform-club-structure --no-deps --repeat-each 20 --trace on`: all 20 runs of the target pass without a retry (SC-001); when the 20 repeats finish, stop the kept-running AppHost with `Ctrl+C`
  Step 10: Run the project under CPU load (the CPU load recipe in [quickstart.md](quickstart.md), processes stopped afterward) at least 5 times: every run starts and stops its own AppHost on a fresh platform (no AppHost listening on 4174) and passes (SC-001)
  Step 11: Run `npm run test:e2e -- --project platform` at least 5 times: each run starts and stops its own AppHost on a fresh platform (no AppHost listening on 4174, as in step 3); the club structure test passes each time without a rerun; these runs satisfy both the "after its predecessors" and the "whole platform chain" measures of SC-001
  Step 12: Apply each of the three temporary probes of SC-002 one at a time (team list not refreshed after creation, season not activated, save not persisting the new name) in the Web UI sources under `src/clients/web/src/features/`, run `npm run test:e2e -- --project platform-club-structure` on a fresh platform with no AppHost listening on 4174, confirm the test fails on its unchanged assertions and does not hang, and revert each probe before the next (FR-003)
  Step 13: If step 8b changed shared support (`platformState.ts` or `fixtures.ts`), find the other platform specs that import the changed helper and confirm they pass unchanged in the step 11 `--project platform` runs (FR-005); also confirm unconditionally that `tests/e2e/platform/club-structure.spec.ts` still calls `archiveActiveSeasons` in `test.beforeEach` and `test.afterEach` (through the club structure operations of the platform) and, with a forced mid-test failure probe (a temporary `throw` in a scratch copy `src/clients/web/tests/e2e/platform/failure-probe/club-structure.spec.ts` of the test with relative imports adjusted, so the project's `testMatch` picks it up and the run shows the probe executed (not "no tests found"), never in the committed test or support code), that the club is left without an active season (FR-007); if that confirmation of `archiveActiveSeasons` in `beforeEach` and `afterEach` or the forced mid-test failure probe shows the club keeps an active season, step 8b applies regardless of step 7's verdict

  Clean up and confirm:

  Step 14: Remove every scratch file and probe (the folders `src/clients/web/tests/e2e/platform/scratch/` of step 6 and `src/clients/web/tests/e2e/platform/failure-probe/` of step 13, probe edits under `src/clients/web/src/features/`), then check with `git status` and `git diff` that only the files the cause lives in changed and that no assertion of `src/clients/web/tests/e2e/platform/club-structure.spec.ts` was textually removed (SC-003)
  Step 15: Run `npm run build` and `npm test` in `src/clients/web` and confirm both pass; no `docs/architecture/` change is needed (plan.md: Architecture Updates is `None`)

**Checkpoint**: US1 is complete and independently verified.

---

## Dependencies & Execution Order

- T008 is the only task; its numbered steps run in order.
- Step 2 reads recorded runs only and is done first; its findings decide which candidates step 6 widens first. Steps 3 to 6 share the AppHost ports, so they run one after another.
- Steps 8a, 8b, and 8c are sequential and conditional on step 7's finding.
- Steps 9 to 15 need the fix and run one after another.
- The AppHost kept running in steps 3 and 9 is stopped (`Ctrl+C`) when the 20 repeats finish; every other run (steps 4 to 6 and 10 to 14) runs with no AppHost listening on 4174, so each starts and stops its own AppHost on a fresh platform.
- SC-004 (the verification of #314 reports no flaky `platform-club-structure` test) is post-merge evidence and has no task here.

## Implementation Strategy

MVP is US1 itself: reproduce (steps 1 to 7), fix only the evidenced cause (step 8a, with 8b or 8c only if the evidence says so), then prove it with the SC-001 runs and SC-002 probes. If the failure cannot be reproduced, harden only the candidates the recorded failures support and say so in the code comment.
