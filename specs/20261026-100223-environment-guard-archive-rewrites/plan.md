# Implementation Plan: Environment Exclusivity Guard Ignores Archive Reference Rewrites

**Branch**: `20261026-100223-environment-guard-archive-rewrites` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261026-100223-environment-guard-archive-rewrites/spec.md`

## Summary

`environmentExclusivityReasons` counts every path the implementation branch changes against its base branch, so the spec files that `refreshArchivedReferences` rewrites after each base branch merge make an environment spec fail every land attempt. The plan extracts the per-file criterion of `referenceOnlySpecChanges` (modified, not added or removed, and equal to the base branch version with every archive of the staged tree applied) into one exported function in `speckit-archive.mjs` that both the merge-time check and the guard use. `checkStagedTask` determines the reference-rewrite paths from Git and passes them to the still pure `environmentExclusivityReasons`, which skips them. Everything else keeps failing the guard. The rule descriptions in `AGENTS.md`, `README.md`, the workflows README, and the gha extension README source (`.specify/extension-src/gha/README.md`) state the exception.

## Technical Context

**Language/Version**: Node.js 24 ES modules (`.mjs`), the repository tooling runtime

**Primary Dependencies**: none new; `node:test`, `node:assert`, and the `git` CLI through the existing `git` runner injected into the scripts

**Storage**: N/A (reads Git blobs and the index only)

**Testing**: `node --test .github/scripts/speckit-*.test.mjs` (unit tests in `speckit-implement-core.test.mjs` and `speckit-archive.test.mjs`; temporary-repository tests in `speckit-implement.test.mjs`)

**Target Platform**: GitHub Actions runners and local checkouts (Linux, Windows, macOS)

**Project Type**: repository tooling scripts (not product code)

**Performance Goals**: no measurable change; at most two `git cat-file` reads per modified spec file outside the spec's folder

**Constraints**: the guard stays a pure function of its inputs (FR-004); the criterion is shared, never reimplemented (FR-002); an unreadable blob means foreign (FR-005); the behavior and results of `referenceOnlySpecChanges` and the other staging reasons stay unchanged (FR-006)

**Scale/Scope**: three script files change (`speckit-archive.mjs`, `speckit-implement-core.mjs`, `speckit-implement-work.mjs`), their tests, and four documentation files

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Status | Evidence |
| ----------------------- | ------ | -------- |
| I. Architecture authority | PASS | No `docs/architecture/` narrative describes the Spec Kit tooling guard; Architecture Updates is `None` (below). |
| II. Source-area ownership | PASS | No `src/` change; only `.github/scripts/` and repository documentation. |
| III. API-first control plane | PASS (not applicable) | No product capability, contract, or API. |
| IV. Evidence over claims | PASS | Unit tests plus a temporary-repository test of `checkStagedTask` ([quickstart.md](quickstart.md)); the existing tooling tests must pass unchanged. |
| V. Focused, minimal changes | PASS | One extracted function, one new optional input, one caller change; no new dependency or abstraction beyond the shared criterion the spec requires. |
| Environment features | PASS | The tasks need only Node.js and `git`, which `environment-setup` provides; this spec changes no environment action and names no environment spec under dependencies. |

Post-design re-check: unchanged, all gates still pass.

## Project Structure

### Documentation (this feature)

```text
specs/20261026-100223-environment-guard-archive-rewrites/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output: the internal function contract
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
.github/scripts/
├── speckit-archive.mjs               # extract referenceRewriteCheck; referenceOnlySpecChanges reuses it
├── speckit-archive.test.mjs          # tests of the extracted function and unchanged referenceOnlySpecChanges
├── speckit-implement-core.mjs        # environmentExclusivityReasons accepts referenceRewrites
├── speckit-implement-core.test.mjs   # pure guard cases (FR-007)
├── speckit-implement-work.mjs        # checkStagedTask computes and passes the rewrites
└── speckit-implement.test.mjs        # checkStagedTask against a temporary repository with an archived spec
AGENTS.md                             # rule description (Spec Kit section)
README.md                             # "Specs extend these actions" item 1
.github/workflows/README.md           # environment actions rule description
.specify/extension-src/gha/README.md  # environment-spec description (extension source, not the generated copies)
```

**Structure Decision**: Repository tooling only. The shared criterion lives next to `archiveRewrite` and `referenceOnlySpecChanges` in `speckit-archive.mjs`, the single source of truth for archive rewrites; the guard stays in `speckit-implement-core.mjs` and receives its result as data.

## Design

1. **Shared criterion** (`speckit-archive.mjs`): export `referenceRewriteCheck({ git, baseSha, folder })`, which lists the staged changes under `specs/` against `baseSha` exactly as `referenceOnlySpecChanges` does today and classifies every file outside the spec's own folder as `rewritten` (status `M`, both blobs readable, base version with every archive of the staged tree applied equals the staged blob) or `violating` (anything else). It returns `{ rewritten, violations, error }`, where `error` is the existing "could not be listed" text. `referenceOnlySpecChanges` becomes a thin wrapper that renders the same reasons from `violations` and `error`, so its results are byte-identical.
2. **Pure guard** (`speckit-implement-core.mjs`): `environmentExclusivityReasons({ folder, branchPaths, referenceRewrites = [] })` removes the paths in `referenceRewrites` from the foreign list. It stays pure; a missing or empty list gives today's behavior. Its code comment states the exception (FR-008).
3. **Caller** (`speckit-implement-work.mjs`): `checkStagedTask` calls the shared check against the base branch commit `refs/remotes/origin/<baseBranch>` only when the branch changes an environment action and has candidate paths under `specs/`, and passes `rewritten` as `referenceRewrites`. A path outside the spec's folder that is in `branchPaths` (diff against the merge base) but has no diff against the base branch tip is not a change against the base branch, so the caller also passes it as a `referenceRewrites` entry and it does not count as foreign. The caller derives this "no diff against the tip" set from the same `referenceRewriteCheck` result: the spec files outside the spec's folder in `branchPaths` that are absent from both `rewritten` and `violations`. The set is empty whenever `error` is non-null, so a listing failure leaves every candidate foreign. A listing error or an unreadable blob leaves the file out of `rewritten`, so it counts as foreign (FR-005).
4. **Tests** (FR-007): pure cases in `speckit-implement-core.test.mjs` (accepted rewrite, non-reference edit, added, deleted, `.github/` file, source file, mixed branch naming only foreign paths, no environment change); a temporary-repository case of `checkStagedTask` with an archived spec in `speckit-implement.test.mjs`; tests of the extracted function in `speckit-archive.test.mjs`; the existing `referenceOnlySpecChanges` tests unchanged.
5. **Documentation** (FR-008): `AGENTS.md`, `README.md`, `.github/workflows/README.md`, and `.specify/extension-src/gha/README.md` (the extension source; the generated `.specify/extensions/` copies are never hand-edited) state that archive reference rewrites outside the spec's folder do not count as other changes; the guard's code comment says the same.

## Architecture Updates

None. The change affects only the Spec Kit implementation tooling and its repository documentation. No narrative in `docs/architecture/` describes the environment exclusivity guard or the archive rewrites, and no implementation-status statement there becomes true or false. The documentation tasks cover `AGENTS.md`, `README.md`, `.github/workflows/README.md`, and `.specify/extension-src/gha/README.md`, which are outside `docs/architecture/`.

## Complexity Tracking

No constitution violations; nothing to justify.
