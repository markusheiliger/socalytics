# Quickstart: Validating the Already-Satisfied Task Path

## Prerequisites

Node.js (the version of the repository's workflows) and a checkout of this branch; no Docker or network.

## Run the tooling tests

```bash
node --test .github/scripts/speckit-implement.test.mjs
node --test .github/scripts/speckit-*.test.mjs
```

## Scenarios the tests prove

Rules and data: [data-model.md](data-model.md), [contracts/task-result.md](contracts/task-result.md).

| Scenario | Expected outcome |
| --- | --- |
| Empty change, passed verification with at least one check, no uncovered file, same head | The task is ticked, no failed attempt, the comment says it was already satisfied. |
| Pushed commit before the first attempt, workflow commits on top, otherwise as above | The task is ticked. |
| Empty change, failed verification | Attempt fails with a reason, task unticked. |
| Empty change, verification missing, not configured, or covering no check | Attempt fails (`the agent made no changes`), task unticked. |
| Empty change, task names no verifiable file | Attempt fails as an empty change. |
| Empty change, verification ran on a different head | Attempt fails naming the head mismatch and counts toward `MAX_TASK_ATTEMPTS`. |
| Empty change, passed verification, but the named files exist unchanged on the base branch | Attempt fails as an empty change. |
| Empty change, task names only existing Markdown files, Markdown check passed | Attempt fails as an empty change. |
| Existing `doneByPush` cases and changing-task rules (protected paths, environment exclusivity, uncovered files) | Unchanged results. |

## Documentation checks

```bash
node .github/scripts/check-markdown.mjs
```

## Manual evidence after merge

The stopped implementation of `specs/20261026-100223-environment-guard-archive-rewrites` (pull request #344) is not continued by this path: its T004 and T005 name only files under `.github/scripts/`, which no check covers, so `/speckit retry` still fails them without a push by a person or Copilot to its branch (`doneByPush`). Observe SC-004 manually after the merge on a task that names at least one file covered by a test or build check and whose work was pushed before its first attempt: the comment says the task was already satisfied and ticked on the verification of the unchanged branch.
