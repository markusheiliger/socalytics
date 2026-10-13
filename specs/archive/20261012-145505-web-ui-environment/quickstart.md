# Quickstart: Web UI Verification Environment

These scenarios validate [contracts/verify-web-ui.md](contracts/verify-web-ui.md).
Run them in Bash (for example WSL or the GitHub runner) against a scratch copy
of the repository created outside the repository working tree, for example
`scratch="$(mktemp -d)"; cp -a "$PWD/." "$scratch/repo"`, so no scratch file
appears as a changed or untracked file of the feature (FR-008). Set
`GITHUB_OUTPUT` to a temporary file and invoke the `run` script of
`.github/actions/environment-verify/action.yml` with the environment variables
the action defines (`WORKSPACE`, `MODE`, `CHANGED_FILES`, `LOG_FILE`,
`MARKDOWN_CHECK`). Delete the scratch directory afterwards.

## Prerequisites

- The tools `environment-setup` installs, after running its steps: the .NET
  SDK, Kiota 1.36.0 on `PATH`, and Playwright 1.63.0 Chromium with its
  dependencies.
- Node.js 24 and npm.
- A scratch workspace outside the repository; nothing is pushed.

## Probe project

In the scratch copy, create a minimal `src/clients/web/` npm project with:

- `@playwright/test` 1.63.0 and a committed lockfile;
- a `build` script that runs `kiota --version` and a trivial TypeScript
  compile;
- a `test` script with one passing unit test;
- a `test:e2e` script with one Playwright test that opens `about:blank` in
  Chromium.

## Scenarios

1. **Unchanged baseline, project missing**: `MODE=finalize` on the scratch
   copy without the probe project. Expect the same `checks` and `uncovered` as
   on the current tree (row 1; SC-002).
2. **Web UI change**: add the probe project, list a probe file in
   `CHANGED_FILES`, `MODE=task`. Expect `checks=Web UI build and tests`, empty
   `uncovered`, exit 0, and no browser download in the log (rows 2; SC-001).
3. **Platform API change**: list only a file under
   `src/platform/SocAlytics.Platform.Api/`, `MODE=task`. Expect
   `platform build and tests,Web UI build and tests` (row 3).
4. **Other platform change**: list only a file under
   `src/platform/SocAlytics.Platform.Domain/`, `MODE=task`. Expect
   `platform build and tests` only (row 4).
5. **Lockfile mismatch**: change a dependency version in `package.json`
   without updating the lockfile. Expect exit 1 and
   `Web UI step failed: install` (row 6; SC-003).
6. **Playwright pin mismatch**: pin `@playwright/test` to another version and
   update the lockfile. Expect exit 1 and `Web UI step failed: playwright pin`
   with both versions (row 7).
7. **Failing unit test** and **failing browser test**: expect
   `Web UI step failed: unit tests` and `Web UI step failed: browser tests`
   (rows 8 and 9).
8. **Project missing, Web UI file changed**: remove `package.json` and list a
   probe `.ts` file. Expect no Web UI check and the file in `uncovered`
   (row 10).
9. **Outside the Web UI project**: list `src/clients/other/probe.ts`. Expect
   the file in `uncovered` (row 11).
10. **Pins**: `kiota --version` prints 1.36.0, and the Playwright install
    output names version 1.63.0 (SC-005).

## Evidence

The merge workflow self-tests the branch's own actions, but the Web UI project
does not exist when this feature merges, so that self-test proves only the
unchanged baseline (SC-002). Web UI coverage (SC-001, SC-003, SC-005) is proven
by scenarios 2–10, which the final validation task runs and reports in its
final summary: one line per scenario with the expected and actual `checks`,
`uncovered`, and exit code. The reviewer reads that summary before approving
the environment pull request, which is held for human review.
