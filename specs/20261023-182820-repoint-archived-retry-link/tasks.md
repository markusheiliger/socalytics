---

description: "Task list for repointing the archived workload retry link in the stall expiry spec"
---

# Tasks: Repoint the archived workload retry link in the stall expiry spec

**Input**: Design documents from `/specs/20261023-182820-repoint-archived-retry-link/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md

**Tests**: No test tasks: the feature adds no behavior; the whole-repository Markdown validation is the evidence.

**Organization**: One user story (P1). The plan's Architecture Updates section is `None`, so no documentation task for `docs/architecture/` exists.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1)

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Confirm the starting state

- [ ] T001 Run `node .github/scripts/check-markdown.mjs` from the repository root and confirm its only finding is the dead link on line 80 of `specs/20261023-150804-deterministic-stall-expiry-test/spec.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

None: no shared infrastructure, dependency, or environment change is needed.

---

## Phase 3: User Story 1 - Repository Markdown validation passes again (Priority: P1) 🎯 MVP

**Goal**: The whole-repository Markdown validation reports no dead link, so verification of every implementation no longer fails because of the stall expiry spec.

**Independent Test**: Run `node .github/scripts/check-markdown.mjs` from the repository root; it reports zero findings and exits successfully.

- [ ] T002 [US1] Run `node .github/scripts/speckit-archive.mjs 20261020-224223-deterministic-simulated-workload-retry` so its reference rewrite of the already archived spec lands as a reference-only change (the only kind of change outside this spec's folder that the automation accepts, since `specs/` is protected); in `specs/20261023-150804-deterministic-stall-expiry-test/` it must change the link target on line 80 of `spec.md` (the Dependencies bullet) from `../20261020-224223-deterministic-simulated-workload-retry/spec.md` to `../archive/20261020-224223-deterministic-simulated-workload-retry/spec.md` (the required fix), plus, as accepted side effects, the plain-text mentions of `specs/archive/20261020-224223-deterministic-simulated-workload-retry` on line 19 of `spec.md` and line 102 of `tasks.md`, which point to `specs/archive/...` afterwards; keep the link text, the rest of those lines, and every other line unchanged
- [ ] T003 [US1] Run `node .github/scripts/check-markdown.mjs` from the repository root and confirm zero findings and exit code 0 (FR-004, SC-001)
- [ ] T004 [US1] Run `git diff -U0 <base>... -- specs/20261023-150804-deterministic-stall-expiry-test/` (against the base branch, excluding this spec's own folder) and confirm exactly two files and three lines changed (line 80 and line 19 of `spec.md`, line 102 of `tasks.md`), with only `archive/` added to the retry spec path in each (FR-001, FR-002, FR-003, SC-002)

---

## Phase 4: Polish & Cross-Cutting Concerns

None: no documentation, code, test, environment action, or `docs/architecture/` file changes.

---

## Dependencies & Execution Order

- T001 → T002 → T003 → T004, all sequential: every task depends on the previous one, and T002 is the only file change.
- The edit is the archive rewrite's output (see T002), not a hand edit.
- The sibling spec `specs/20261023-031208-deterministic-upload-recovery-test` depends on this spec; this spec depends on no other spec.

## Parallel Example

No parallel tasks: the feature is three lines in two files, produced by one command.

## Implementation Strategy

MVP is User Story 1, which is the whole feature: run the archive rewrite (T002), then validate with the Markdown check and the diff check (T003, T004).
