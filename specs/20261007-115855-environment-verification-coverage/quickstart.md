# Quickstart: Environment Verification Coverage

These scenarios validate [contracts/verify-coverage.md](contracts/verify-coverage.md).
Run them in Bash (for example WSL or the GitHub runner) from a scratch copy of
the repository, setting `GITHUB_OUTPUT` to a temporary file and invoking the
`run` script of `.github/actions/environment-verify/action.yml` with the
environment variables the action defines (`WORKSPACE`, `MODE`,
`CHANGED_FILES`, `LOG_FILE`, `MARKDOWN_CHECK`).

## Prerequisites

- .NET SDK matching `src/platform/global.json`, Node.js, and the Markdown
  linters installed by `environment-setup`.
- `softhsm2` installed (as `environment-setup` does) for scenario 9.
- A scratch workspace; nothing is pushed.

## Scenarios

1. **Unchanged baseline**: `MODE=finalize` on the current tree. Expect
   `checks=platform build and tests,Markdown check`, empty `uncovered`, exit 0.
2. **Contract-only change**: add `contracts/README.md` and a sample schema;
   list them in `CHANGED_FILES`; `MODE=task`. Expect the platform check to run
   once and `uncovered` to be empty.
3. **Contracts without platform**: delete `src/platform/SocAlytics.Platform.slnx`
   in the scratch copy and repeat scenario 2. Expect no platform check and the
   contract files in `uncovered`.
4. **Manager missing**: `MODE=finalize`. Expect no Manager check and exit 0.
5. **Manager present**: create a minimal
   `src/analysts/manager/SocAlytics.Analysts.Manager.slnx` with one test
   project and a `global.json` identical to the platform's; list a Manager file
   in `CHANGED_FILES`. Expect `Analyst Manager build and tests` in `checks`,
   empty `uncovered`, exit 0.
6. **Manager failure**: make the test fail. Expect exit 1 and
   `Analyst Manager build and tests` as the last check.
7. **Pin mismatch**: change the Manager `global.json` SDK version. Expect exit
   1 with a message naming both versions.
8. **Outside the Manager**: change a non-Markdown file under
   `src/analysts/` outside `manager/`. Expect that file in `uncovered`.

9. **SoftHSM2 available**: after `environment-setup`, `softhsm2-util --version`
   succeeds and `/usr/lib/softhsm/libsofthsm2.so` exists.

On GitHub, the merge workflow self-tests the branch's own actions; the
environment pull request is then held for human review.
