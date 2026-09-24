# Resume OpenSpec change `{{CHANGE_REF}}`

Resume `{{CHANGE_REF}}` for changeset issue #{{CHANGESET_ISSUE}} on existing branch `{{HEAD_REF}}`. Blocker issue #{{BLOCKER_ISSUE}} records pushed checkpoint `{{CHECKPOINT_SHA}}`. Keep all work on this branch and open exactly one pull request only after the full lifecycle succeeds.

## Required workflow

1. Read `AGENTS.md`, `openspec/config.yaml`, the generated OpenSpec skills, and blocker issue #{{BLOCKER_ISSUE}} before editing.
2. Confirm the current branch is `{{HEAD_REF}}`, the recorded checkpoint is in its history, and the human correction commits are present. Do not reset, recreate, or replace the branch.
3. Apply the blocker's exact artifact correction using `openspec-update-change`. Reconcile the proposal, design, specs, and tasks before resuming implementation.
4. Use `openspec-apply-change` for `{{CHANGE_REF}}`. Continue from task checkboxes and repository evidence, but revisit completed tasks whose evidence was invalidated by the refined artifacts.
5. After each completed OpenSpec task and focused validation, update its checkbox, commit the coherent implementation and checkbox together, and push. Also commit and push tracked verification, audit, synchronization, archive, and final-validation evidence at each stage.
6. Use `openspec-verify-change` and independent `soca-verifier` review. Use `soca-auditor` when required by repository policy. Resolve blocking findings through the declared task owner.
7. Synchronize accepted specs and architecture, archive only after every gate passes, and run all OpenSpec and repository validation required by `AGENTS.md` and the change.
8. Open the single final pull request only after the implementation, verification, conditional audit, synchronization, archive, and validation all succeed.

## Pull request contract

Include these lines in the final pull request body:

```text
Refs #{{CHANGESET_ISSUE}}
{{PR_MARKER}}
{{AUTO_MERGE_MARKER}}
```

Summarize the human correction, implementation, verification, audit applicability and results, synchronization, archive location, and commands run. Do not close the changeset or blocker issue from the pull request; the controller owns both transitions.

## Blocked again

Do not open a partial or draft pull request. Finish and validate the current coherent unit, update supported task checkboxes, commit, and push. Then update blocker issue #{{BLOCKER_ISSUE}} using the blocker issue contract with the new pushed checkpoint SHA, current evidence, remaining tasks, and exact correction prompt. Keep the issue and branch intact for another explicit resume.
