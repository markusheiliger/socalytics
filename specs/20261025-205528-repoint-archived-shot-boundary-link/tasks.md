# Tasks: Repoint the archived Shot Boundary Analyst link in the deterministic agent workload test spec

**Input**: Design documents from `/specs/20261025-205528-repoint-archived-shot-boundary-link/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, quickstart.md (no `contracts/`: the change exposes no interface)

**Tests**: No test tasks: the spec requests none. The evidence is the repository Markdown validation (SC-001) and the scoped one-line diff (SC-002).

**Organization**: One user story (User Story 1, P1) covers the whole feature. The plan's Architecture Updates section is `None`, so no `docs/architecture/` task exists.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1)
- Include exact file paths in descriptions

## Phase 1: User Story 1 - Repository Markdown validation passes again (Priority: P1)

**Goal**: The whole-repository Markdown validation reports no dead link, so verification of every implementation no longer fails because of the deterministic agent workload test spec.

**Independent Test**: `node .github/scripts/check-markdown.mjs` from the repository root exits successfully with no finding (SC-001).

- [ ] T001 [US1] In `specs/20261025-003459-deterministic-agent-workload-test/spec.md` line 83 (the Dependencies entry), change only the Shot Boundary Analyst link target from `../20261023-022148-shot-boundary-analyst/spec.md` to `../archive/20261023-022148-shot-boundary-analyst/spec.md` by a direct edit; leave the link text, the rest of line 83, and every other line unchanged, including the plain-text mentions on lines 9 and 11 (the archive tooling's reference rewrite at the merge MAY rewrite those to `specs/archive/20261023-022148-shot-boundary-analyst`). Then run `node .github/scripts/check-markdown.mjs` from the repository root: it MUST exit with code 0 with zero dead links and zero other findings (SC-001, FR-004), and the diff of the task MUST contain only that one-line change in that one file (SC-002, FR-003) (FR-001, FR-002)

**Checkpoint**: The link opens `specs/archive/20261023-022148-shot-boundary-analyst/spec.md` and the validation passes.

---

## Dependencies & Execution Order

- T001 is the only task: it makes the edit and verifies it, so no `[P]` group exists. The earlier verify-only tasks (baseline, final validation, scope check) were folded into it, because a task that changes no file cannot succeed in the implementation workflow.

## Implementation Strategy

- MVP is the whole feature: T001 is one increment. After merge, the stopped implementation pull request #318 of `specs/20261023-085421-cv-inference-environment` can pass its phase and finalize verification (SC-003).
