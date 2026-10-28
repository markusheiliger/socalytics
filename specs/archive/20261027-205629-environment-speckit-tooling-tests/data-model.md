# Data Model: Spec Kit Tooling Tests in Environment Verification

The feature persists no data. These are the declarations and result entries the action reads and writes.

## Spec Kit tooling tests check

| Field | Value |
| --- | --- |
| Name (as reported in `checks`) | `Spec Kit tooling tests` |
| Command | `node --test .github/scripts/speckit-*.test.mjs` (the matched files are listed in the log and the failure message) |
| Runs when | status is 0, first shard (`MAIN`), `SPECKIT_TOOLING_TESTS` set, no nested-run marker, and either mode `finalize` or a changed path matches the trigger |
| Skipped when | the marker `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` is set, an earlier check failed, the change is documentation-only, or nothing relevant changed outside finalize mode |
| Position | after "Environment action tests", before "Markdown check" |
| Fails when | a test fails, no `speckit-*.test.mjs` file exists, or `node` is unavailable |
| Failure message | `Spec Kit tooling tests failed (command: node --test <files>). Recommended action: reproduce this command, correct the failing Spec Kit tooling script or its test without weakening the test, and retry verification` (one recommendation) |

## Tooling scope

| Field | Value |
| --- | --- |
| Pattern | `^\.github/scripts/speckit-[^/]+\.mjs$` |
| Matches | `.github/scripts/speckit-*.mjs`, including `speckit-*.test.mjs` |
| Effect | Trigger of the check; covered path in the plan (`uncovered` excludes it) |
| Does not match | other `.github/scripts/` files, subfolders, workflows, skills, other composite actions, Markdown (still covered by the Markdown check) |

## Verification result entries

- `checks` (output): gains `Spec Kit tooling tests` when the check ran; order as above.
- `uncovered` (output) and `uncovered` (plan): tooling files are absent; every other previously uncovered or protected path stays listed.
- `scope` (plan): for a tooling file, the reason reads "`<file>` is a Spec Kit tooling file covered by the Spec Kit tooling tests"; `documentationOnly` stays false.

No state transitions. Every existing check, input, output, and plan field is unchanged.
