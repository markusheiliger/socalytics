# Tasks: Environment Exclusivity Guard Ignores Archive Reference Rewrites

**Input**: Design documents from `/specs/20261026-100223-environment-guard-archive-rewrites/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/guard-inputs.md](contracts/guard-inputs.md), [quickstart.md](quickstart.md)

**Tests**: Requested by FR-007 of the spec; test tasks are included and come with the behavior they cover.

**Organization**: Tasks are grouped by user story. This is repository tooling only (`.github/scripts/` and documentation); no `src/`, no environment action, and no `docs/architecture/` file changes (Architecture Updates is `None`).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies on each other)
- **[Story]**: Which user story this task belongs to (US1, US2, US3)

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The shared criterion that both the merge-time check and the guard use (FR-002). It blocks every user story.

- [ ] T002 Extract and export `referenceRewriteCheck({ git, baseSha, folder })` in `.github/scripts/speckit-archive.mjs`: list the staged changes under `specs/` against `baseSha` exactly as `referenceOnlySpecChanges` does, skip the spec's own folder (active and archived), and return `{ rewritten, violations, error }` where a file is `rewritten` only when its status is `M`, both blobs are readable, and the base blob with every archive of the staged tree applied equals the staged blob (everything else, including unreadable blobs, is a violation; `error` keeps the existing "could not be listed" text); make `referenceOnlySpecChanges` a thin wrapper that renders byte-identical reasons from `violations` and `error`
- [ ] T003 Add tests of `referenceRewriteCheck` in `.github/scripts/speckit-archive.test.mjs` (modified rewrite is `rewritten`; non-reference edit, added, deleted, and unreadable-blob files are violations; no archive in the staged tree means any modified file is a violation; listing failure sets `error`) and keep every existing `referenceOnlySpecChanges` test unchanged

**Checkpoint**: The criterion is shared and tested; `referenceOnlySpecChanges` results are unchanged.

---

## Phase 3: User Story 1 - An environment spec lands after syncing the base branch (Priority: P1) 🎯 MVP

**Goal**: The guard accepts archive reference rewrites outside the spec's folder next to an environment action change.

**Independent Test**: A branch change set with an environment action file, the spec's own `tasks.md`, and a rewritten spec file outside the spec's folder gives no guard reason, in the pure unit test and in `checkStagedTask` against a temporary repository.

### Guard and caller

- [ ] T004 [P] [US1] Change `environmentExclusivityReasons({ folder, branchPaths, referenceRewrites = [] })` in `.github/scripts/speckit-implement-core.mjs` to exclude the paths in `referenceRewrites` from the foreign list (still pure, wording of the reason unchanged, names exactly the foreign paths in `branchPaths` order) and update its code comment to state that archive reference rewrites outside the spec's folder do not count as other changes (FR-008)
- [ ] T005 [P] [US1] Change `checkStagedTask` in `.github/scripts/speckit-implement-work.mjs` to call `referenceRewriteCheck` against the base branch commit `refs/remotes/origin/<baseBranch>` only when the branch changes an environment action and has candidate paths under `specs/`, and pass its `rewritten` list as `referenceRewrites`, together with every `branchPaths` entry under `specs/` outside the spec's folder that has no diff against the base branch tip (it is not a change against the base branch and must not count as foreign), derived as the spec files outside the spec's folder in `branchPaths` that are absent from both `rewritten` and `violations` of the same `referenceRewriteCheck` result and empty whenever its `error` is non-null, so a listing failure leaves every candidate foreign; a listing error or an unreadable blob leaves the file out of `rewritten` so it counts as foreign (FR-005); keep the "cannot be compared with its base branch" reason and all other reasons unchanged

### Acceptance tests for the guard and caller

- [ ] T006 [P] [US1] Add pure guard cases in `.github/scripts/speckit-implement-core.test.mjs`: a reference rewrite next to an environment action change and the spec's own `tasks.md` gives no reason, also with several rewritten files (a second sync)
- [ ] T007 [P] [US1] Add a `checkStagedTask` case in `.github/scripts/speckit-implement.test.mjs` against a temporary Git repository with an archived spec: the rewritten spec file outside the spec's folder is accepted next to an environment action change

**Checkpoint**: User Story 1 works and is independently testable.

---

## Phase 4: User Story 2 - Everything else still fails the guard (Priority: P1)

**Goal**: Only pure reference rewrites are exempt; every other change class still fails with the existing reason naming exactly the foreign paths.

**Independent Test**: One unit case per foreign class, each naming exactly the offending paths.

### Tests for User Story 2

- [ ] T008 [P] [US2] Add pure guard cases in `.github/scripts/speckit-implement-core.test.mjs` for a non-reference edit under `specs/`, an added spec file, a deleted spec file, a `.github/` file outside the environment actions, a `.specify/` file, and a source file (each rejected naming only that path), a mixed branch with rewrites plus a foreign change naming only the foreign paths, a modified own-folder file other than `tasks.md` (for example `spec.md`, rejected naming only that path, even when it equals its base version with every archive applied), and a branch without an environment action change giving no reason
- [ ] T009 [P] [US2] Add `checkStagedTask` cases in `.github/scripts/speckit-implement.test.mjs` against a temporary repository: a non-reference edit of a spec file outside the spec's folder is refused and named; an unreadable base blob counts as foreign, using an injected git runner wrapping the real repository that fails the `git show <baseSha>:<file>` call; a spec file in the merge-base diff that the advanced base branch tip already contains identically is not foreign; and an injected git runner that fails the `git diff --cached --name-status` listing against the base tip still names the spec file outside the spec's folder as foreign

**Checkpoint**: Zero false acceptances in the tests (SC-002).

---

## Phase 5: User Story 3 - Other checks keep their behavior (Priority: P2)

**Goal**: Staging validation, the protected-path rule, `referenceOnlySpecChanges`, and the archive rewrite behave exactly as before.

**Independent Test**: The existing Spec Kit tooling tests pass without changed expectations.

### Verification for User Story 3

- [ ] T010 [US3] Run `node --test .github/scripts/speckit-*.test.mjs` from the repository root and confirm all tests pass, with no edited expectation in any pre-existing case of `.github/scripts/speckit-archive.test.mjs`, `.github/scripts/speckit-implement-core.test.mjs`, or `.github/scripts/speckit-implement.test.mjs` (including the protected-path rejection of a spec file outside the task's folder)

**Checkpoint**: All user stories pass.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Documentation of the rule (FR-008) and final validation.

- [ ] T011 [P] Update the environment exclusivity rule in `AGENTS.md` (Spec Kit section, the "New frameworks, SDKs, or tools reach these actions only through a standalone environment spec" item) to state that archive reference rewrites outside the spec's folder do not count as other changes
- [ ] T012 [P] Update item 1 of "Specs extend these actions" in `README.md` to state that archive reference rewrites outside the spec's folder do not count as other changes
- [ ] T013 [P] Update the description of the environment actions rule in `.github/workflows/README.md` to add one sentence in its environment actions section stating the environment exclusivity rule with the exception that archive reference rewrites outside the spec's folder do not count as other changes
- [ ] T014 [P] Update the environment-spec/exclusivity description in `.specify/extension-src/gha/README.md` (the extension source; do not hand-edit the generated `.specify/extensions/` copies) to add one sentence next to the `repair` workflow's `environment=true` mention stating the environment exclusivity rule with the exception that archive reference rewrites outside the spec's folder do not count as other changes
- [ ] T015 Run `node .github/scripts/check-markdown.mjs` and `node --test .github/scripts/speckit-*.test.mjs` from the repository root and fix any finding in the files this feature changed (SC-003); then follow [quickstart.md](quickstart.md) sections 1 to 3

---

## Dependencies & Execution Order

- Phase 2 first (there is no Phase 1): T002 before T003.
- Phase 2 blocks all stories. In Phase 3, T004 and T005 touch different files and run together (T005 consumes T002 and passes the `referenceRewrites` input of T004 by contract); T006 and T007 test their combined behavior and run together afterwards.
- Phase 4 extends the test files of Phase 3, so it follows Phase 3; T008 and T009 touch different files.
- Phase 5 (T010) runs after Phases 3 and 4. Phase 6 documentation tasks (T011 to T014) are independent of each other; T015 runs last.
- After merge (SC-004, not a task of this list): start the stopped implementation of `specs/20261025-194925-dotnet-setup-resilience` again with `/speckit retry` or `/speckit sync`.

## Parallel Example: User Story 1

```text
Guard and caller:  T004 speckit-implement-core.mjs, T005 speckit-implement-work.mjs
Acceptance tests:  T006 speckit-implement-core.test.mjs, T007 speckit-implement.test.mjs
```

## Implementation Strategy

- **MVP**: Phases 1 to 3 (shared criterion, guard, caller, acceptance tests) unblock environment specs; Phase 4 locks the non-exemption classes; Phase 5 and 6 prove unchanged behavior and document the rule.
- Deliver incrementally: each checkpoint leaves the tooling tests green.
