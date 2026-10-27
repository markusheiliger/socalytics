# Research: Tick a Verified, Already-Satisfied Task

All technical-context unknowns were resolved from the existing tooling; none is open.

## R1: Where the empty change is decided today

- **Decision**: Keep the decision in `runLand` (`.github/scripts/speckit-implement-work.mjs`, `kind === 'task'` branch): `allowEmpty` is passed to `applyPatch`, which refuses a zero-byte patch with `the agent made no changes` unless `allowEmpty` is true.
- **Rationale**: it is the only place that already combines the work result, the checked-out head, and the pull request history, and it re-validates what it relies on (write token, never runs agent-written code).
- **Alternatives considered**: deciding in the work job (rejected: the work job is read-only and its claims are not trusted by the land job).

## R2: What the verification of an unchanged branch checks today

- **Finding**: `runPackage` writes `changed-files.txt` from the agent's staged paths. For an empty change that file is empty, `environment-verify` in `task` mode selects no check, and `runVerdict` records `verification: configured`, `checks: []`, `verified: true`. A passing verification of an unchanged branch therefore proves nothing about the task. Even with named files selected, the files exist and their tests pass on an unchanged branch, so the land job additionally requires evidence that the task's work exists on the branch: at least one named file differs from the base branch (`branchPaths` diff against `origin/<base>`, or a non-workflow commit touched it). Only test or build checks qualify; the "Markdown check" selected for Markdown-only files does not.
- **Decision**: for an empty change, `runPackage` writes the existing files that the task names in `tasks.md` into `changed-files.txt` (and into a new `result.taskFiles`), so the existing selection logic picks the checks for exactly those files. `result.changedFiles` stays empty.
- **Rationale**: matches the clarification (FR-002): the verification covers the task's work only when it passed and ran at least one check selected for the files the task names. No new selection logic and no change to `environment-verify`.
- **Alternatives considered**: running the full verification (rejected: slow, and covers more than the task); trusting a passing empty verification (rejected: lets an agent skip a task).

## R2a: Extracting the files a task names

- **Decision**: a small pure function in `speckit-implement-core.mjs` takes the task text and returns the distinct repository-relative paths it names (backticked tokens and bare tokens with a path separator or a known extension), keeping only those that exist in the checkout and pass `isSafeRelativePath`, capped at `MAX_CHANGED_FILES`.
- **Rationale**: `tasks.md` tasks name their files by convention; filtering by existence in the checkout makes it a recomputable, bounded input and prevents a task text from steering verification at arbitrary paths.
- **Alternatives considered**: asking the agent to list files (rejected: agent output is untrusted).

## R3: Keeping the existing `doneByPush` results unchanged (FR-004, SC-003)

- **Finding**: if the named-file verification of an empty change added failure reasons to `result.reasons`, a task that today lands through `doneByPush` could start failing on a pre-existing red test.
- **Decision**: for an empty change `runVerdict` does not add the verification's failure to `result.reasons` and does not change `verified`. It records a separate `unchanged` object: `{ status: 'passed' | 'failed' | 'not-run' | 'not-configured', head, checks, uncovered, files }`. Only the land job reads it.
- **Rationale**: the new path is purely additive; the `doneByPush` path and every changing-task path see the same result shape and outcomes as today.
- **Alternatives considered**: treating failure as a work-job reason (rejected: changes `doneByPush` outcomes).

## R4: Head comparison (FR-002, FR-003)

- **Decision**: the work result already carries `base` (`SPECKIT_HEAD`, the commit the work, and so the verification, started from). The land loop checks out `result.base` before `runLand`, so the detached checkout's `HEAD` always equals `unchanged.head` and cannot detect a moved branch. The land job therefore accepts the verified-unchanged path only when `result.unchanged.head` equals the pull request's current head (the tip fetched before the checkout, the `rev-parse HEAD` taken as `tip` in the land loop, or the head from the pull request). A mismatch means the branch moved after the work job. A mismatch fails the attempt with a reason naming the head mismatch and counts toward `MAX_TASK_ATTEMPTS` (clarification).
- **Rationale**: same pattern as the required-synchronization check (`was verified on a different head`).
- **Alternatives considered**: requeue without consuming an attempt (rejected by the clarification). A branch that moves later, during the push, is already handled by `pushTask` as a requeue from the new head, where the task runs again and is verified again.

## R5: Pushing an empty change

- **Finding**: `pushTask` re-applies `changes.patch` with `git apply` only after a rejected push; `git apply` rejects an empty patch. The `doneByPush` path has this latent gap too.
- **Decision**: skip the `git apply` when the patch file is empty (the tick is the only change). Needed so a tick-only landing survives a moved branch.

## R6: Marking the landing (FR-007)

- **Decision**: add an `unchanged-verified` flag to what `runLand` hands the reporting code. The task comment, commit message, and check-run title say "already satisfied" and name the checks that passed; the task is ticked and the implementation continues automatically (no hold for review). The attempt marker format is unchanged, so attempt counting (FR-006) is untouched.

## R7: Documentation

- **Decision**: update the rule in `.github/workflows/README.md` (around the `doneByPush` paragraph) and the matching sentence in `AGENTS.md` (Spec Twins, "A task may land without a change of its agent only when …"). No `docs/architecture/` statement is affected.
