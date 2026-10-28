# Data Model: Merge Verification Time Limit

No persisted data. The feature changes configuration and one action input.

| Element | Fields | Rule |
| --- | --- | --- |
| Job `verify` (merge workflow) | `timeout-minutes` | 150; larger than 90, at most 360, at least one third above the time of the full verification plus the self-test |
| Action input `environment-tests-only` | string `"true"` or `"false"`, default `"false"` | `"true"` runs only the check "Environment action tests"; any other value runs the unchanged verification |
| Self-test step `selftest-verify` | `uses` the workspace action, `mode: finalize`, `environment-tests-only: "true"`, `log-file` | outcome still feeds `SPECKIT_SELFTEST_OUTCOME`; skipped when the setup self-test failed or the action does not exist in the workspace |
| Check list output (`checks`) | comma-separated names | with `environment-tests-only` it is `Environment action tests`; otherwise unchanged |

## State transitions of the verdict inputs

- `verify` outcome: from the full verification with the trusted action (unchanged).
- `SPECKIT_SELFTEST_OUTCOME`: `failure` when the setup self-test failed, otherwise the outcome of `selftest-verify`
  (`success`, `failure`, or `skipped`); unchanged.
