---
description: Choose between implementing the active spec locally and requesting implementation on GitHub.
---

## User Input

```text
$ARGUMENTS
```

This command runs as a mandatory `before_implement` hook of `/speckit-implement`. Its result decides whether
`/speckit-implement` continues.

## Outline

1. Determine the mode without asking when possible:
   - If the environment variable `GITHUB_ACTIONS` is `true`, the mode is **local** (the server-side implementation run).
   - Else if `SPECKIT_IMPLEMENT_MODE` is `local` or `remote`, use that mode.
   - Else if the user input of `/speckit-implement` clearly says to implement locally or to request implementation
     on GitHub, use that mode.
   - Otherwise ask the user exactly one question with these two choices:
     - **Implement locally**: continue with `/speckit-implement` in this session.
     - **Request implementation on GitHub**: flag the spec for the speckit-implement GitHub Actions workflow and stop.

2. **Local mode**: reply `CONTINUE: implementing locally.` and nothing else, so `/speckit-implement` proceeds with its
   outline.

3. **Remote mode**: run `node .github/scripts/speckit-implement.mjs request` from the repository root (add
   `--folder <folder>` only if the user named a specific spec folder). Report the result as `/speckit-gha-request`
   does: on exit code 0 share the issue link and any open blockers; on exit code 1 or 2 explain the reasons and next
   steps. Then end with this line, whatever the exit code:

   `STOP: implementation was routed to GitHub. Do not continue /speckit-implement.`

   After this hook returns STOP, `/speckit-implement` must end immediately: do not read tasks, change files, or run
   any further step. If the request failed, the user can fix the reported problems and request again, or rerun
   `/speckit-implement` and choose local implementation.
