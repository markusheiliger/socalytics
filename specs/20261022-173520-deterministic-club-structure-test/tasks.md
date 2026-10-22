# Tasks: Deterministic Club Structure Browser Test

**Input**: Design documents from `specs/20261022-173520-deterministic-club-structure-test/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md); [contracts/README.md](contracts/README.md) states that there is no contract.

**Tests**: The feature is the hardening of an existing browser test, so no new test files are added; verification is the repeated runs and failure probes of SC-001 and SC-002. All commands run from `src/clients/web` and need Docker for the `platform` projects.

**Organization**: One user story (US1, P1). Because the cause is a result of the implementation (FR-001), the reproduction phase comes first and the fix tasks apply only what its evidence shows. Tasks are sequential because they share `club-structure.spec.ts` and the evidence record.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit, so their files must not overlap (none in this list)
- **[Story]**: Which user story the task belongs to (US1)
- Constraints for every task: no retries, no skipping, no new or longer timeouts, no `waitForTimeout` or `networkidle` wait, and no assertion removed or loosened (FR-002, FR-003); temporary probes are removed before the task ends.

## Phase 1: Setup

- [ ] T001 Confirm the baseline in `src/clients/web`: run `npm ci`, install Playwright Chromium if missing, and run `npm run test:e2e -- --project platform-club-structure --trace on` once to prove the platform projects start and the chain (`platform-session` → `platform-navigation` → `platform-club-structure`) runs; change no file.

---

## Phase 2: Foundational (Reproduction, blocking)

**Purpose**: Establish the evidenced cause before any fix (FR-001). Nothing in Phase 3 starts before the evidence section exists.

- [ ] T002 Reproduce alone and under load: run the chain once with `npm run test:e2e -- --project platform-club-structure --trace on`, then repeat the test alone with `npm run test:e2e -- --project platform-club-structure --no-deps --trace on --repeat-each 20`, then start one `yes > /dev/null` process per core, run `--no-deps --repeat-each 5` the same way, and stop the load processes (`--repeat-each` without `--no-deps` would repeat `platform-session` too, which fails on its second repeat because its initial password was already changed). Known harness collision: `run` in `club-structure.spec.ts` is a module-level `Date.now()` value, so repeats in one worker reuse the same season and team names; until the names are derived per attempt (`run` computed inside the test or a per-repeat suffix from `test.info().repeatEachIndex`, applied as a scratch change that is reverted completely at the end of T002, so T004 probes the unchanged test; T005 applies it again), failures from duplicate names are harness failures and do not count as evidence of the flaky cause. Keep the traces of failing runs and read the failing step and the network order. Record the counts and findings in a new `## Evidence` section of `specs/20261022-173520-deterministic-club-structure-test/research.md`.
- [ ] T003 Reproduce after the chained projects: run `npm run test:e2e -- --project platform --trace on` five times and note whether the club structure result differs; append the result to the `## Evidence` section of `specs/20261022-173520-deterministic-club-structure-test/research.md`.
- [ ] T004 Confirm the cause with a temporary probe: in a scratch change of `src/clients/web/tests/e2e/platform/club-structure.spec.ts`, add a `page.route` handler that delays the team list and season list responses (candidates 1 to 3 of `research.md`) and, separately, start the test with a season already active (candidate 4) and with the previous attempt's names present (candidate 5); the candidate that fails the unchanged test deterministically is the evidenced cause. When no candidate reproduces, record that in `## Evidence` and either apply in T005 the cheapest candidate fix that the traces still support (for example waiting on the refetched list, or archiving active seasons before the test), naming it as a supported candidate rather than a reproduced cause, or record the result and continue with T005 applying only the unconditional parts (per-attempt names, ordering of the absence assertion), marking T006 and T007 as no change needed; the choice is recorded in `research.md`. Revert the scratch change completely (`git diff` of `club-structure.spec.ts` must be empty afterward) and record the confirmed cause, the rejected candidates, and the failing step in `specs/20261022-173520-deterministic-club-structure-test/research.md` under `## Evidence`.

**Checkpoint**: The evidenced cause is named; the fix applies only to it.

---

## Phase 3: User Story 1 - The club structure browser test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: The `platform-club-structure` test passes whenever a Club Admin can create and activate a season, create two teams, and edit one, however loaded the runner is and in whatever order the platform projects run, and fails only when that behavior is broken.

**Independent Test**: 20 consecutive runs alone, 5 under CPU load, and 5 after the other platform projects all pass; each of three probes makes the test fail on its unchanged assertions.

### Implementation

- [ ] T005 [US1] Remove the evidenced cause in `src/clients/web/tests/e2e/platform/club-structure.spec.ts` by controlling the racing input or waiting on the awaited condition, by kind of cause: for a stale cached team list or refetch order, observe the refreshed list (for example through the response of the team list request that follows the save, or the list state that only the refetch can show) before the absence assertion for `secondTeam`; for shared state, call `archiveActiveSeasons()` before the test as well as in `afterEach`; for external readiness, see T006. Whatever the evidenced cause, also derive the season and team names per attempt (compute `run` inside the test, or from a random part plus `test.info().repeatEachIndex`, which differs between `--repeat-each` repeats, unlike `test.info().retry`) so a rerun or second worker never sees the previous attempt's names, and when the cause is not the stale cached team list, still order the absence assertion for `secondTeam` provably after the refreshed list was shown; both follow the edge cases of `spec.md`. Keep every existing assertion, including `toHaveCount(0)` for `secondTeam`, and every existing `timeout` unchanged; write a code comment next to the fix that names the evidenced cause (FR-001, FR-003).
- [ ] T006 [US1] Apply the support-code part of the fix only where the evidence in `research.md` places it: in `src/clients/web/tests/e2e/support/platformState.ts` (shared state isolation or readiness wait before the first sign-in) or `src/clients/web/tests/e2e/support/fixtures.ts`, in a way that leaves the other platform specs behaviorally unchanged (FR-005); if the evidence places the cause only in the test, record in `specs/20261022-173520-deterministic-club-structure-test/research.md` that no support change was needed and change no support file.
- [ ] T007 [US1] Apply a screen change only if a trace shows that the screen, not the test, produces the race and shows a state that contradicts its documented behavior: change the smallest part of `src/clients/web/src/features/teams/` or `src/clients/web/src/features/seasons/` without changing the documented behavior of the season and team screens (FR-004); otherwise record in `specs/20261022-173520-deterministic-club-structure-test/research.md` that no production file changed and change none.

### Verification

- [ ] T008 [US1] Prove SC-001: run the chain once (`npm run test:e2e -- --project platform-club-structure`), then `npm run test:e2e -- --project platform-club-structure --no-deps --trace on --repeat-each 20` (all 20 pass; names are derived per attempt), the same with `--no-deps --repeat-each 5` under CPU load on every core (all pass; stop the load processes afterward), and `npm run test:e2e -- --project platform` five times (the club structure result passes each time), all without a retry; record the counts in the `## Evidence` section of `specs/20261022-173520-deterministic-club-structure-test/research.md`. When T006 changed a support file, also run `npm run test:e2e -- --project platform-operations` once (it runs the whole platform chain including `platform-registry`) and record in the same section that all platform specs pass.
- [ ] T009 [US1] Prove SC-002 with three temporary probes, one at a time, each reverted before the next: in the Web UI a team list that is not refreshed after creation (`src/clients/web/src/features/teams/`), a season that is not activated (`src/clients/web/src/features/seasons/`), and a save that does not persist the new name (`src/clients/web/src/features/teams/`); confirm each makes `platform-club-structure` fail on its unchanged assertions and not hang, and after each failing probe confirm that the club has no active season (the next probe or run activates a season without error), then revert every probe so `git diff` shows no probe remains; record the three outcomes in `specs/20261022-173520-deterministic-club-structure-test/research.md`.

**Checkpoint**: US1 is independently verified.

---

## Phase 4: Polish & Cross-Cutting Concerns

- [ ] T010 Run the regression checks in `src/clients/web`: `npm run build`, `npm test`, and `npm run test:e2e -- --project simulated`; fix only failures that the change caused.
- [ ] T011 Prove SC-003: review `git diff` against the base so that only the files the evidenced cause lives in and this spec folder changed, no probe or scratch route remains, no assertion of `src/clients/web/tests/e2e/platform/club-structure.spec.ts` is textually removed or loosened, and no `timeout` is lengthened; confirm the code comment names the evidenced cause and state that the commit message text, which names the same cause, is delivered as the final summary (agents do not commit) and that the cause comment in the test file is the persisted record.
- [ ] T012 Run `node .github/scripts/check-markdown.mjs --files specs/20261022-173520-deterministic-club-structure-test/research.md specs/20261022-173520-deterministic-club-structure-test/tasks.md` from the repository root and fix any finding.

Architecture updates: the plan states `None` (browser test code only, no design or implementation-status statement of `docs/architecture/` changes), so no documentation task references `docs/architecture/`. SC-004 is post-merge evidence observed when #225 retries and is not verified by a task of this spec.

---

## Dependencies & Execution Order

- Phase 1 (T001) → Phase 2 (T002 → T003 → T004) → Phase 3 (T005 → T006 → T007 → T008 → T009) → Phase 4 (T010 → T011 → T012).
- T005 to T007 depend on the evidence recorded by T004; T008 and T009 depend on the fix; T011 depends on all earlier tasks.
- No task is marked `[P]`: they all touch `club-structure.spec.ts`, the shared `research.md` evidence record, or the same running platform instance.

## Implementation Strategy

- **MVP**: Phases 1 to 3 (US1 is the only story); Phase 4 closes the evidence and the scope check.
- Reproduce first, fix only the evidenced cause, then prove with repeated runs and failure probes; stop and record the evidence in `research.md` if the cause is not reproducible, rather than adding retries, longer timeouts, or weaker assertions.
