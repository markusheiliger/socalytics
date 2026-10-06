---
name: speckit-gha-request
description: Request implementation of the active spec by the speckit-implement GitHub
  Actions workflow.
compatibility: Requires spec-kit project structure with .specify/ directory
metadata:
  author: socalytics
  source: extension:gha
---

# Gha Request Skill

## User Input

```text
$ARGUMENTS
```

If the input names a spec folder (for example `20261005-130700-platform-persistence-foundation`), use it as `FOLDER`.
Otherwise leave `FOLDER` empty so the active feature from `.specify/feature.json` is used.

## Outline

1. From the repository root, run exactly one of:
   - `node .github/scripts/speckit-implement.mjs request --folder <FOLDER>` when `FOLDER` is set;
   - `node .github/scripts/speckit-implement.mjs request` otherwise.

   Do not edit files, create commits, or change labels yourself; the command does everything.

2. Report the command output to the user, interpreting its exit code:
   - **0**: the spec twin is flagged `speckit:stage:implement` (or already was). Share the issue link and say that the
     Spec Kit prepare workflow validates the request on GitHub. List any open blockers it reported; the
     speckit-implement workflow starts after they are closed.
   - **1**: the request was not made. Explain every reason it printed and the next step, for example:
     - spec not merged to the default branch, or no twin yet: merge the spec, plan, and tasks first; the twin appears after the merge;
     - stage is not `tasked`: run `/speckit-plan` and `/speckit-tasks`, then merge;
     - checklist items not checked: resolve and check them in `checklists/`, then merge;
     - tasks already completed: the feature is already implemented on the default branch.
   - **2**: a usage or authentication problem, for example a missing GitHub token (`gh auth login`) or an unknown
     folder. Show the message and how to fix it.
