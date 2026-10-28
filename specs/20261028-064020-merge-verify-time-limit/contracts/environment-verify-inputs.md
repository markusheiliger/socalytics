# Contract: `environment-verify` input `environment-tests-only`

The composite action `.github/actions/environment-verify` keeps its inputs `workspace`, `mode`, `changed-files`,
`log-file`, and `shard`, its outputs `checks`, `uncovered`, `flaky`, and `plan`, and adds one input.

| Input | Required | Default | Meaning |
| --- | --- | --- | --- |
| `environment-tests-only` | no | `"false"` | `"true"`: run only the check "Environment action tests" (the Node tests and fixtures of both environment actions) from this action's folder and skip the verification plan, the components, the Python projects, and the Markdown check |

## Behavior with `"true"`

- `checks` is `Environment action tests` when the tests ran; the step fails when they fail, with the existing single
  "Recommended action" line.
- The step fails with one recommended action when the tests are not configured (`ENVIRONMENT_TESTS` empty), so a
  self-test never succeeds without running a test.
- `plan` and `flaky` are empty, and `uncovered` lists every line of `changed-files` (none when the input is not
  given), because no plan decides coverage.
- The marker `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` still makes a nested run skip the tests; a nested run does not
  fail.

## Behavior with `"false"` or empty

Unchanged from today.

## Caller

`.github/workflows/speckit-implement-merge.yml`, step `Self-test the changed environment verification`, passes
`environment-tests-only: "true"`. The step `Verify the merged result` does not pass it.
