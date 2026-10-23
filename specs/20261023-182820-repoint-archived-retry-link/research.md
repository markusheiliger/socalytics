# Research: Repoint the archived workload retry link in the stall expiry spec

No `NEEDS CLARIFICATION` remains in the Technical Context. The decisions below record the approach.

## Decision 1: Repoint the link to the archive location

- **Decision**: Change the link target on line 80 of `specs/20261023-150804-deterministic-stall-expiry-test/spec.md` from `../20261020-224223-deterministic-simulated-workload-retry/spec.md` to `../archive/20261020-224223-deterministic-simulated-workload-retry/spec.md`.
- **Rationale**: The archived spec exists at that path, and the sibling link on the same line to the archived `harden-integration-test-timing` spec already uses the `../archive/` form. The change keeps the link text and the meaning of the bullet.
- **Alternatives considered**: Removing the link (loses the reference); replacing it with plain text (loses navigation); running `/speckit-gha-archive` for the stall expiry spec (it rewrites other specs and archives a spec that is not implemented here, which exceeds the scope).

## Decision 2: Accept the plain-text updates of the archive rewrite

- **Decision**: The edit is made with `node .github/scripts/speckit-archive.mjs 20261020-224223-deterministic-simulated-workload-retry`. Besides the link on line 80, it also rewrites the plain-text mentions on line 19 of the stall expiry `spec.md` and line 102 of its `tasks.md` to `specs/archive/20261020-224223-deterministic-simulated-workload-retry`; these are accepted side effects (`referenceOnlySpecChanges`).
- **Rationale**: Verified on a scratch copy, the tool changes exactly these three lines in two files. Using it is the only way the automation accepts a change outside the own spec folder, and the plain-text updates keep the mentions accurate.
- **Alternatives considered**: A hand edit of the single line (rejected by the automation's protected-path rule for `specs/`).

## Decision 3: Verify with the repository Markdown check

- **Decision**: Run `node .github/scripts/check-markdown.mjs` on the whole repository and require zero findings, and confirm the diff is the three lines of the archive rewrite.
- **Rationale**: The dead link is reported by exactly this check, and phase, finalize, and merge verification run the same check on the whole repository.
- **Alternatives considered**: Checking only the changed file with `--files` (does not prove the whole repository is clean, which is the requirement).
