# Contract: Sample tooling tests check

The interface of `environment-verify` (inputs, outputs, modes, shards) is unchanged. This contract states the added
behavior that the action's own tests assert.

## Selection

| Mode | Change list | Check runs |
| --- | --- | --- |
| task, phase | contains `tools/samples.test.mjs`, `tools/samples-lib.mjs`, `tools/upload-samples.mjs`, or `tools/download-samples.mjs` (deleted paths included) | yes |
| task, phase | contains only other paths (for example `tools/other.mjs`, `tools/samples.test.mjs.bak`, `tools/sub/samples-lib.mjs`, `tools/Samples-Lib.mjs`) | no |
| finalize | any | yes |
| any | nested-run marker set, `SAMPLE_TOOLING_TESTS` unset, or not the first shard | no |

## Execution

- Command: `node --test tools/samples.test.mjs`, run from the workspace with `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING=1`.
- Uses the caller's `node`; installs and downloads nothing.
- The name `Sample tooling tests` is appended to `checks` before the command runs.

## Failure messages (one recommended action each)

- `Sample tooling tests: node is required. Recommended action: run environment-setup before verification`
- `Sample tooling tests: tools/samples.test.mjs does not exist. Recommended action: restore tools/samples.test.mjs and retry verification`
- `Sample tooling tests failed (command: node --test tools/samples.test.mjs). Recommended action: reproduce this command, correct the failing sample tooling file or its test without weakening the test, and retry verification`

## Coverage

- `uncovered` never lists the four paths above; it lists every other changed `tools/` path.
- Protected paths are decided elsewhere and stay protected.

## Sharding

- The check runs in shard `1/<count>` only; a failure in any shard fails the aggregate verification.
