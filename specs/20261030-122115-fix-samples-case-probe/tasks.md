# Tasks: Fix the Case-Sensitivity Probe of the Sample Tooling Test

**Input**: Design documents from `/specs/20261030-122115-fix-samples-case-probe/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md

**Tests**: No new test tasks. The change is itself a fix of the existing test `tools/samples.test.mjs`; the verification tasks run it. The only changed file outside this spec folder is `tools/samples.test.mjs`.

**Organization**: Tasks are grouped by user story. All three stories are served by one edit of one probe expression, so the edit lives in User Story 1 and the other stories verify it.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: Setup

- [ ] T001 Run `node --test tools/samples.test.mjs` from the repository root and confirm the baseline on Linux: 13 tests, 12 pass, and the test "refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links" fails with "Paths differ only in case" thrown from the probe at the `discoverVideos(folder({ 'probe/a.mp4': '1', 'probe/A.mp4': '2' }))` line of `tools/samples.test.mjs`

---

## Phase 2: User Story 1 - The sample tooling test passes on Linux (Priority: P1)

**Goal**: `node --test tools/samples.test.mjs` passes on a case-sensitive file system with the case-clash assertion executed.

**Independent Test**: Run `node --test tools/samples.test.mjs` on Linux; 13 of 13 tests pass and the case-clash assertion runs.

- [ ] T002 [US1] In `tools/samples.test.mjs`, in the test "refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links", replace the probe condition `discoverVideos(folder({ 'probe/a.mp4': '1', 'probe/A.mp4': '2' })).videos.length === 2` with a direct file-system check without any call to `discoverVideos` and without `try`/`catch`: create the single file with `const probe = folder({ 'probe/a.mp4': '1' });` (same temporary location as the `clash` folder) and run the assertion `assert.throws(() => discoverVideos(clash), /differ only in case/)` only `if (!existsSync(path.join(probe, 'probe', 'A.mp4')))`; keep that assertion, the `clash` folder creation, and the test title textually unchanged, and reuse the already imported `existsSync`
- [ ] T003 [US1] Run `node --test tools/samples.test.mjs` from the repository root and confirm 13 of 13 tests pass with 0 failures; execution of the case-clash assertion is proven by T005

---

## Phase 3: User Story 2 - The test stays correct on case-insensitive file systems (Priority: P2)

**Goal**: On a file system on which `probe/A.mp4` is visible after `probe/a.mp4` was created, the case-clash assertion is not run and the test passes.

**Independent Test**: Review the branch of the probe; on macOS or Windows (manual evidence) the command passes.

- [ ] T004 [US2] Review the probe in `tools/samples.test.mjs` against FR-003 and FR-006: when `existsSync(path.join(probe, 'probe', 'A.mp4'))` is true the assertion is skipped and nothing else in the test depends on it, and a failure to create `probe/a.mp4` surfaces as a test error with its real cause (no `try`/`catch` around the probe); record the case-insensitive branch as manual evidence only and fix any deviation in `tools/samples.test.mjs`

---

## Phase 4: User Story 3 - The refusal of case-only clashes stays enforced (Priority: P2)

**Goal**: The fix weakens neither the test nor the protection against case-only path clashes.

**Independent Test**: Only `tools/samples.test.mjs` differs outside this spec folder, no assertion is removed or loosened, and disabling the refusal in a scratch copy makes exactly the case-clash test fail.

- [ ] T005 [US3] Confirm that disabling the discovery's case-only refusal makes exactly the case-clash test fail on Linux: copy the whole `tools/` folder into a temporary folder outside the repository, then change `discoverVideos` only in the copy so that it stops refusing case-only clashes, run the copied test, and verify the only failure is the case-clash test; leave `tools/samples-lib.mjs` in the repository unchanged and delete the temporary folder afterwards
- [ ] T006 [US3] Confirm the scope: `git diff --name-only` against the base branch, excluding `specs/`, lists only `tools/samples.test.mjs`, and `git diff tools/samples.test.mjs` removes or loosens no assertion and does not touch `tools/samples-lib.mjs`, other tests, `.github/`, `src/`, or `docs/architecture/`

---

## Phase 5: Polish & Cross-Cutting Concerns

- [ ] T007 Run the steps of `specs/20261030-122115-fix-samples-case-probe/quickstart.md` that apply on Linux (steps 1, 2, and 4) and `node --test tools/samples.test.mjs` a final time; `docs/architecture/` needs no change (plan.md "Architecture Updates": None)

---

## Dependencies & Execution Order

- T001 first (baseline); T002 after T001; T003 after T002.
- T004 after T002; T005 and T006 after T002 and T003.
- T007 last.
- No task is marked `[P]`: every code task changes or reads the same file `tools/samples.test.mjs`, and the verification tasks share the working tree.

## Implementation Strategy

MVP is User Story 1: T001 to T003 deliver the whole fix. User Stories 2 and 3 only verify that the fix stays correct on case-insensitive file systems and does not weaken the test, and Phase 5 repeats the quickstart.
