# Contract: environment-verify finalize reuse and reduced scope

The contract of the composite action `.github/actions/environment-verify`, which the implementation workflows
already call. It is component-local: the action and its Node tests own and validate it. No workflow, input, or output
name changes.

## Unchanged interface

- Inputs: `workspace`, `mode`, `changed-files`, `log-file`. Outputs: `checks`, `uncovered`, `flaky`.
- The step fails when a check fails; the outputs are always written.
- Environment read (provided by GitHub Actions, not inputs): `RUNNER_TEMP`, `GITHUB_RUN_ID`, `GITHUB_RUN_ATTEMPT`,
  `GITHUB_JOB`, `GITHUB_OUTPUT`, `GITHUB_STEP_SUMMARY`.

## Behavior of `finalize` mode

| Situation | Behavior | Outputs | Exit |
| --- | --- | --- | --- |
| Changed paths all in `.github/actions/environment-setup/` or `.github/actions/environment-verify/` | Scope `environment`: Environment action tests, then Markdown check; log and summary say the scope was reduced and why | `checks` lists those two; `uncovered` empty | per checks |
| Any path elsewhere, an empty or missing list, a lookalike prefix, a path with `..`, an absolute path, or a backslash | Scope `full`: every check as before | as before | per checks |
| A record for this job passed, with identical identity, mode, changed-files content, and scope | Reused: no check runs; log and summary say "Reused result" and name the earlier run | the record's `checks`, `uncovered`, `flaky` | 0 |
| No record, an unreadable, incomplete, or foreign record, a failed earlier run, another identity, mode, list, or scope | Every check of the scope runs | the run's own | per checks |
| `RUNNER_TEMP`, run id, attempt, or job id missing or empty, or identity uncertain | Every check of the scope runs; log says reuse was not possible; nothing is read or written | the run's own | per checks |
| `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` set | Nested test run: no record is read or written | the run's own | per checks |

`task` and `phase` modes never read or write a record and never reduce their scope.

## Invariants

1. A pass is never produced from a failed, missing, or unreadable record.
2. A reduced run's `checks` never lists a check that did not run, and `uncovered` is never smaller than what the run's
   checks cover.
3. A record exists only after a complete pass whose workspace identity did not change during the run.
4. The record never leaves the job: it lives in `RUNNER_TEMP`, never in the repository or an artifact.

The record format is in [data-model.md](../data-model.md).
