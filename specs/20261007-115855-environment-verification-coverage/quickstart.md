# Quickstart: Environment Verification Coverage

These scenarios validate [contracts/verify-coverage.md](contracts/verify-coverage.md).
Run them in Bash (for example WSL or the GitHub runner) against a scratch copy
of the repository created OUTSIDE the repository working tree, for example
`scratch="$(mktemp -d)"; cp -a "$PWD/." "$scratch/repo"`, so no scratch file
appears as a changed or untracked file of the feature (FR-007). Set
`GITHUB_OUTPUT` to a temporary file and invoke the `run` script of
`.github/actions/environment-verify/action.yml` with the environment variables
the action defines (`WORKSPACE` pointing at `$scratch/repo`, `MODE`,
`CHANGED_FILES`, `LOG_FILE`, `MARKDOWN_CHECK`). Delete the scratch directory
afterwards.

## Prerequisites

- .NET SDK matching `src/platform/global.json`, Node.js, and the Markdown
  linters installed by `environment-setup`.
- `softhsm2` installed (as `environment-setup` does) for scenario 9.
- A scratch workspace outside the repository; nothing is pushed.

## Scenarios

Run the scenarios in this order. Only scenarios 1 and 2 run the full platform
restore, build, and test; the later scenarios work on a scratch copy without
the platform solution so that they check only the Manager check and the
outputs.

1. **Unchanged baseline, Manager missing**: `MODE=finalize` on the current
   tree. Expect `checks=platform build and tests,Markdown check` (no Manager
   check), empty `uncovered`, exit 0 (contract row 10; SC-002).
2. **Contract-only change**: add only a sample schema
   `contracts/sample/v1/sample.schema.json` (no Markdown file, so the Markdown
   check is not triggered); list it in `CHANGED_FILES`; `MODE=task`. Expect
   `checks=platform build and tests` (once), empty `uncovered`, exit 0 (rows 1
   and 3; SC-001).
3. **Contracts without platform**: delete `src/platform/SocAlytics.Platform.slnx`
   in the scratch copy and repeat scenario 2. Expect empty `checks` and the
   sample schema in `uncovered` (row 2). Keep the platform solution deleted
   for scenarios 4–8.
4. **Manager present**: create a minimal Manager solution, for example
   `cp src/platform/global.json src/analysts/manager/global.json`,
   `dotnet new xunit -o src/analysts/manager/Probe.Tests`, and
   `dotnet new sln -n SocAlytics.Analysts.Manager -o src/analysts/manager --format slnx`
   followed by `dotnet sln src/analysts/manager/SocAlytics.Analysts.Manager.slnx add src/analysts/manager/Probe.Tests`;
   list a Manager file in `CHANGED_FILES`; `MODE=task`. Expect
   `checks=Analyst Manager build and tests`, empty `uncovered`, exit 0
   (row 4; SC-001).
5. **Manager fixture change**: list only
   `contracts/analyst-manager/registration/v1/registration.schema.json` (create
   it) in `CHANGED_FILES`; `MODE=task`. Expect
   `checks=Analyst Manager build and tests` and that file in `uncovered`,
   because the platform solution is deleted in this copy (row 6); with the
   platform solution present the platform check would run first and the file
   would be covered (row 5).
6. **Manager failure**: make the probe test fail and repeat scenario 4. Expect
   exit 1 and `Analyst Manager build and tests` as the last check (row 8;
   SC-003).
7. **Pin mismatch**: restore the passing test, change only `sdk.rollForward`
   in `src/analysts/manager/global.json` (for example to `disable`), and repeat
   scenario 4. Expect exit 1, `Analyst Manager build and tests` as the last
   check, a log message naming both `sdk` values, and no restore in the log
   (row 9).
8. **Outside the Manager**: list a non-Markdown file under `src/analysts/`
   outside `manager/` (for example `src/analysts/other/Probe.cs`) in
   `CHANGED_FILES`; `MODE=task`. Expect empty `checks` and that file in
   `uncovered` (row 12).
9. **SoftHSM2 available**: after `environment-setup`, `softhsm2-util --version`
   succeeds and `/usr/lib/softhsm/libsofthsm2.so` exists.

## Evidence

On GitHub, the merge workflow self-tests the branch's own actions. Because
neither `contracts/` nor the Manager solution exists when this feature merges,
that self-test proves only the unchanged baseline (SC-002). Contract and
Manager coverage (SC-001, SC-003) are proven by scenarios 2–8, which the final
validation task runs and reports in its final summary (one line per scenario
with the expected and actual `checks`, `uncovered`, and exit code). The
reviewer reads that summary before approving the environment pull request,
which is held for human review.
