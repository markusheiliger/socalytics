# Data Model: Environment Verification Coverage

This feature has no persisted data. Its model is the set of verification
scopes and checks inside `environment-verify`.

## Verification scopes

| Scope pattern | Active when | Verified by |
| --- | --- | --- |
| `\.md$`, `^specs/`, `^\.gitignore$`, `^\.gitattributes$`, `^\.editorconfig$`, `^\.github/actions/environment-(setup\|verify)/` | always | Markdown check or no check needed (unchanged) |
| `^(src/platform\|contracts)/` | `src/platform/SocAlytics.Platform.slnx` exists | Platform build and tests |
| `^src/analysts/manager/` | `src/analysts/manager/SocAlytics.Analysts.Manager.slnx` exists | Analyst Manager build and tests |

Changed paths matching no active scope are reported in the `uncovered` output.

## Check triggers

A trigger is the path pattern that makes a check run in `mode=task`. It
equals the scope except for the Manager check, whose tests also read the
shared golden fixtures under `contracts/analyst-manager/`.

| Check | Trigger pattern (`applies`) | Scope pattern (`COVERED`) |
| --- | --- | --- |
| `platform build and tests` | `^(src/platform\|contracts)/` | `^(src/platform\|contracts)/` |
| `Analyst Manager build and tests` | `^(src/analysts/manager\|contracts/analyst-manager)/` | `^src/analysts/manager/` |

A change under `contracts/analyst-manager/` therefore runs both checks while
both solutions exist, and is covered only through the platform scope.

## Checks

| Order | Name | Runs when | Steps |
| --- | --- | --- | --- |
| 1 | `platform build and tests` | platform trigger matched or finalize, solution exists | restore, build, test of the platform solution |
| 2 | `Analyst Manager build and tests` | Manager trigger matched or finalize, solution exists | comparison of the complete `global.json` `sdk` objects, then restore, build, test of the Manager solution |
| 3 | `Markdown check` | Markdown changed or finalize | `check-markdown.mjs` |

A failing check stops later checks (`status` gate) and is the last name in the
`checks` output.
