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

## Checks

| Order | Name | Runs when | Steps |
| --- | --- | --- | --- |
| 1 | `platform build and tests` | platform scope changed or finalize, solution exists | restore, build, test of the platform solution |
| 2 | `Analyst Manager build and tests` | Manager scope changed or finalize, solution exists | SDK pin comparison, restore, build, test of the Manager solution |
| 3 | `Markdown check` | Markdown changed or finalize | `check-markdown.mjs` |

A failing check stops later checks (`status` gate) and is the last name in the
`checks` output.
