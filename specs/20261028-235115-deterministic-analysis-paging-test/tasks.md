# Tasks: Deterministic Keyboard Paging Test of the Connected Analysis Review

**Input**: Design documents from `/specs/20261028-235115-deterministic-analysis-paging-test/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md)

**Tests**: The feature is itself a test fix. Its verification tasks are runs of the Playwright `simulated` project (no Docker) as described in [quickstart.md](quickstart.md); no new test files are added.

**Organization**: One user story (US1, P1). All source changes live in `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts` unless the evidence of T002 puts the cause in the page. Temporary probes and delays are always removed again (FR-003, FR-004).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit, so they must have disjoint file footprints and no dependencies on each other
- **[Story]**: Which user story the task belongs to
- No task is marked `[P]`: every task touches the one spec file or `research.md`, or depends on the result of the preceding run.

## Phase 1: Setup

**Purpose**: Make the Web UI browser tests runnable.

- [x] T001 Install the Web UI dependencies and the pinned Playwright 1.63.0 Chromium in `src/clients/web` (`npm ci`, then the Playwright Chromium install if missing) and confirm `npx playwright test tests/e2e/simulated/analysis-review-access.spec.ts --project simulated -g "every control is reachable by keyboard"` starts and runs the test once — verified without changes by the assessed verification at a8ff400b (no change by this task)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish the cause with evidence before any fix (FR-001).

**⚠️ CRITICAL**: No fix in Phase 3 may start before the cause is recorded.

- [ ] T002 Reproduce the failure of the test "has no violations with facts, and every control is reachable by keyboard with visible focus" in `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts` as in quickstart step 3: run it under the CPU load and, in a temporary local-only change, with the delay defined in quickstart step 2 (a fixed 5000 ms on the facts request for `fromMs=10000`); keep a failing trace, read `src/clients/web/src/features/analysis/FactBrowser.tsx` and `src/clients/web/src/features/analysis/useFactPages.ts` to confirm which candidate cause of the spec produces it, remove the temporary delay again, and write what was observed (commands, failure counts, the trace's finding) as a new "Reproduction" subsection of the Evidence section of `specs/20261028-235115-deterministic-analysis-paging-test/research.md`; this task ends with that change to `research.md`, so it is never complete without a file change
- [ ] T003 Using the "Reproduction" subsection that T002 wrote to the Evidence section of `specs/20261028-235115-deterministic-analysis-paging-test/research.md` (do not repeat it), confirm or replace the "Expected cause" line there with the confirmed cause, or the contradicting cause and its location, and leave the Decisions section consistent with it

**Checkpoint**: The cause and the file it lives in are known and recorded.

---

## Phase 3: User Story 1 - The keyboard paging test fails only when the review is wrong (Priority: P1) 🎯 MVP

**Goal**: The test passes whenever a member can filter the facts by range and page forward and back by keyboard with visible focus, however loaded the runner is, and fails only when that behavior or its accessibility is broken.

**Independent Test**: 20 consecutive runs alone and 5 consecutive runs of the file under CPU load pass, including runs with the filtered facts request delayed; the two probes of quickstart step 4 each make the test fail on an unchanged assertion.

### Implementation for User Story 1

- [ ] T004 [US1] Fix the cause in `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts` (expected cause: the test acts on the previous selection): after the `toHaveURL` assertion following "Apply range", wait on observable conditions of the filtered page, namely `facts.first()` having `aria-label` matching `/: pass-1$/` (the unfiltered first page starts with `pass-0`) and `page.getByRole('button', { name: 'Next page' })` being enabled, inserted directly after the `toHaveURL` assertion and before the unchanged `toHaveCount(2)` and the "Next page" activation; keep every existing assertion and accessibility check (`expectNoAxeViolations`, `expectAllReachableByKeyboard`, `toBeFocused` and visible focus in `activateWithKeyboard`, `toHaveCount`) on the same states, use no retry, sleep, longer timeout, or `force`, and add a short code comment naming the evidenced cause; if T002/T003 put the cause in the page instead, change only `src/clients/web/src/features/analysis/FactBrowser.tsx` or `src/clients/web/src/features/analysis/useFactPages.ts` so the control stays mounted and focusable across the range reload, with no other behavior change
- [ ] T005 [US1] Run the test alone 20 times in a row (`npx playwright test tests/e2e/simulated/analysis-review-access.spec.ts --project simulated -g "every control is reachable by keyboard" --repeat-each 20 --workers 1` in `src/clients/web`) and confirm every run passes, recording the command and the pass count by creating the "Determinism runs" subsection of the Evidence section of `specs/20261028-235115-deterministic-analysis-paging-test/research.md` (this task ends with that change to `research.md`)
- [ ] T006 [US1] Run the whole file `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts` at least 5 times under the CPU load and once with the delay, both as defined in quickstart step 2 (a fixed 5000 ms on the filtered facts request, `fromMs=10000`), confirm every run passes, then remove the temporary delay, confirm with `git diff` that none remains, and record the run counts and results by appending to the "Determinism runs" subsection (created by T005) of the Evidence section of `specs/20261028-235115-deterministic-analysis-paging-test/research.md` (this task ends with that change to `research.md`)
- [ ] T007 [US1] Run the two probes of quickstart step 4 against `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts` (keep "Next page" disabled for the filtered selection, then move the focus away from the control), confirm each makes the test fail on an unchanged assertion, remove both probes, confirm with `git diff` that only the intended fix remains, and record each probe and the assertion it failed on in a "Probes" subsection of the Evidence section of `specs/20261028-235115-deterministic-analysis-paging-test/research.md` (this task ends with that change to `research.md`)
- [ ] T008 [US1] Run the rest of the Web UI browser suite (`npm run test:e2e -- --project simulated` in `src/clients/web`) and the Vitest unit tests (`npm test`), and confirm the results are unchanged except for the now deterministic test, and record the commands and result counts by appending to the "Determinism runs" subsection of the Evidence section of `specs/20261028-235115-deterministic-analysis-paging-test/research.md` (this task ends with that change to `research.md`)

**Checkpoint**: User Story 1 is complete and independently verified.

---

## Phase 4: Polish & Cross-Cutting Concerns

- [ ] T009 Complete the Evidence section of `specs/20261028-235115-deterministic-analysis-paging-test/research.md` with the final cause and a note that passing runs are development evidence only; no `docs/architecture/` change is needed because the plan's Architecture Updates is `None`
- [ ] T010 Validate Markdown with `node .github/scripts/check-markdown.mjs` from the repository root and fix any finding in `specs/20261028-235115-deterministic-analysis-paging-test/research.md` or this folder's other files
- [ ] T011 Confirm with `git status` and `git diff --stat` that only the file the cause lives in and `specs/20261028-235115-deterministic-analysis-paging-test/` changed, and that no assertion or accessibility check was removed

---

## Dependencies & Execution Order

- Phase 1 (T001) → Phase 2 (T002 → T003) → Phase 3 (T004 → T005 → T006 → T007 → T008) → Phase 4 (T009 → T010 → T011).
- T002 and T005 to T008 each end with a change to `research.md`, because a task that only runs experiments and changes no file cannot be completed by the automation (the assessment rejects it: CPU load, temporary edits, and traces cannot be re-executed by the trusted runner).
- T006 and T007 temporarily edit the same spec file as T004, so they run in order and never in parallel; each removes its temporary change before the next task.
- SC-004 (the merge verification of #318 no longer failing) is post-merge evidence and has no task.

## Parallel Opportunities

None: all tasks share the one spec file or depend on the preceding run's result.

## Implementation Strategy

- **MVP**: Phases 1 to 3 (User Story 1 is the whole feature).
- Establish the cause first (T002, T003), fix it where it lives (T004), prove determinism (T005, T006), prove the assertions still bite (T007), then widen to the rest of the suite (T008) and the documentation (T009, T010).
