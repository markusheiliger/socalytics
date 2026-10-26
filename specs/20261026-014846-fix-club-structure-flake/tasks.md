# Tasks: Remove the Remaining Flakiness of the Club Structure Browser Test

**Input**: Design documents from `specs/20261026-014846-fix-club-structure-flake/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md), [contracts/README.md](contracts/README.md)

**Tests**: No new test files are added. The existing test `tests/e2e/platform/club-structure.spec.ts` is the subject; its evidence is the repeated runs and probes of SC-001 and SC-002.

**Organization**: One user story (US1). All commands run in `src/clients/web` (Docker running, Node.js 24, Playwright 1.63.0 Chromium, Kiota 1.36.0 on `PATH`). Temporary scratch files and probes are removed before the end (T016). Nothing is written under `specs/` by these tasks.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies); this spec has none, because every task shares one AppHost or the same few files
- **[Story]**: US1

## Phase 1: Setup

- [ ] T001 Confirm the baseline in `src/clients/web`: `npm ci`, `npm run build`, and `npm test` pass before any change (no file changes)

---

## Phase 2: Foundational (Reproduce and identify the cause, FR-001)

**Purpose**: Establish by reproduction which cause still produces the failure after the two earlier hardenings. These tasks change no committed file; scratch files are removed in T016. Record the findings (failing step, network order, why the earlier hardening did not prevent it) for the code comment of T008.

**⚠️ CRITICAL**: No fix task starts before T007 names the cause, or states that it was not reproduced.

- [ ] T006 Read the recorded failures of the implementation pull requests #235, #284, and #314 with `gh run view --log` and `gh run download` (where still available), list the candidates their traces and logs support, and note the failing step, network order, and logs found there; they decide which candidates T005 widens first
- [ ] T002 Reproduce alone: run the dependency projects once with `npm run test:e2e -- --project platform-club-structure --trace on` in `src/clients/web`, then, against the same running AppHost, repeat only the target with `npm run test:e2e -- --project platform-club-structure --no-deps --repeat-each 20 --trace on`, and keep the traces of every failing run. Capture the AppHost and platform resource logs of every failing run next to its Playwright traces.
- [ ] T003 Reproduce under CPU load: start one busy-loop process per core, run `npm run test:e2e -- --project platform-club-structure --trace on` at least 5 times, stop the processes. Capture the AppHost and platform resource logs of every failing run next to its Playwright traces.
- [ ] T004 Reproduce after its predecessors: run `npm run test:e2e -- --project platform --trace on` at least 5 times of `tests/e2e/platform/club-structure.spec.ts`; these `--project platform` runs satisfy both the "after its predecessors" and the "whole platform chain" measures of SC-001. Capture the AppHost and platform resource logs of every failing run next to its Playwright traces.
- [ ] T005 Widen the candidate races of research.md in a scratch copy `src/clients/web/tests/e2e/platform/club-structure.scratch.spec.ts` (removed in T016): a temporary `page.route` delay on `POST /api/v1/seasons/*/teams` (candidate 1), on the earlier team list GET (candidate 2), on the seasons list GET (candidate 4), and a service worker `controllerchange` reload check from the traces (candidate 3); run each against the unchanged assertions with `npm run test:e2e -- --project platform-club-structure`
- [ ] T007 Decide from T002 to T006 the evidenced cause (or that the failure was not reproduced and which candidates the recorded failures support), where it lives (test, `src/clients/web/tests/e2e/support/platformState.ts`, `src/clients/web/tests/e2e/support/fixtures.ts`, `src/clients/web/src/features/teams/`, `src/clients/web/src/features/seasons/`, `src/clients/web/src/serviceWorker/`, or platform code), and why the earlier hardening (per-attempt names, archiving active seasons, the absence assertion after the refreshed list response) did not remove it. Give a one-line verdict per research.md candidate 1 to 6 (confirmed, rejected with the evidence, or untestable). If the failure is not reproduced and the recorded logs of #235, #284, and #314 are no longer available or support no candidate, the implementation changes no behavior and only updates the code comment in `src/clients/web/tests/e2e/platform/club-structure.spec.ts` to state that the failure was not reproduced and that no logs were available; T011 to T015 then run as confirmation of the unchanged test

**Checkpoint**: The cause, or the supported candidates, is named; only then fix it.

---

## Phase 3: User Story 1 - The club structure test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: The club structure test passes whenever a Club Admin can create and activate a season, create two teams, and edit one, however loaded the runner is and in whatever order the platform projects run, and fails only when that behavior is broken.

**Independent Test**: 20 consecutive passing runs of the project alone, 5 under CPU load, 5 after the platform projects and in the whole `platform` chain; each of the three probes of SC-002 fails the unchanged assertions without hanging.

### Fix the evidenced cause

Apply only the tasks T007 names; mark the others as not applicable in the pull request notes. They change the same files or depend on each other, so they run in order. Wait on the awaited condition or control the racing input; no retries, skipping, fixed sleeps, new or longer timeouts, `networkidle`, or removed or loosened assertions (FR-002, FR-003).

- [ ] T008 [US1] Fix the evidenced test-side cause in `src/clients/web/tests/e2e/platform/club-structure.spec.ts`, for example: observe the first team in the list (or wait for its creation response) before the second creation, so the `The team was created.` alert of the first iteration cannot satisfy the second iteration's assertion (candidate 1); register the post-rename list wait only after the save was shown and match the team list GET of the chosen season id (candidate 2); wait for the service worker to control the page before the test acts (candidate 3); or wait for the activated season option in the seasons list before `selectOption` (candidate 4). Keep every assertion and every existing `timeout` unchanged, and update the comment block above `beforeEach`: keep the still-valid evidence of the earlier hardening and add the evidenced cause (or the not-reproduced statement with the candidates supported by #235, #284, and #314) and why the earlier hardening did not remove it; if the fix lives in another file (T009, T010), that file gets the code comment next to its fix and this block only points to it (FR-001, SC-003)
- [ ] T009 [US1] Only if T007 puts the cause in shared state: isolate it in `src/clients/web/tests/e2e/support/platformState.ts` (and `src/clients/web/tests/e2e/support/fixtures.ts` only if the cause is there) so that, through the club structure operations of the platform, no season is active when the test starts and the cleanup that runs after the test, also after a failure, leaves the club without an active season (FR-007); keep the behavior of the other platform specs that use these helpers unchanged (FR-005); and record the evidenced cause and why the earlier hardening did not remove it in a code comment next to the change (FR-001, SC-003)
- [ ] T010 [US1] Only if T007 shows that a Web UI or platform file produces the race: fix it in the named file under `src/clients/web/src/features/teams/`, `src/clients/web/src/features/seasons/`, `src/clients/web/src/serviceWorker/`, or the platform code, without changing what a member sees or can do on the season and team screens or the club structure operations' documented results, concurrency, or audit behavior (FR-004); and record the evidenced cause and why the earlier hardening did not remove it in a code comment next to the change (FR-001, SC-003)

### Prove the fix

- [ ] T011 [US1] Run the dependency projects once with `npm run test:e2e -- --project platform-club-structure --trace on` in `src/clients/web`, then, against the same running AppHost, run `npm run test:e2e -- --project platform-club-structure --no-deps --repeat-each 20 --trace on`: all 20 runs of the target pass without a retry (SC-001)
- [ ] T012 [US1] Run the project under CPU load (one busy-loop process per core, stopped afterward) at least 5 times: every run passes (SC-001)
- [ ] T013 [US1] Run `npm run test:e2e -- --project platform` at least 5 times: the club structure test passes each time without a rerun; these runs satisfy both the "after its predecessors" and the "whole platform chain" measures of SC-001
- [ ] T014 [US1] Apply each of the three temporary probes of SC-002 one at a time (team list not refreshed after creation, season not activated, save not persisting the new name) in the Web UI sources under `src/clients/web/src/features/`, run `npm run test:e2e -- --project platform-club-structure`, confirm the test fails on its unchanged assertions and does not hang, and revert each probe before the next (FR-003)
- [ ] T015 [US1] If T009 changed shared support (`platformState.ts` or `fixtures.ts`), find the other platform specs that import the changed helper and confirm they pass unchanged in the T013 `--project platform` runs (FR-005); also confirm unconditionally that `tests/e2e/platform/club-structure.spec.ts` still calls `archiveActiveSeasons` in `test.beforeEach` and `test.afterEach` (through the club structure operations of the platform) and that a forced mid-test failure probe leaves the club without an active season (FR-007)

**Checkpoint**: US1 is complete and independently verified.

---

## Phase 4: Polish & Cross-Cutting Concerns

- [ ] T016 Remove every scratch file and probe (`src/clients/web/tests/e2e/platform/club-structure.scratch.spec.ts`, probe edits under `src/clients/web/src/features/`), then check with `git status` and `git diff` that only the files the cause lives in changed and that no assertion of `src/clients/web/tests/e2e/platform/club-structure.spec.ts` was textually removed (SC-003)
- [ ] T017 Run `npm run build` and `npm test` in `src/clients/web` and confirm both pass; no `docs/architecture/` change is needed (plan.md: Architecture Updates is `None`)

---

## Dependencies & Execution Order

- Phase 1 → Phase 2 → Phase 3 → Phase 4.
- T002 to T005 share one platform instance and the AppHost ports, so they run one after another; T006 reads recorded runs only and is done first, before T002; its findings decide which candidates T005 widens first.
- T008, T009, and T010 are sequential and conditional on T007's finding.
- T011 to T015 need the fix and share one AppHost, so they run one after another.
- SC-004 (the verification of #314 reports no flaky `platform-club-structure` test) is post-merge evidence and has no task here.

## Implementation Strategy

MVP is US1 itself: reproduce (Phase 2), fix only the evidenced cause (T008, with T009 or T010 only if the evidence says so), then prove it with the SC-001 runs and SC-002 probes. If the failure cannot be reproduced, harden only the candidates the recorded failures support and say so in the code comment.
