# Contract: environment-verify Coverage

The action's inputs (`workspace`, `mode`, `changed-files`, `log-file`) and
outputs (`checks`, `uncovered`) do not change. This contract adds the
following observable behavior.

The Manager check distinguishes its coverage scope `^src/analysts/manager/`
(paths it reports as covered) from its trigger
`^(src/analysts/manager|contracts/analyst-manager)/` (paths that make it run
in `mode=task`), because the Manager tests read the shared golden fixtures
under `contracts/analyst-manager/`. Contract files stay covered only by the
platform scope. Markdown files anywhere, including `contracts/**/*.md`, stay
covered by the existing Markdown scope and trigger the Markdown check as
before; the rows below concern non-Markdown contract files.

| Row | Situation | `checks` output | `uncovered` output | Result |
| --- | --- | --- | --- | --- |
| 1 | Only non-Markdown `contracts/**` files changed, platform solution exists, tests pass | `platform build and tests` | empty | success |
| 2 | Only non-Markdown `contracts/**` files changed, platform solution missing | empty | the contract files | success (held for review by the workflow) |
| 3 | `src/platform/**` and `contracts/**` changed | `platform build and tests` once | empty | per test result |
| 4 | Only `src/analysts/manager/**` changed, Manager solution exists, tests pass | `Analyst Manager build and tests` | empty | success |
| 5 | Only `contracts/analyst-manager/**` changed, both solutions exist, tests pass | `platform build and tests,Analyst Manager build and tests` | empty | success |
| 6 | Only `contracts/analyst-manager/**` changed, Manager solution exists, platform solution missing, tests pass | `Analyst Manager build and tests` | the contract files | success (held for review) |
| 7 | Only `contracts/analyst-manager/**` changed, Manager solution missing, platform solution exists | `platform build and tests` | empty | per test result |
| 8 | Manager test fails | `...,Analyst Manager build and tests` (last) | per scopes | failure |
| 9 | Manager `global.json` `sdk` object (`version`, `rollForward`, `allowPrerelease`) differs from the platform's, or the Manager `global.json` is missing | `...,Analyst Manager build and tests` (last) | per scopes | failure with a pin-mismatch message naming both values; restore, build, and test are skipped |
| 10 | Manager solution missing, `mode=finalize` | platform and Markdown checks only | per scopes | as before this feature |
| 11 | Manager files changed, Manager solution missing | no Manager check | the Manager files | success (held for review) |
| 12 | `src/analysts/**` outside `manager/` changed | no Manager check | those files | success (held for review) |
