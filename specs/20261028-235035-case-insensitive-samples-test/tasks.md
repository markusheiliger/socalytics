# Tasks: Case-Sensitivity-Independent Sample Tooling Test

**Input**: Design documents from `/specs/20261028-235035-case-insensitive-samples-test/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md

**Tests**: The feature is itself a test change; no additional test tasks are generated. Verification runs the changed test.

**Organization**: Tasks are grouped by user story. The whole code change is one edit of `tools/samples.test.mjs`, so it belongs to User Story 1 and User Story 2 together; User Story 3 is verified, not implemented.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies); this spec has none
- **[Story]**: Which user story this task belongs to
- Only `tools/samples.test.mjs` may change; `tools/samples-lib.mjs` and every other test stay untouched (FR-005)

## Phase 1: Setup

No setup tasks: the baseline failure of the case-clash test is not a task, because a run that is expected to fail cannot be verified or ticked by the implementation automation. SC-001 and SC-003 are evidenced by T005, whose scratch copy without the refusal fails exactly the case-clash test.

## Phase 2: Foundational

No foundational tasks: there is no shared infrastructure to prepare.

## Phase 3: User Story 1 and User Story 2 - The test passes on case-sensitive and case-insensitive file systems (Priority: P1) 🎯 MVP

**Goal**: The probe decides whether `a.mp4` and `A.mp4` coexist without calling the discovery, so the test passes on Linux (assertion runs) and on macOS and Windows (assertion not demanded).

**Independent Test**: `node --test tools/samples.test.mjs` passes on Linux with the case-clash assertion executed; on a file system where the probe finds one entry the assertion is not demanded and the test still passes.

- [x] T002 [US1] [US2] In `tools/samples.test.mjs`, add `readdirSync` to the existing `node:fs` import (keep the import list alphabetical: `existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync`)
- [x] T003 [US1] [US2] In `tools/samples.test.mjs`, replace the probe condition `discoverVideos(folder({ 'probe/a.mp4': '1', 'probe/A.mp4': '2' })).videos.length === 2` in the test 'refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links' with a direct check that does not call `discoverVideos`: create the probe with `folder({ 'probe/a.mp4': '1', 'probe/A.mp4': '2' })` and assign the result to a new const (for example `probeRoot`) and compare `readdirSync(path.join(probeRoot, 'probe')).length === 2` (two entries: names coexist; one entry: they cannot coexist); do not catch errors from the probe (FR-006); keep `assert.throws(() => discoverVideos(clash), /differ only in case/)` textually unchanged inside the conditional, keep the `clash` folder creation, and leave the missing-folder, no-videos, empty-video, and symbolic-link checks unchanged
- [ ] T004 [US1] Run `node --test tools/samples.test.mjs` from the repository root and confirm zero failures on Linux (SC-001 is evidenced by T005: the scratch copy without the refusal must fail exactly the case-clash test, which proves the assertion ran in the unmodified Linux run); then run `node --test tools/samples.test.mjs .github/scripts/speckit-*.test.mjs` to confirm the wider tooling tests are unaffected

**Checkpoint**: The tooling test passes on Linux; the logic for case-insensitive systems is verified by reasoning (the second write replaces the first, one entry remains).

## Phase 4: User Story 3 - The refusal of case-only clashes stays enforced (Priority: P2)

**Goal**: Show that the test still guards the refusal and that the change touches nothing else.

**Independent Test**: A scratch copy of the discovery without the refusal makes exactly the case-clash test fail; `git diff --name-only` lists only `tools/samples.test.mjs` and this spec folder.

- [ ] T005 [US3] Verify the guard (SC-003) without modifying the repository: copy `tools/` to a temporary folder outside the repository, remove the case-only refusal from the copy of `samples-lib.mjs`, run `node --test samples.test.mjs` there, confirm that exactly the case-clash test fails, and delete the copy; `tools/samples-lib.mjs` in the repository stays unchanged
- [ ] T006 [US3] Verify the change scope (SC-005): run `git diff --name-only <base-branch>...HEAD` (committed changes) and `git status --short` (uncommitted and untracked files) from the repository root and confirm that the only changed file outside `specs/20261028-235035-case-insensitive-samples-test/` is `tools/samples.test.mjs`, with no edit to `tools/samples-lib.mjs`, `docs/architecture/`, or another test

## Phase 5: Polish & Cross-Cutting Concerns

- [ ] T007 Run `node .github/scripts/check-markdown.mjs` from the repository root and fix any finding in any of the spec folder's Markdown files

No documentation task for `docs/architecture/` is needed: the plan's `## Architecture Updates` section is `None` with a reason.

## Dependencies & Execution Order

- Phase 1 has no tasks (T001 was removed: its expected-failure run cannot be verified automatically).
- T002 → T003 → T004 (same file, sequential).
- Phase 4 (T005, T006) depends on T004; T006 also needs T003 to be complete.
- T007 last.
- Parallel opportunities: none; every code task edits the same file and the verification tasks are short and sequential.

## Implementation Strategy

- MVP: Phase 3 (T002–T004) completes the fix; Phase 4 adds the evidence that the refusal stays enforced and the scope is one file.
- Real case-insensitive file systems (macOS, Windows) are verified by running the test there; CI exercises only the case-sensitive branch (SC-002).
- Passing tests are development evidence only and do not establish deployment support or production readiness.
