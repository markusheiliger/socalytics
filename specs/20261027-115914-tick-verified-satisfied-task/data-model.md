# Data Model: Tick a Verified, Already-Satisfied Task

No persisted store changes. The only data are the fields of the work job's `result.json` for a task, and values the land job derives.

## Task work result (`result.json`, kind `task`)

Existing fields are unchanged (`kind`, `task`, `agentExit`, `reasons`, `checks`, `verified`, `patchBytes`, `changedFiles`, `base`, `verification`, `verificationMode`, `flaky`, `uncovered`).

New, written only when `patchBytes === 0`:

| Field | Written by | Meaning |
| --- | --- | --- |
| `taskFiles` | `package` | Existing, safe, bounded repository paths the task's text names; the verification is selected for these. |
| `unchanged.status` | `verdict` | `passed` (verification succeeded), `failed`, `not-run` (no verification output), or `not-configured` (no `environment-verify` action). |
| `unchanged.head` | `verdict` | The commit the verification ran on (the result's `base`). |
| `unchanged.checks` | `verdict` | Names of the checks that ran and passed. |
| `unchanged.uncovered` | `verdict` | Named files no check covers. |
| `unchanged.files` | `verdict` | The files the verification was selected for (copy of `taskFiles`). |

Rules: `unchanged` never adds to `reasons` and never changes `verified`; the land job alone interprets it.

## Already-satisfied decision (derived in the land job)

An empty agent change lands when either holds:

1. `doneByPush` is true (existing rule, unchanged); or
2. all of: `unchanged.status === 'passed'`, `unchanged.checks.length >= 1`, `unchanged.uncovered` is empty, `unchanged.files` is non-empty, `unchanged.head` equals the pull request's current head (the tip fetched before the land checkout, not the detached checkout of the work head, which always equals `unchanged.head`), at least one check in `unchanged.checks` qualifies (a check qualifies unless its name is exactly "Markdown check"; the land job identifies it by that name alone), and the task's work is evidenced on the branch: at least one file in `unchanged.files` that no other task in `tasks.md` names (a file specific to this task, recomputed by the land job) differs from the base branch (the `branchPaths` diff against `origin/<base>`).

Otherwise the attempt fails with the first applicable reason: `the agent made no changes` plus the cause — head mismatch, verification failed, verification not run or not configured, task names no verifiable file, no (qualifying) check covered the task's files, no named file is specific to this task, or no specific named file differs from the base branch.

## State transitions

Task attempt: `started` → agent change empty → (satisfied by rule 1 or 2) → `ticked` and `landed`; or → `failed` (counts toward `MAX_TASK_ATTEMPTS`). No new states, markers, or labels.
