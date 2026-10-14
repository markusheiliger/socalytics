---
name: speckit-gha-archive
description: Archive an implemented spec to specs/archive/<folder> and rewrite every
  reference to it, for a local or manual merge.
compatibility: Requires spec-kit project structure with .specify/ directory
metadata:
  author: socalytics
  source: extension:gha
---

# Gha Archive Skill

## User Input

```text
$ARGUMENTS
```

Implemented specs move from `specs/<folder>` to `specs/archive/<folder>` in the same commit that merges their
implementation, so the base branch never has a broken link. Implementations on GitHub do this automatically in their
merge (job `Archive the spec before the merge`). This command does it for an implementation that is merged locally or by a
person, and runs as an optional `after_converge` hook of `/speckit-converge`.

If the input names a spec folder (for example `20261005-130700-platform-persistence-foundation`), use it as `FOLDER`.
Otherwise use the active feature: run `python .specify/scripts/python/check_prerequisites.py --json --paths-only` from the
repository root and take the last path segment of `FEATURE_DIR`.

## Outline

1. If the environment variable `GITHUB_ACTIONS` is `true`, reply `SKIP: implementations on GitHub archive their spec in
   the merge.` and stop.

2. Preview from the repository root: `node .github/scripts/speckit-archive.mjs <FOLDER> --check`. It refuses (exit code 1)
   when a task is still unchecked, the folder does not exist, or both `specs/<FOLDER>` and `specs/archive/<FOLDER>` exist;
   explain the reason and stop. A spec that is already archived is not moved again; only references that appeared since
   are rewritten.

3. Make sure the working tree is on the branch that merges the implementation, and that this branch contains its base
   branch (the branch it will be merged into) up to date: references that arrived there must be rewritten too. If it
   does not, tell the user to merge the base branch first and stop.

4. Ask the user to confirm the archive, showing the folder move and the files whose references change. Then run
   `node .github/scripts/speckit-archive.mjs <FOLDER>`. It moves the folder (staged as a rename) and rewrites relative
   Markdown links into and out of the folder and plain-text mentions such as `specs/<FOLDER>/contracts/…` in code
   comments; URLs are left alone.

5. Run `node .github/scripts/check-markdown.mjs` and fix any reported link that points into the old folder (report
   other findings without changing them).

6. Summarize the changes and tell the user to commit them with the implementation, for example
   `git add -A && git commit -m "Archive specs/<FOLDER> to specs/archive/<FOLDER>"`. Do not commit or push yourself.
   After the merge, `Spec Kit prepare` points the spec twin to the archive and keeps it closed as implemented.
