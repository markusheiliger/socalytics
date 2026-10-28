# Data Model: Environment Exclusivity Guard Ignores Archive Reference Rewrites

No persisted data. These are the values the tooling passes between functions; the signatures are in [contracts/guard-inputs.md](contracts/guard-inputs.md).

## Branch change set

- **What**: Every path the implementation branch changes against the merge base with its base branch, including the staged change (`branchPaths` in `checkStagedTask`).
- **Fields**: a list of repository-relative paths with `/` separators.
- **Validation**: Only computed when the base branch is in the checkout; otherwise the existing "cannot be compared" reason applies.

## Reference-rewrite set

- **What**: The subset of the branch change set that is only an archive reference rewrite.
- **Membership rule** (the single criterion, shared with `referenceOnlySpecChanges`): the path is under `specs/`, outside the spec's own folder (active or archived), its change status is `M` (never added, removed, or renamed), both the base branch blob and the staged blob are readable, and `archiveRewrite(base blob, { file, folders })` equals the staged blob, where `folders` are the archives of the staged tree.
- **Additional members**: The caller also includes spec files outside the spec's folder that are in the merge-base change set but have no diff against the base branch tip (not a change against the base branch).
- **Relationships**: Input of `environmentExclusivityReasons` as `referenceRewrites`; the complement under `specs/` outside the spec's folder is the violation list of `referenceOnlySpecChanges`.

## Guard decision

- **Inputs**: `folder`, `branchPaths`, `referenceRewrites` (default `[]`).
- **Output**: a list of reasons, empty or one reason naming exactly the foreign paths.
- **Foreign path**: a path in `branchPaths` that is neither an environment action path, nor the spec's `tasks.md` (active or archived location), nor in `referenceRewrites`.
- **State transitions**: none; the decision is a pure function. With no environment action in `branchPaths` the result is always empty.
