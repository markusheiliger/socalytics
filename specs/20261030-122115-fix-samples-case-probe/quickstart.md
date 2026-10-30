# Quickstart: Validate the Case-Probe Fix

Prerequisite: Node.js 24 on `PATH`. No Docker, network, or install step is needed. Run from the repository root.

## 1. The tooling test passes on a case-sensitive file system (Linux)

```bash
node --test tools/samples.test.mjs
```

Expected: 13 tests pass, 0 fail, 0 skipped by the case probe (the "refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links" test passes and its case-clash assertion runs). The link check may be skipped only on a platform that cannot create the link.

## 2. The assertion is really executed

Temporarily make `discoverVideos` in `tools/samples-lib.mjs` stop refusing case-only clashes (in a scratch copy or by a change that you revert), then rerun step 1.

Expected: exactly the case-clash test fails. Revert the scratch change; `git diff --stat` must list only `tools/samples.test.mjs` outside this spec folder.

## 3. Case-insensitive file systems (macOS, Windows; manual)

```bash
node --test tools/samples.test.mjs
```

Expected: all tests pass; the case-clash assertion is not run because `probe/A.mp4` is visible after `probe/a.mp4` was created.

## 4. Scope check

```bash
git diff --name-only <base branch> -- . ':!specs'
```

Here `<base branch>` is the default branch, such as `origin/main`.

Expected output: `tools/samples.test.mjs` only.

See [spec.md](spec.md) for the requirements and [research.md](research.md) for the design decisions.
