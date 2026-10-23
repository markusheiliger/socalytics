# Feature Specification: Repoint the archived workload retry link in the stall expiry spec

**Feature Branch**: `20261023-182820-repoint-archived-retry-link`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Repoint the archived workload retry link in the stall expiry spec. The Dependencies bullet on line 80 of `specs/20261023-150804-deterministic-stall-expiry-test/spec.md` links `../20261020-224223-deterministic-simulated-workload-retry/spec.md`, but that spec was archived to `specs/archive/20261020-224223-deterministic-simulated-workload-retry/`, so the repository Markdown check reports one dead link and fails the phase, finalize, and merge verification of every implementation."

## Problem

The spec [Deterministic Stall Expiry of the Simulated Agent Workload](../20261023-150804-deterministic-stall-expiry-test/spec.md) (merged by #263) was merged with a relative link in its Dependencies bullet (line 80) to the spec `20261020-224223-deterministic-simulated-workload-retry` at its pre-archive location. That spec has since been archived to [specs/archive/20261020-224223-deterministic-simulated-workload-retry](../archive/20261020-224223-deterministic-simulated-workload-retry/spec.md), so the link no longer resolves.

The repository Markdown validation (`node .github/scripts/check-markdown.mjs`) therefore reports exactly one dead link on the whole repository. Because phase, finalize, and merge verification of every implementation run that validation on the whole repository, every implementation currently fails verification for a reason unrelated to its own change. This blocks, among others, the implementation of the sibling spec `specs/20261023-031208-deterministic-upload-recovery-test` (pull request #248), whose Phase 4 verification only passes once the link is repointed.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Repository Markdown validation passes again (Priority: P1)

A maintainer or the implementation automation runs the repository Markdown validation on the whole repository and gets no dead link, so verification of any implementation no longer fails because of the stall expiry spec.

**Why this priority**: The dead link fails verification of every implementation; no implementation can finish until it is repaired.

**Independent Test**: Run `node .github/scripts/check-markdown.mjs` from the repository root and confirm it reports no dead links and exits successfully.

**Acceptance Scenarios**:

1. **Given** the stall expiry spec links its retry dependency at the pre-archive location, **When** the link target is repointed to the archive location, **Then** the Markdown validation resolves the link and reports no dead link on the whole repository.
2. **Given** the repointed link, **When** a reader follows it from the Dependencies bullet, **Then** it opens the archived spec `20261020-224223-deterministic-simulated-workload-retry`.
3. **Given** an implementation (such as pull request #248 of `specs/20261023-031208-deterministic-upload-recovery-test`) whose own change is correct, **When** its phase, finalize, or merge verification runs after this spec is merged, **Then** the Markdown validation no longer fails because of the stall expiry spec.

---

### Edge Cases

- The same spec is also mentioned as plain text on line 19 of the stall expiry spec and on line 102 of its `tasks.md`; these are not links and are not reported by the validation, but the archive rewrite also changes them to `specs/archive/20261020-224223-deterministic-simulated-workload-retry`. That is an accepted side effect (`referenceOnlySpecChanges`); the dead link on line 80 is the required fix.
- A different dead link may appear later; this spec repairs only the one link named here.
- The archived spec's content is not changed; only the location the link points at changes.
- The edit is a reference rewrite of an archived spec, made with the archive tooling (`node .github/scripts/speckit-archive.mjs 20261020-224223-deterministic-simulated-workload-retry`). It is the only kind of change outside the implementing spec's own folder that the implementation automation accepts, because `specs/` is otherwise a protected path.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The Dependencies bullet (line 80) of `specs/20261023-150804-deterministic-stall-expiry-test/spec.md` MUST link the workload retry spec at `../archive/20261020-224223-deterministic-simulated-workload-retry/spec.md` instead of `../20261020-224223-deterministic-simulated-workload-retry/spec.md`.
- **FR-002**: The change MUST be the output of the archive rewrite: the link target on line 80 (the required fix) and the plain-text mentions of `specs/20261020-224223-deterministic-simulated-workload-retry` on line 19 of the stall expiry `spec.md` and line 102 of its `tasks.md`, which point to `specs/archive/20261020-224223-deterministic-simulated-workload-retry` afterwards (accepted side effects). The link text, the rest of line 80, and every other line of the stall expiry spec folder MUST stay unchanged.
- **FR-003**: The change MUST touch only Markdown of the stall expiry spec folder (`spec.md` and `tasks.md`); no code, test, environment action, or `docs/architecture/` file may change.
- **FR-004**: After the change, the repository Markdown validation MUST report no dead link on the whole repository.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: `node .github/scripts/check-markdown.mjs` reports zero dead links and zero other findings on the whole repository.
- **SC-002**: Measured against the base branch and scoped to the stall expiry spec folder (`git diff -U0 <base>... -- specs/20261023-150804-deterministic-stall-expiry-test/`, excluding this spec's own folder), the difference is exactly three changed lines in two files: line 80 and line 19 of `spec.md` and line 102 of `tasks.md`, and in each only the `specs/<folder>` path of the retry spec differs (adding `archive/`).
- **SC-003**: Phase 4 verification of the implementation of `specs/20261023-031208-deterministic-upload-recovery-test` (pull request #248) no longer fails on the Markdown validation after this spec is merged.

## Assumptions

- The archived spec `specs/archive/20261020-224223-deterministic-simulated-workload-retry/spec.md` exists and is the intended target of the link.
- Line 80 is the only link in the stall expiry spec that points at a pre-archive location; the validation reports exactly one dead link.
- No architecture statement is affected, so the plan's Architecture Updates section is `None`.
- The sibling spec `specs/20261023-031208-deterministic-upload-recovery-test` depends on this spec, because its Phase 4 verification only passes once the link is repointed; this spec itself depends on no other spec.
