# Quickstart: validating finalize reuse and reduced scope

Prerequisites: Node.js 24, Python 3 and Git on `PATH`. Run from the repository root; nothing else is needed (the
tests stub `uv` and the Markdown check and use disposable fixtures under the system temporary folder).

## 1. The action tests (reuse, reduced scope, and everything that must stay)

```bash
node --test .github/actions/environment-verify/analyst-projects.test.mjs .github/actions/environment-setup/toolchain.test.mjs
```

Expected: all tests pass, including the new `finalize reuse` and `finalize scope` groups:

| Scenario | Expected |
| --- | --- |
| Finalize passes, a second finalize with the identical workspace, list, and scope | second run executes no stub command, reports "Reused result", same `checks`, `uncovered`, `flaky`, exit 0 |
| Earlier run failed; no record; record of another identity, mode, list, or scope; damaged record | every check runs again; the verdict is that run's own |
| One file changed between the runs; same list in another order or with CRLF and blank lines | changed file: full repeat; reordered list: reused |
| Missing run id, attempt, job id, or `RUNNER_TEMP`; no Git metadata; ignored non-build file | every check runs; log says reuse was not possible; no record |
| Changed files only in the two action folders | only Environment action tests and Markdown check run; `uncovered` empty |
| A file outside, `environment-setup-x/`, `environment-verify/../x`, an empty list | full scope, same checks and order as before |
| Task and phase modes, existing tests | unchanged results |

## 2. Markdown validation

```bash
node .github/scripts/check-markdown.mjs
```

Expected: no findings in the action README and the spec artifacts.

## 3. Confinement check

```bash
git diff --name-only $(git merge-base HEAD origin/main) | grep -vE '^(\.github/actions/environment-verify/|specs/)'
```

Expected: no output (only the action folder and the spec's own files changed).

## 4. Evidence in a real job (after merge)

The next conflict resolution or `/speckit sync` of `specs/archive/20261018-110526-agents-python-environment` shows, in the
step summary of the self-test, "Reused result" and a job that records its verdict inside the 90-minute limit. This
is observed, not simulated, and is not production-readiness evidence.
