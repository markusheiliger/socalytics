---
name: soca-changeset-fixed
description: "Resume an OpenSpec changeset change after a human fixes its marked child blocker issue and pushes corrections to the preserved branch. Use when: blocker fixed, resume changeset, continue blocked OpenSpec change, or /soca-changeset-fixed with a parent changeset issue number."
allowed-tools: Bash(gh:*), Bash(node:*)
license: MIT
compatibility: Requires Node.js 24 or later, GitHub CLI authentication, and a published GitHub repository.
metadata:
  author: SocAlytics
  version: "1.0"
---

Resume one human-corrected OpenSpec change through the changeset controller. The input is the parent changeset issue number, not the blocker issue number. This skill authorizes resume; editing or closing issues directly does not.

## Input

Require one positive integer identifying the parent issue. Reject change refs, pull request numbers, and blocker issue numbers as substitutes.

## Steps

1. **Validate the parent changeset**

   Read the parent issue with `gh issue view`. Require it to be open and labeled `openspec:changeset`. Stop without dispatch when it is missing, closed, or not a changeset.

2. **Discover marked child blockers**

   Read child issues with the GitHub API:

   ```bash
   gh api "repos/{owner}/{repo}/issues/<changeset-issue>/sub_issues?per_page=100"
   ```

   Consider only open child issues labeled `openspec:change-blocker` whose body contains a valid `openspec-change-blocker:v1` marker with `parentIssue` equal to the supplied changeset issue. Parse the marker as JSON; never infer branch or change identity from the title or prose.

3. **Select one blocker**

   Auto-select when exactly one valid blocker exists. If multiple exist, ask the user to select one by issue number and change ref. If none exist, stop and report that no resumable blocker was found.

4. **Confirm the correction is pushed**

   Show the selected change ref, blocker issue, `headRef`, and recorded `checkpointSha`. Read the branch and compare history with `gh api`; require the recorded checkpoint to remain reachable from the current branch head. The controller repeats this validation. Do not modify, force-push, or recreate the branch.

5. **Dispatch resume**

   Trigger exactly one changeset-scoped workflow run:

   ```bash
   gh workflow run openspec-changeset-processing.yml \
     -f issue=<changeset-issue> \
     -f command=resume \
     -f blocker=<blocker-issue>
   ```

   Do not dispatch an agent directly, close the blocker, add mutable state labels, or create a pull request. The controller serializes work by parent changeset issue and dispatches the resumed cloud task on the preserved branch.

6. **Report**

   Return the parent changeset issue, selected blocker issue, change ref, preserved branch, and workflow dispatch result. Explain that the blocker remains open until the completed final pull request is produced.

## Guardrails

- `retry` is for failed or expired technical dispatches; this skill uses only `resume` for a human-fixed branch.
- Never authorize more than one blocker in one workflow dispatch.
- Never accept an unmarked issue or a marker belonging to another parent changeset.
- Never treat labels, comments, or task checkboxes alone as resume authorization.
- Product changes continue through apply, independent verification, conditional audit, synchronization, archive, and final validation before their only pull request is opened.
