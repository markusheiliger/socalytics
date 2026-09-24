# Complete OpenSpec change `{{CHANGE_REF}}`

Process `{{CHANGE_REF}}` from fresh `{{DEFAULT_BRANCH}}` as part of changeset issue #{{CHANGESET_ISSUE}}. Work on the branch prepared for this task. Open exactly one pull request only after the full lifecycle succeeds. Do not merge it directly; the changeset controller enables GitHub auto-merge after successful completion.

This task is explicit authorization to apply the named change, run its required verification and audit, synchronize accepted specs, and archive it only after every gate passes. It is not authorization to bypass warnings, incomplete work, blocking findings, or repository policy.

## Required workflow

1. Read `AGENTS.md`, `openspec/config.yaml`, and the generated OpenSpec skills before editing.
2. Use the `openspec-apply-change` skill for `{{CHANGE_REF}}`. Follow every task's exact `Owner: soca-*` declaration. Stop on missing, invalid, or conflicting ownership.
3. Run the narrowest relevant executable validation after each substantive edit and the documented repository validation required by the change.
4. After each completed OpenSpec task and its focused validation, mark the task complete, commit the coherent implementation and checkbox together, and push the branch. Do not batch completed tasks into an unpushed session tail.
5. Use the `openspec-verify-change` skill. Dispatch the independent `soca-verifier`; do not self-certify implementation work. Resolve every critical finding through the appropriate task owner, then reverify. Commit and push tracked verification evidence.
6. Dispatch `soca-auditor` when security, privacy, governance, provenance, tenancy, authorization, data lifecycle, threat, or control evidence is affected. Resolve blocking findings through the appropriate owner and preserve, commit, and push independent audit evidence.
7. Synchronize accepted behavioral specs and current architecture narratives. Use `openspec-sync-specs` where required, verify the synchronized result, then commit and push it.
8. Use `openspec-archive-change` only when all tasks, verification, audit, synchronization, and validation gates pass. The standing choice for a clean, fully verified change is to sync now and archive; never use that choice to override a warning or blocker. Commit and push the archive.
9. Run final validation:
   - `openspec doctor --json`
   - `openspec schema validate spec-driven --json`
   - `openspec validate --all --json`
   - `openspec status --all --json`
   - Every build or test command required by `AGENTS.md` and the change tasks
10. Commit and push final validation evidence, then open one pull request containing implementation, tests, synchronized authority documents, and the archived change.

## Pull request contract

Include all three lines in the pull request body only after the full lifecycle and every validation gate pass:

```text
Refs #{{CHANGESET_ISSUE}}
{{PR_MARKER}}
{{AUTO_MERGE_MARKER}}
```

The auto-merge marker authorizes the controller to enable squash auto-merge. GitHub must still enforce configured checks and review requirements before merging. Summarize implementation, verification, audit applicability and results, synchronization, archive location, and commands run. Do not use `Closes #{{CHANGESET_ISSUE}}`; the controller owns issue completion.

## Incomplete or blocked work

If human correction is required or the full lifecycle cannot finish, do not open a pull request, force archive, or claim completion.

1. Finish the current coherent unit, run its focused validation, update only task checkboxes supported by evidence, commit all coherent work, and push the branch.
2. Ask the user for the exact decision or correction through this task's native session. Include the pushed branch and commit SHA, remaining tasks, observed evidence, focused validation commands, and the precise artifact correction required.
3. Wait in the same task. Do not create a child blocker issue, start another cloud task, or use changeset labels as conversation state.
4. After the user responds, continue in this same session and branch through Apply, Verify, conditional Audit, Sync, Archive, and final validation.

If this task becomes terminal and cannot continue natively, leave the coherent checkpoint pushed. An operator may use the changeset controller's explicit `recover` operation to start a new session on this branch.
