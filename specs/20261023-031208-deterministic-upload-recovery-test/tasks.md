# Tasks: Deterministic Upload Recovery Browser Test

**Input**: Design documents from `/specs/20261023-031208-deterministic-upload-recovery-test/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [contracts/fake-store-control.md](contracts/fake-store-control.md), [quickstart.md](quickstart.md)

**Tests**: No new browser test is requested. The unit test of the new store counter and the repeated-run and probe evidence are part of the plan and the success criteria, so they are tasks.

**Organization**: One user story (US1, P1). All paths are relative to the repository root; the Web UI is in `src/clients/web`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies); none is used here
- **[Story]**: US1 is the only user story

## Phase 1: Setup (Evidence First)

**Purpose**: Establish the cause of the flakiness with evidence before fixing it (FR-001, research R1, R3).

- [ ] T001 Confirm the tooling in `src/clients/web` (`npm ci`, Playwright 1.63.0 Chromium installed, Node.js 24) so the commands of `specs/20261023-031208-deterministic-upload-recovery-test/quickstart.md` run. A task must leave a change in the working tree, so record the confirmed versions and the outcome of the commands (a confirmation that changes no file is refused as "the agent made no changes") under a heading of its own in the `## Evidence` section of `specs/20261023-031208-deterministic-upload-recovery-test/research.md` (task ID, command, outcome)
- [ ] T002 Reproduce the flakiness in `src/clients/web/tests/e2e/simulated/recovery.spec.ts` with `npx playwright test --project simulated tests/e2e/simulated/recovery.spec.ts --workers=1 --repeat-each=30`, alone and under CPU load (one busy-loop process per core, stopped afterward), adding a temporary arrival timestamp `ts` to the recorded requests in `src/clients/web/tests/e2e/support/fakeObjectStorage.mjs` and logging the store's state at the helper's `reset`; record for a failing run whether a part 3 request arrives after the `reset`, and the result for each other candidate of research R3 (shared store state between serial tests, leftover journal or sessions, order of journal state, pending list and refusal message after the reload), also record for the isolation of serial tests (spec scenario 3) whether the previous test's closed page or worker can still send a request when the next `beforeEach` reset runs, and why the existing close order is sufficient or not, keeping the notes for T008. All temporary instrumentation and stress code (the logging in `recovery.spec.ts`, the lowered delay, the `ts` field) is removed in T003/T005. If the failure does not reproduce naturally in these runs, add a deterministic stress reproduction, test-side only and removed afterward (widen the race with the store's existing `delayMs` fault or a temporarily lowered retry delay), and show that the old helper then fails and the new helper passes; the failed natural reproduction and the stress reproduction are the evidence for SC-001. If the evidence names another cause, apply the fix of T005 to that cause instead and amend the plan's Design through the implementation

---

## Phase 2: Foundational (Observable Store Condition)

**Purpose**: The one additive store counter the quiesce step waits on; it blocks the helper change in US1.

**⚠️ CRITICAL**: US1 cannot start until this phase is complete.

- [ ] T003 Only if the T002 evidence confirms the plan's cause (or any cause whose fix needs the counter), add the live `inFlight` counter to the `GET /__control/state` response in `src/clients/web/tests/e2e/support/fakeObjectStorage.mjs` per `specs/20261023-031208-deterministic-upload-recovery-test/contracts/fake-store-control.md` (incremented at the start of the storage handler of every PUT request, which is the request that can be recorded, before the body is read; OPTIONS preflights and control requests not counted; decremented in a `finally` or on both response `finish` and connection `close`; not cleared by `reset`, existing fields unchanged), update the state shape and the header comment of the control endpoints in `src/clients/web/tests/e2e/support/fakeObjectStorage.mjs`, and in `fakeObjectStorage.d.mts` only if it types the state shape, and always remove the temporary `ts` of T002; otherwise T003 and T004 are replaced through the amendment that T002 already requires, and the contract note is dropped or amended in the same change
- [ ] T004 Cover the counter in `src/clients/web/tests/unit/support/fakeObjectStorage.test.ts` (0 when idle, greater than 0 while a delayed part request is being handled, back to 0 after the response, back to 0 after a request aborted by the client, unchanged by `reset`, not counting OPTIONS preflights, existing assertions untouched) and run `npx vitest run tests/unit/support/fakeObjectStorage.test.ts` in `src/clients/web`

**Checkpoint**: The store exposes `inFlight`; its unit test passes.

---

## Phase 3: User Story 1 - The different-file refusal test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: `refuses a different file and changes nothing (scenario 2)` and every test of `recovery.spec.ts` pass whenever the behavior is right, however loaded the runner is, and fail when it is broken.

**Independent Test**: 30 consecutive runs of `recovery.spec.ts` alone and 10 under CPU load all pass; both probes make the named test fail on its assertions and not hang.

- [ ] T005 [US1] In `src/clients/web/tests/e2e/simulated/recovery.spec.ts` replace the `page.waitForTimeout(500)` of `startInterrupted` with observable conditions, and put a code comment naming the evidenced cause (from T002) next to it: after the two stored parts, `expect.poll` until the store recorded a part 3 request, then `expect` on the first page's upload row (the role and text taken from its actual markup, determined during T002/T005) showing the retry text for the failing part 3, with a code comment stating why that state is only visible after the journal write of parts 1 and 2; if the markup has no such state, instead read the journal entry from the page's IndexedDB with `page.evaluate` before navigating to `about:blank` and expect parts 1 and 2 in it (no sleep), await the navigation of the page to `about:blank` (destroys the page and its dedicated upload worker), `expect.poll` until the old page reports no worker (`page.workers()` is empty), `expect.poll` until the state's `inFlight` is 0 (it covers only requests the server has seen, so the proof that page and worker are gone is what excludes requests still on their way), keeping the fault in place until then, and only then `control('reset')` and open the match page again with the helper's `page.reload()` replaced by `page.goto(`/matches/${match.id}`)` (the permitted equivalent of the reload, FR-004; the reloads inside individual tests stay unchanged); extend the `storageState` type with `inFlight: number`; keep `test.describe.configure({ mode: 'serial' })`, the `beforeEach`, and every assertion, expected value, and step of every test unchanged; add no `waitForTimeout`, retry, or longer timeout
- [ ] T006 [US1] Run `npx playwright test --project simulated tests/e2e/simulated/recovery.spec.ts --workers=1 --repeat-each=30` in `src/clients/web` and confirm all 30 runs of every test pass (SC-001, SC-002), and verify spec scenario 3 (no fault or activity of one serial test affects the next) from these runs, including the notes of T002 on the previous test's page and worker; fix the helper and rerun until they do, without retries. A task must leave a change in the working tree, so record the run counts and results under a heading of its own in the `## Evidence` section of `specs/20261023-031208-deterministic-upload-recovery-test/research.md` (task ID, command, outcome)
- [ ] T007 [US1] Run the same file under CPU load (one busy-loop process per core for the duration, `--repeat-each=10`, loops stopped afterward) in `src/clients/web` and confirm all runs pass (SC-001). A task must leave a change in the working tree, so record the run counts and results under a heading of its own in the `## Evidence` section of `specs/20261023-031208-deterministic-upload-recovery-test/research.md` (task ID, command, outcome)
- [ ] T008 [US1] Merge the pre-fix and run evidence that T001-T007 recorded in the `## Evidence` section of `specs/20261023-031208-deterministic-upload-recovery-test/research.md` into one coherent section, keeping their per-task headings (T009-T012 append their own headings afterwards), so it holds: the commands and run counts with and without load before the fix (T002), the failing run's request log with timestamps, the result for each candidate of R3, the evidenced cause (or the failed natural reproduction together with the stress reproduction of T002), why the existing close order isolates the serial tests (or the fix for it), and the repeated runs after the fix (T006, T007)

### Verification of Other Users and the Test's Strength

- [ ] T009 [US1] Run `npx playwright test --project simulated tests/e2e/simulated/uploads.spec.ts tests/e2e/simulated/upload-shell.spec.ts` and `npm test` in `src/clients/web` and confirm they pass unchanged (FR-006, SC-002). A task must leave a change in the working tree, so record the commands and results under a heading of its own in the `## Evidence` section of `specs/20261023-031208-deterministic-upload-recovery-test/research.md` (task ID, command, outcome)
- [ ] T010 [US1] Run the two probes of SC-003 temporarily against the Web UI upload code (a different-file refusal that sends a part, and a different file that is accepted and uploaded), confirm the named test fails on its assertions and does not hang for each, then remove both probes and confirm with `git status` that no production file under `src/clients/web/src/` remains changed (FR-005). A task must leave a change in the working tree, so record the probes and the observed failures of the named test (the probe code itself is removed) under a heading of its own in the `## Evidence` section of `specs/20261023-031208-deterministic-upload-recovery-test/research.md` (task ID, command, outcome)

**Checkpoint**: US1 is complete and independently verified.

---

## Phase 4: Polish & Cross-Cutting Concerns

- [ ] T011 Confirm with `git diff` that only `src/clients/web/tests/e2e/simulated/recovery.spec.ts`, `src/clients/web/tests/e2e/support/fakeObjectStorage.mjs`, `src/clients/web/tests/e2e/support/fakeObjectStorage.d.mts` (only if it types the state shape), `src/clients/web/tests/unit/support/fakeObjectStorage.test.ts`, and this spec folder changed, that no `waitForTimeout` remains in `recovery.spec.ts`, that no temporary logging, `console.log`, or stress code remains, and that no assertion line was removed (SC-004). A task must leave a change in the working tree, so record the `git diff` file list and the checks under a heading of its own in the `## Evidence` section of `specs/20261023-031208-deterministic-upload-recovery-test/research.md` (task ID, command, outcome)
- [ ] T012 Run `node .github/scripts/check-markdown.mjs --files specs/20261023-031208-deterministic-upload-recovery-test/research.md specs/20261023-031208-deterministic-upload-recovery-test/tasks.md` and fix findings. `docs/architecture/` needs no change (plan: Architecture Updates is `None`); confirm that no architecture statement is made false by the change. A task must leave a change in the working tree, so record the check outcome under a heading of its own in the `## Evidence` section of `specs/20261023-031208-deterministic-upload-recovery-test/research.md` (task ID, command, outcome)

---

## Dependencies & Execution Order

- Phase 1 (T001 → T002) first; its evidence decides the cause T005 fixes.
- Phase 2 (T003 → T004) after T002; blocks US1.
- US1: T005 → T006 → T007 → T008 → T009 → T010; T008 needs T006 and T007 results.
- Polish (T011, T012) after US1.
- Every task that only runs commands records its outcome in `research.md` § Evidence, because the implementation refuses a task that changes no file; T008 then merges the evidence of T001-T007 into a coherent section (keeping the per-task headings), and T009-T012 append their own headings afterwards.
- No task is marked `[P]`: tasks share the same files or depend on the previous task's evidence, and the Playwright runs share one fake object store and its fixed port, so they must not run in parallel.

## Implementation Strategy

- MVP is the whole feature (single story): evidence, counter, helper change, repeated runs.
- Stop and amend through the implementation if T002 names a cause other than the still-running upload retry racing the fixed wait and the reset.
- Production values (retry delays, limits) and the platform browser projects stay untouched.
