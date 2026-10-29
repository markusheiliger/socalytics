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

No setup tasks: the baseline failure of the case-clash test is not a task, because a run that is expected to fail cannot be verified or ticked by the implementation automation. SC-001 and SC-003 are evidenced by the manual checks in `quickstart.md`.

## Phase 2: Foundational

No foundational tasks: there is no shared infrastructure to prepare.

## Phase 3: User Story 1 and User Story 2 - The test passes on case-sensitive and case-insensitive file systems (Priority: P1) 🎯 MVP

**Goal**: The probe decides whether `a.mp4` and `A.mp4` coexist without calling the discovery, so the test passes on Linux (assertion runs) and on macOS and Windows (assertion not demanded).

**Independent Test**: `node --test tools/samples.test.mjs` passes on Linux with the case-clash assertion executed; on a file system where the probe finds one entry the assertion is not demanded and the test still passes.

- [x] T002 [US1] [US2] In `tools/samples.test.mjs`, add `readdirSync` to the existing `node:fs` import (keep the import list alphabetical: `existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync`)
- [x] T003 [US1] [US2] In `tools/samples.test.mjs`, replace the probe condition `discoverVideos(folder({ 'probe/a.mp4': '1', 'probe/A.mp4': '2' })).videos.length === 2` in the test 'refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links' with a direct check that does not call `discoverVideos`: create the probe with `folder({ 'probe/a.mp4': '1', 'probe/A.mp4': '2' })` and assign the result to a new const (for example `probeRoot`) and compare `readdirSync(path.join(probeRoot, 'probe')).length === 2` (two entries: names coexist; one entry: they cannot coexist); do not catch errors from the probe (FR-006); keep `assert.throws(() => discoverVideos(clash), /differ only in case/)` textually unchanged inside the conditional, keep the `clash` folder creation, and leave the missing-folder, no-videos, empty-video, and symbolic-link checks unchanged

**Checkpoint**: The tooling test passes on Linux; the logic for case-insensitive systems is verified by reasoning (the second write replaces the first, one entry remains).

## Phase 4: User Story 3 - The refusal of case-only clashes stays enforced (Priority: P2)

No tasks: running `node --test` commands (the wider run, the scratch copy without the refusal, the change-scope check) fits no requirement runner of the implementation automation (`tools/` has no `package.json` and is no `environment-verify` component), so such tasks are rejected as unverifiable and can never be ticked. They are evidenced by the convergence step and the phase and merge verification (which check the whole repository, including Markdown, and the changed files), and by the manual checks in `quickstart.md` (SC-001, SC-003, SC-005).

## Phase 5: Polish & Cross-Cutting Concerns

No tasks: the Markdown check of the spec folder runs in the phase and merge verification. No documentation task for `docs/architecture/` is needed: the plan's `## Architecture Updates` section is `None` with a reason.

## Dependencies & Execution Order

- Phase 1 has no tasks (T001 was removed: its expected-failure run cannot be verified automatically).
- T002 → T003 (same file, sequential).
- T004 to T007 were removed: each only runs `node --test` or a repository check that no requirement runner supports.
- Parallel opportunities: none.

## Implementation Strategy

- MVP: Phase 3 (T002–T003) completes the fix; the guard (SC-003) and scope (SC-005) evidence are manual checks from `quickstart.md`.
- Real case-insensitive file systems (macOS, Windows) are verified by running the test there; CI exercises only the case-sensitive branch (SC-002).
- Passing tests are development evidence only and do not establish deployment support or production readiness.
