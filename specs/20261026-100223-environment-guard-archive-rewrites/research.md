# Research: Environment Exclusivity Guard Ignores Archive Reference Rewrites

No `NEEDS CLARIFICATION` remains; the decisions below close the design questions.

## R1: How to share the criterion with `referenceOnlySpecChanges`

- **Decision**: Extract the per-file classification into an exported `referenceRewriteCheck` in `.github/scripts/speckit-archive.mjs` that returns the rewritten and the violating files; `referenceOnlySpecChanges` renders its unchanged reasons from the violations.
- **Rationale**: FR-002 requires one criterion that cannot drift. The loop in `referenceOnlySpecChanges` already contains the exact logic (status `M`, readable blobs, `archiveRewrite(base)` equals staged); splitting its result into two lists costs no new logic.
- **Alternatives considered**: Calling `referenceOnlySpecChanges` and parsing its message (fragile, rejected). Copying the loop into `speckit-implement-work.mjs` (the two copies would drift, rejected). Passing a predicate into the guard (the guard needs per-path data anyway; a path list is simpler and keeps it pure).

## R2: Where the Git reads happen

- **Decision**: `checkStagedTask` in `speckit-implement-work.mjs` reads Git and passes the rewritten paths to the guard as `referenceRewrites`.
- **Rationale**: FR-004 keeps the guard a pure function; `checkStagedTask` already owns the Git access and the base branch lookup.
- **Alternatives considered**: Reading blobs inside `environmentExclusivityReasons` (breaks purity and unit-testability, rejected).

## R3: Which base the criterion compares against

- **Decision**: The base branch commit `refs/remotes/origin/<baseBranch>`, as the merge-time check does with `baseSha`; the guard's change set stays the diff against the merge base that `branchPaths` already computes.
- **Rationale**: The spec assumes both. After a sync the merge base is the base tip the rewrites were made against, so a rewritten file equals the base version with the archives applied. A file that differs for any other reason fails the criterion and counts as foreign. Because `branchPaths` is the merge-base diff while the criterion lists changes against the base tip, a spec file outside the spec's folder that is in `branchPaths` but has no diff against the base tip (the base branch advanced and already contains the identical change) is not a change against the base branch; the caller passes it as a `referenceRewrites` entry so it does not count as foreign. The caller derives that set from the same `referenceRewriteCheck` result as the spec files outside the spec's folder in `branchPaths` that are absent from both `rewritten` and `violations`; it is empty whenever `error` is non-null, so a listing failure leaves every candidate foreign.
- **Alternatives considered**: Comparing against the merge base (an archive rewrite is defined against the base branch's current version, and a stale base would accept edits the merge-time check refuses, rejected).

## R4: When to run the extra Git reads

- **Decision**: Only when the branch changes an environment action and at least one changed path lies under `specs/` outside the spec's own folder.
- **Rationale**: Other branches skip the guard result anyway, so no extra work or new failure mode reaches them (FR-006).
- **Alternatives considered**: Always computing the rewrites (needless cost and risk of new errors for ordinary specs).

## R5: Failure handling

- **Decision**: A listing error or an unreadable base or staged blob keeps the file out of the rewritten set, so the guard names it as foreign.
- **Rationale**: FR-005; never silently accept what cannot be compared. The "no diff against the tip" set (spec files outside the spec's folder in `branchPaths` absent from both `rewritten` and `violations` of the same `referenceRewriteCheck` result) is empty whenever `error` is non-null, so a listing failure leaves every candidate foreign.
- **Alternatives considered**: Surfacing a separate reason for the listing error (it would add a new reason class; the existing exclusivity reason already names the path).
