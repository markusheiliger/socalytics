# Tasks: Fix the Case-Sensitivity Probe of the Sample Tooling Test

**Input**: Design documents from `/specs/archive/20261030-122115-fix-samples-case-probe/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md

**Tests**: No new test tasks. The change is itself a fix of the existing test `tools/samples.test.mjs`; the verification is manual evidence (see quickstart.md), not a task. The only changed file outside this spec folder is `tools/samples.test.mjs`.

**Organization**: Tasks are grouped by user story. All three stories are served by one edit of one probe expression, so the only task lives in User Story 1; the other stories are verified manually.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - The sample tooling test passes on Linux (Priority: P1)

**Goal**: `node --test tools/samples.test.mjs` passes on a case-sensitive file system with the case-clash assertion executed.

**Independent Test**: Run `node --test tools/samples.test.mjs` on Linux; 13 of 13 tests pass and the case-clash assertion runs.

- [x] T002 [US1] In `tools/samples.test.mjs`, in the test "refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links", replace the probe condition `discoverVideos(folder({ 'probe/a.mp4': '1', 'probe/A.mp4': '2' })).videos.length === 2` with a direct file-system check without any call to `discoverVideos` and without `try`/`catch`: create the single file with `const probe = folder({ 'probe/a.mp4': '1' });` (same temporary location as the `clash` folder) and run the assertion `assert.throws(() => discoverVideos(clash), /differ only in case/)` only `if (!existsSync(path.join(probe, 'probe', 'A.mp4')))`; keep that assertion, the `clash` folder creation, and the test title textually unchanged, and reuse the already imported `existsSync`

---

## Phase 2: User Story 2 - The test stays correct on case-insensitive file systems (Priority: P2)

**Goal**: On a file system on which `probe/A.mp4` is visible after `probe/a.mp4` was created, the case-clash assertion is not run and the test passes.

**Independent Test**: Review the branch of the probe; on macOS or Windows (manual evidence) the command passes.

No task: verified manually (see quickstart.md step 3).

---

## Phase 3: User Story 3 - The refusal of case-only clashes stays enforced (Priority: P2)

**Goal**: The fix weakens neither the test nor the protection against case-only path clashes.

**Independent Test**: Only `tools/samples.test.mjs` differs outside this spec folder, no assertion is removed or loosened, and disabling the refusal in a scratch copy makes exactly the case-clash test fail.

No task: verified manually (see quickstart.md steps 2 and 4).

---

## Dependencies & Execution Order

- T002 is the only task and has no dependencies.
- No task is marked `[P]`.
- The Linux run of `node --test tools/samples.test.mjs`, the scratch disable-the-refusal check, the scope check, and the macOS or Windows branch are manual evidence in quickstart.md and research.md; no task claims them as verified.

## Implementation Strategy

MVP is User Story 1: T002 delivers the whole fix. User Stories 2 and 3 are checked manually through quickstart.md that the fix stays correct on case-insensitive file systems and does not weaken the test.
