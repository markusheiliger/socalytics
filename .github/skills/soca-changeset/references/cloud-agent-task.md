# Complete OpenSpec change `{{CHANGE_REF}}`

Process `{{CHANGE_REF}}` from fresh `{{DEFAULT_BRANCH}}` as part of changeset issue #{{CHANGESET_ISSUE}}. Complete the full lifecycle in this single cloud-agent task and open exactly one pull request. Do not merge it directly; the changeset controller enables GitHub auto-merge after successful completion.

This task is explicit authorization to apply the named change, run its required verification and audit, synchronize accepted specs, and archive it only after every gate passes. It is not authorization to bypass warnings, incomplete work, blocking findings, or repository policy.

## Required workflow

1. Read `AGENTS.md`, `openspec/config.yaml`, and the generated OpenSpec skills before editing.
2. Use the `openspec-apply-change` skill for `{{CHANGE_REF}}`. Follow every task's exact `Owner: soca-*` declaration. Stop on missing, invalid, or conflicting ownership.
3. Run the narrowest relevant executable validation after each substantive edit and the documented repository validation required by the change.
4. Complete every approved task and maintain its checkbox only when requested artifacts and evidence exist.
5. Use the `openspec-verify-change` skill. Dispatch the independent `soca-verifier`; do not self-certify implementation work. Resolve every critical finding through the appropriate task owner, then reverify.
6. Dispatch `soca-auditor` when security, privacy, governance, provenance, tenancy, authorization, data lifecycle, threat, or control evidence is affected. Resolve blocking findings through the appropriate owner and preserve independent audit evidence.
7. Synchronize accepted behavioral specs and current architecture narratives. Use `openspec-sync-specs` where required and verify the synchronized result.
8. Use `openspec-archive-change` only when all tasks, verification, audit, synchronization, and validation gates pass. The standing choice for a clean, fully verified change is to sync now and archive; never use that choice to override a warning or blocker.
9. Run final validation:
   - `openspec doctor --json`
   - `openspec schema validate spec-driven --json`
   - `openspec validate --all --json`
   - `openspec status --all --json`
   - Every build or test command required by `AGENTS.md` and the change tasks
10. Open one pull request containing implementation, tests, synchronized authority documents, and the archived change.

## Pull request contract

Include all three lines in the pull request body only after the full lifecycle and every validation gate pass:

```text
Refs #{{CHANGESET_ISSUE}}
{{PR_MARKER}}
{{AUTO_MERGE_MARKER}}
```

The auto-merge marker authorizes the controller to enable squash auto-merge. GitHub must still enforce configured checks and review requirements before merging. Summarize implementation, verification, audit applicability and results, synchronization, archive location, and commands run. Do not use `Closes #{{CHANGESET_ISSUE}}`; the controller owns issue completion.

## Incomplete or blocked work

If the full lifecycle cannot finish within the session, do not force archive or claim completion. Preserve coherent partial work, clearly identify the blocker and remaining tasks in the pull request, retain `{{PR_MARKER}}`, and omit `{{AUTO_MERGE_MARKER}}` so the controller requests operator attention. A partial pull request does not release dependent changes.
