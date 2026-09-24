# Recover OpenSpec change `{{CHANGE_REF}}`

Recover `{{CHANGE_REF}}` for changeset issue #{{CHANGESET_ISSUE}} on existing branch `{{HEAD_REF}}`. The previous Agent Task `{{PREVIOUS_TASK_ID}}` is terminal, and pushed checkpoint `{{CHECKPOINT_SHA}}` was validated against the branch before this replacement session was dispatched.

This is terminal recovery, not the normal human-correction path. Keep all work on the existing branch and open exactly one pull request only after the full lifecycle succeeds.

## Required workflow

1. Read `AGENTS.md`, `openspec/config.yaml`, and the generated OpenSpec skills before editing.
2. Confirm the current branch is `{{HEAD_REF}}`, the recorded checkpoint is in its history, and coherent work from the previous task is present. Do not reset, recreate, replace, or force-push the branch.
3. Inspect repository evidence and OpenSpec task checkboxes to identify the last verified point. Revisit completed tasks whose evidence is missing or invalidated.
4. Use `openspec-apply-change` for `{{CHANGE_REF}}` and continue through every remaining declared task and focused validation.
5. Use `openspec-verify-change` and independent `soca-verifier` review. Use `soca-auditor` when required by repository policy. Resolve blocking findings through the declared task owner.
6. Synchronize accepted specs and architecture, archive only after every gate passes, and run all OpenSpec and repository validation required by `AGENTS.md` and the change.
7. Commit and push coherent evidence at each stage. Open the single final pull request only after implementation, verification, conditional audit, synchronization, archive, and validation all succeed.

## Pull request contract

Include these lines in the final pull request body:

```text
Refs #{{CHANGESET_ISSUE}}
{{PR_MARKER}}
{{AUTO_MERGE_MARKER}}
```

Summarize the recovered work, implementation, verification, audit applicability and results, synchronization, archive location, and commands run. Do not close the changeset from the pull request; the controller owns that transition.

## Blocked again

Do not open a partial or draft pull request. Finish and validate the current coherent unit, update supported task checkboxes, commit, and push. Ask the user for the exact correction through this task's native session and wait here. A further terminal replacement requires another explicit controller `recover` operation.
