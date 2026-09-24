# OpenSpec Change Blocker Issue Contract

A blocker issue is a child of one changeset issue and represents human correction required before one OpenSpec change can continue on its existing branch. It is not mutable workflow state and uses only the classification label `openspec:change-blocker`.

## Identity

- Title: `OpenSpec blocker: <change-ref>`
- Required label: `openspec:change-blocker`
- Parent: the owning changeset issue
- State: open until the resumed task creates its completed final pull request
- Assignee: the changeset creator when assignable; otherwise mention that user in the issue body

## Authoritative Marker

The issue body must contain exactly one marker pair with strict JSON between it:

```markdown
<!-- openspec-change-blocker:v1:start -->
{
  "version": 1,
  "parentIssue": 42,
  "change": "add-platform-persistence-foundation",
  "baseRef": "main",
  "headRef": "copilot/add-platform-persistence-foundation",
  "checkpointSha": "0123456789abcdef0123456789abcdef01234567",
  "taskId": "agent-task-id"
}
<!-- openspec-change-blocker:v1:end -->
```

`taskId` may be `"unknown"` when the task identifier is unavailable. Every other field is required. `checkpointSha` is the exact pushed commit reachable from `headRef` before issue publication.

## Human-Readable Body

After the marker, include:

- why processing stopped and the observed evidence;
- completed and remaining OpenSpec tasks;
- the exact artifact correction required;
- an exact prompt to run with `openspec-update-change` on `headRef`;
- commands that validate the correction;
- the recovery command `/soca-changeset-fixed <parentIssue>`.

Never include secrets or mutable status labels. Update the existing open child blocker for the same change instead of creating duplicates. Human correction commits must be pushed to `headRef`; they do not replace or rewrite the recorded checkpoint.
