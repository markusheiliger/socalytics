# Tasks: Deterministic Keyboard Paging Test of the Connected Analysis Review

**Input**: Design documents from `/specs/20261028-235115-deterministic-analysis-paging-test/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md)

**Tests**: The feature is itself a test fix. Its verification tasks are runs of the Playwright `simulated` project (no Docker) as described in [quickstart.md](quickstart.md); no new test files are added.

**Organization**: One user story (US1, P1). All source changes live in `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts` unless the evidence of T002 puts the cause in the page. Temporary probes and delays are always removed again (FR-003, FR-004). No task writes to `research.md` or any other file under `specs/`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit, so they must have disjoint file footprints and no dependencies on each other
- **[Story]**: Which user story the task belongs to
- No task is marked `[P]`: every task touches the one test file or depends on the result of the preceding run.

## Phase 1: Setup

**Purpose**: Make the Web UI browser tests runnable.

- [x] T001 Install the Web UI dependencies and the pinned Playwright 1.63.0 Chromium in `src/clients/web` (`npm ci`, then the Playwright Chromium install if missing) and confirm `npx playwright test tests/e2e/simulated/analysis-review-access.spec.ts --project simulated -g "every control is reachable by keyboard"` starts and runs the test once — verified without changes by the assessed verification at a8ff400b (no change by this task)

---

## Phase 2: User Story 1 - The keyboard paging test fails only when the review is wrong (Priority: P1) 🎯 MVP

**Goal**: The test passes whenever a member can filter the facts by range and page forward and back by keyboard with visible focus, however loaded the runner is, and fails only when that behavior or its accessibility is broken.

**Independent Test**: 20 consecutive runs alone and 5 consecutive runs of the file under CPU load pass, including runs with the filtered facts request delayed; the two probes of quickstart step 4 each make the test fail on an unchanged assertion.

### Implementation for User Story 1

- [x] T002 [US1] Establish the cause with evidence and fix it in `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts`. First reproduce the failure of the test "has no violations with facts, and every control is reachable by keyboard with visible focus" as in quickstart steps 2 and 3 (under CPU load and, in a temporary local-only change, with a fixed 5000 ms delay on the facts request for `fromMs=10000`), keep a failing trace, and read `src/clients/web/src/features/analysis/FactBrowser.tsx` and `src/clients/web/src/features/analysis/useFactPages.ts` to confirm the cause. Then fix it (expected cause: the test acts on the previous selection): after the `toHaveURL` assertion following "Apply range", wait on observable conditions of the filtered page, namely `facts.first()` having `aria-label` matching `/: pass-1$/` (the unfiltered first page starts with `pass-0`) and `page.getByRole('button', { name: 'Next page' })` being enabled, inserted directly after the `toHaveURL` assertion and before the unchanged `toHaveCount(2)` and the "Next page" activation; keep every existing assertion and accessibility check (`expectNoAxeViolations`, `expectAllReachableByKeyboard`, `toBeFocused` and visible focus in `activateWithKeyboard`, `toHaveCount`) on the same states, use no retry, sleep, longer timeout, or `force`, and add a short code comment naming the evidenced cause and the observed failure counts. If the reproduction puts the cause in the page instead, change only `src/clients/web/src/features/analysis/FactBrowser.tsx` or `src/clients/web/src/features/analysis/useFactPages.ts` so the control stays mounted and focusable across the range reload, with no other behavior change. Afterwards run the whole file at least 5 times under the CPU load and once with the delay (quickstart step 2), then the two probes of quickstart step 4 (each must fail on an unchanged assertion), remove the temporary delay and both probes, and confirm with `git diff` that only the fix remains; report the commands, failure counts, the trace's finding, and the probe results in the final summary of the task
- [x] T003 [US1] Run the test alone 20 times in a row (`npm run test:e2e -- tests/e2e/simulated/analysis-review-access.spec.ts --project simulated -g "every control is reachable by keyboard" --repeat-each 20 --workers 1` in `src/clients/web`) and confirm every run passes — verified without changes by the assessed verification at ae68bc45 (no change by this task)
- [ ] T004 [US1] Run the rest of the Web UI browser suite (`npm run test:e2e -- --project simulated` in `src/clients/web`) and the Vitest unit tests (`npm test`), and confirm the results are unchanged except for the now deterministic test

**Checkpoint**: User Story 1 is complete and independently verified.

---

## Dependencies & Execution Order

- Phase 1 (T001) → Phase 2 (T002 → T003 → T004).
- No task changes anything under `specs/`, `.specify/`, or `.github/`: the implementation automation may not change those paths, so experiments, probes, and verification runs are not written to `research.md`. The cause and the run evidence are recorded in the code comment next to the fix (FR-001) and in the report of the task, which the automation posts on the pull request.
- T002 is the only task that changes a file (the one file the cause lives in); T003 and T004 only run commands that the trusted runner can verify, so they complete without a change.
- SC-004 (the merge verification of #318 no longer failing) is post-merge evidence and has no task.

## Parallel Opportunities

None: all tasks share the one spec file or depend on the preceding run's result.

## Implementation Strategy

- **MVP**: Phases 1 and 2 (User Story 1 is the whole feature).
- Establish the cause and fix it where it lives (T002), prove determinism (T003), then widen to the rest of the suite (T004).
