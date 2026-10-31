# Feature Specification: Repoint the archived Shot Boundary Analyst link in the deterministic agent workload test spec

**Feature Branch**: `20261025-205528-repoint-archived-shot-boundary-link`

**Created**: 2026-10-25

**Status**: Draft

**Input**: User description: "Repoint the archived Shot Boundary Analyst link in the deterministic agent workload test spec. The Dependencies entry on line 83 of `specs/archive/20261025-003459-deterministic-agent-workload-test/spec.md` links `../20261023-022148-shot-boundary-analyst/spec.md`, but that spec was archived to `specs/archive/20261023-022148-shot-boundary-analyst/`, so the repository Markdown check reports one dead link and fails the phase and finalize verification of every implementation."

## Problem

The spec [Deterministic Agent Workload Test](../20261025-003459-deterministic-agent-workload-test/spec.md) is on the default branch with a relative link in its Dependencies entry (line 83) to the spec `20261023-022148-shot-boundary-analyst` at its pre-archive location. That spec has since been archived to [specs/archive/20261023-022148-shot-boundary-analyst](../20261023-022148-shot-boundary-analyst/spec.md), so the link no longer resolves.

The repository Markdown validation (`node .github/scripts/check-markdown.mjs`) therefore reports one dead link on the whole repository. Because phase and finalize verification of every implementation run that validation on the whole repository, every implementation currently fails verification for a reason unrelated to its own change. This blocks the stopped implementation pull request #318 of `specs/archive/20261023-085421-cv-inference-environment` (twin #256), whose own task T001 passed.

The merged spec `20261023-182820-repoint-archived-retry-link` fixed the same kind of defect for a different link and does not cover this one.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Repository Markdown validation passes again (Priority: P1)

A maintainer or the implementation automation runs the repository Markdown validation on the whole repository and gets no dead link, so verification of an implementation no longer fails because of the deterministic agent workload test spec.

**Why this priority**: The dead link fails full verification of every implementation; none can finish until it is repaired.

**Independent Test**: Run `node .github/scripts/check-markdown.mjs` from the repository root and confirm it reports no dead links and exits successfully.

**Acceptance Scenarios**:

1. **Given** the deterministic agent workload test spec links the Shot Boundary Analyst at the pre-archive location, **When** the link target is repointed to the archive location, **Then** the Markdown validation resolves the link and reports no dead link on the whole repository.
2. **Given** the repointed link, **When** a reader follows it from the Dependencies entry, **Then** it opens the archived spec `20261023-022148-shot-boundary-analyst`.
3. **Given** the implementation pull request #318 of `specs/archive/20261023-085421-cv-inference-environment`, whose own change is correct, **When** its phase or finalize verification runs after this spec is merged, **Then** the Markdown validation no longer fails because of the deterministic agent workload test spec.

---

### Edge Cases

- The plain-text mentions of the unarchived folder on lines 9 and 11 of the same spec are not links and are not reported by the validation; the archive tooling's reference rewrite MAY also rewrite them to `specs/archive/20261023-022148-shot-boundary-analyst` during the merge, and either state is accepted.
- A different dead link may appear later; this spec repairs only the one link named here.
- The archived Shot Boundary Analyst spec's content is not changed; only the location the link points at changes.
- `specs/` is otherwise a protected path for implementations; the one-line edit is the only change outside this spec's own folder, so the implementation applies it directly, and the archive tooling's reference rewrite at the merge MAY additionally rewrite the plain-text mentions.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The Dependencies entry (line 83) of `specs/archive/20261025-003459-deterministic-agent-workload-test/spec.md` MUST link the Shot Boundary Analyst at `../archive/20261023-022148-shot-boundary-analyst/spec.md` instead of `../20261023-022148-shot-boundary-analyst/spec.md`.
- **FR-002**: The link target on line 83 MUST change. The link text and the rest of line 83 MUST stay unchanged. The plain-text mentions on lines 9 and 11 MAY also be rewritten to `specs/archive/20261023-022148-shot-boundary-analyst` by the archive tooling's reference rewrite; every other line of the file MUST stay unchanged.
- **FR-003**: No other file outside this spec's own folder MAY change; no code, test, environment action, or `docs/architecture/` file changes.
- **FR-004**: After the change, the repository Markdown validation MUST report no dead link on the whole repository.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: `node .github/scripts/check-markdown.mjs` reports zero dead links and zero other findings on the whole repository.
- **SC-002**: Measured against the base branch and scoped to `specs/archive/20261025-003459-deterministic-agent-workload-test/`, every changed line is a reference rewrite of the archived folder `20261023-022148-shot-boundary-analyst`: the link target on line 83 (where only `archive/` is added) and, optionally, the plain-text mentions on lines 9 and 11; no other line changes.
- **SC-003**: Phase and finalize verification of the implementation of `specs/archive/20261023-085421-cv-inference-environment` (pull request #318) no longer fails on the Markdown validation after this spec is merged.

## Assumptions

- The archived spec `specs/archive/20261023-022148-shot-boundary-analyst/spec.md` exists and is the intended target of the link.
- Line 83 is the only link that points at a pre-archive location; the validation reports exactly one dead link.
- No architecture statement is affected, so the plan's Architecture Updates section is `None`.
- The spec `specs/archive/20261023-085421-cv-inference-environment` (implementation pull request #318) depends on this spec; this spec itself depends on no other spec.
