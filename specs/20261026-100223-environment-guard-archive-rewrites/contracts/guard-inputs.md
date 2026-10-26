# Contract: Guard and Criterion Functions

Internal contract of the repository tooling (`.github/scripts/`); there is no external interface. The functions are covered by the unit tests named in [../plan.md](../plan.md).

## `referenceRewriteCheck({ git, baseSha, folder })` in `speckit-archive.mjs`

- **Inputs**: the injected `git` runner, the base branch commit `baseSha`, and the spec `folder`.
- **Returns**: `{ rewritten: string[], violations: string[], error: string | null }`.
  - Considers the staged changes under `specs/` against `baseSha` (`git diff --cached --name-status --no-renames -z`), skipping the spec's own folder (active and archived).
  - A file is in `rewritten` only when its status is `M`, both blobs are readable, and the base blob with every archive of the staged tree applied equals the staged blob; every other file is in `violations`.
  - `error` carries the existing "could not be listed" text when the listing fails; both lists are then empty.
- **Compatibility**: `referenceOnlySpecChanges({ git, baseSha, folder })` keeps its signature and returns exactly the reasons it returns today, derived from this result.

## `environmentExclusivityReasons({ folder, branchPaths, referenceRewrites = [] })` in `speckit-implement-core.mjs`

- **Pure**: no Git, file, or network access.
- **Returns**: `[]` when `branchPaths` has no environment action path or no foreign path remains; otherwise one reason, `changes to the environment actions must come from a standalone environment spec, but the branch also changes <paths>`, naming exactly the foreign paths in `branchPaths` order. The wording is unchanged.
- **Foreign**: not an environment action path, not `tasks.md` of the spec (active or archived), and not in `referenceRewrites`. Besides the paths that satisfy the shared criterion, the caller also includes in `referenceRewrites` the spec files outside the spec's folder that are in the merge-base change set but have no diff against the base branch tip (not a change against the base branch).
