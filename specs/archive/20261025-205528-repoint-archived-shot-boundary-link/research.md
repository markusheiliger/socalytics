# Research: Repoint the archived Shot Boundary Analyst link

No `NEEDS CLARIFICATION` remains in the plan's Technical Context. The decisions below record the choices the plan relies on.

## Decision 1: Apply the one-line edit by hand

- **Decision**: Edit line 83 of `specs/archive/20261025-003459-deterministic-agent-workload-test/spec.md` directly, changing the link target `../20261023-022148-shot-boundary-analyst/spec.md` to `../archive/20261023-022148-shot-boundary-analyst/spec.md`.
- **Rationale**: FR-002 requires the link target to change. The archive tooling (`refreshArchivedReferences` and `referenceOnlySpecChanges` in `.github/scripts/speckit-archive.mjs`) also rewrites plain-text `specs/<folder>` mentions at the merge, so lines 9 and 11 MAY be rewritten to `specs/archive/20261023-022148-shot-boundary-analyst` as well; both states are pure reference rewrites and are accepted. The target already exists at `specs/archive/20261023-022148-shot-boundary-analyst/spec.md`, and the relative depth from the edited spec is unchanged apart from the added `archive/` segment.
- **Alternatives considered**: Running the archive tooling by hand (rejected: the merge applies it anyway, and the direct edit keeps this change minimal); removing the link (rejected: loses the dependency reference); rewriting the link as plain text (rejected: hides rather than repairs the defect).

## Decision 2: Verify with the existing validator and a scoped diff

- **Decision**: Verify with `node .github/scripts/check-markdown.mjs` on the whole repository and `git diff` scoped to the edited spec.
- **Rationale**: SC-001 and SC-002 are defined by exactly these two checks; the validator is the one that fails phase and finalize verification today.
- **Alternatives considered**: A new link-checking test (rejected: speculative tooling, the validator already covers it); validating only the changed file with `--files` (rejected: SC-001 requires the whole repository).

## Decision 3: No architecture or environment effect

- **Decision**: Architecture Updates is `None`; no environment spec is needed.
- **Rationale**: Only a link between spec artifacts changes, and the existing Node.js tooling suffices.
- **Alternatives considered**: None.
