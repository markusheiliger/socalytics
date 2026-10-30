# Data Model: Sample Tooling Tests in Environment Verification

No persisted data. The feature adds one check and one path scope to the verification result.

## Sample tooling tests check

| Field | Value |
| --- | --- |
| Name (entry in the `checks` output) | `Sample tooling tests` |
| Command | `node --test tools/samples.test.mjs` |
| Selection | task or phase mode: a changed or deleted path matches the scope below; finalize mode: always; never in a documentation-only change; never when the nested-run marker `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` is set |
| Shard | first shard only (shared check) |
| Budget | part of `MAIN_MINUTES` (6) |
| Position | after "Spec Kit tooling tests", before "Markdown check" |
| Failure states | `node` missing; `tools/samples.test.mjs` missing; test failure; each sets the step status to failed and prints one recommended action |

## Sample tooling scope

Exactly these four repository paths, matched whole, case-sensitively, with forward slashes:

- `tools/samples.test.mjs`
- `tools/samples-lib.mjs`
- `tools/upload-samples.mjs`
- `tools/download-samples.mjs`

Everything else under `tools/` (other scripts, suffixed or nested or case-variant names) is not in the scope and stays
uncovered, except Markdown, which the Markdown rule covers.

## Verification result (existing, extended)

| Field | Effect |
| --- | --- |
| `checks` | contains `Sample tooling tests` when the check ran or failed |
| `uncovered` | no longer lists the four paths; lists any other `tools/` path |
| `plan.scope` | for a change whose first non-Markdown path is in the scope, the reason names the sample tooling tests |
| `plan.shards` | unchanged structure; the first shard's minutes include the new budget |

## State

None. The check is stateless and starts no process other than `node --test`.
