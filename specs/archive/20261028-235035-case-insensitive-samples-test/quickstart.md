# Quickstart: Case-Sensitivity-Independent Sample Tooling Test

Prerequisite: Node.js 24, run from the repository root.

## Linux (case-sensitive)

```bash
node --test tools/samples.test.mjs
```

Expected: zero failures, including 'refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links', and the case-clash assertion runs (SC-001).

## Guard check (SC-003)

Copy `tools/` to a temporary folder outside the repository, remove the case-only refusal from the copy of `samples-lib.mjs`, run `node --test samples.test.mjs` there, and expect exactly the case-clash test to fail; then delete the copy. `tools/samples-lib.mjs` in the repository stays unchanged.

## Change scope (SC-005)

```bash
git diff --name-only <base-branch>...HEAD
git status --short
```

Expected: `tools/samples.test.mjs` plus the spec folder only, in the committed changes and in the uncommitted and untracked files.

## macOS and Windows

Run the same `node --test tools/samples.test.mjs` command; expected: zero failures, with the case-clash assertion not demanded because the probe finds one entry (SC-002).

The tests are development evidence only and do not establish deployment support or production readiness.
