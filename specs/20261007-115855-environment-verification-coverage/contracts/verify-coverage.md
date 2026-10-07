# Contract: environment-verify Coverage

The action's inputs (`workspace`, `mode`, `changed-files`, `log-file`) and
outputs (`checks`, `uncovered`) do not change. This contract adds the
following observable behavior.

| Situation | `checks` output | `uncovered` output | Result |
| --- | --- | --- | --- |
| Only `contracts/**` changed, platform solution exists, tests pass | `platform build and tests` | empty | success |
| Only `contracts/**` changed, platform solution missing | empty | the contract files | success (held for review by the workflow) |
| `src/platform/**` and `contracts/**` changed | `platform build and tests` once | empty | per test result |
| Only `src/analysts/manager/**` changed, Manager solution exists, tests pass | `Analyst Manager build and tests` | empty | success |
| Manager test fails | `...,Analyst Manager build and tests` (last) | per scopes | failure |
| Manager `global.json` SDK version differs from the platform pin | `...,Analyst Manager build and tests` (last) | per scopes | failure with a pin-mismatch message |
| Manager solution missing, `mode=finalize` | platform and Markdown checks only | per scopes | as before this feature |
| Manager files changed, Manager solution missing | no Manager check | the Manager files | success (held for review) |
| `src/analysts/**` outside `manager/` changed | no Manager check | those files | success (held for review) |
