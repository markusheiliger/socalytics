# Data Model: Resilient .NET Environment Setup

Nothing is persisted; these are the in-memory shapes of `dotnet-resolve.mjs`.

## SDK requirement

| Field | Type | Rule |
| --- | --- | --- |
| `file` | string | Repository-relative `global.json` path (`src/platform/global.json`, `src/analysts/manager/global.json`) |
| `version` | string | Pinned SDK version, for example `10.0.400` |
| `rollForward` | enum | One of the `global.json` policies; absent means `latestPatch` |
| `allowPrerelease` | boolean | Absent means `true` only for a prerelease pin; this repository pins `false` |

The requirement set is the list of both files. A missing file, an unparseable file, or a missing `sdk.version` fails visibly naming the file. Different `version`, `rollForward`, or `allowPrerelease` values in the two files do not fail by themselves: a candidate must satisfy both. Only a combination no candidate can satisfy ends in total failure naming both files.

## Download source

| Field | Type | Values |
| --- | --- | --- |
| `id` | string | `primary` (setup-dotnet attempts), `runner` (SDK on the runner), `alternative` (official install script with the secondary feed) |
| `order` | number | 1 `primary`, 2 `runner`, 3 `alternative`; `primary` is always first |

## Setup attempt

| Field | Type | Meaning |
| --- | --- | --- |
| `source` | string | Source `id` |
| `number` | number | 1-based attempt number within the source |
| `outcome` | enum | `success`, `failure`, `cancelled`, `skipped` (as reported by the workflow step) or `rejected` (acceptance rule) |
| `version` | string or null | Candidate SDK version when the source produced one |
| `cause` | string or null | Short cause on failure or rejection, free of credentials |

## Decision

| Field | Type | Meaning |
| --- | --- | --- |
| `resolved` | string or null | Resolved SDK version |
| `source` | string or null | Source that supplied it |
| `fallbackUsed` | boolean | `false` only when `source` is `primary` |
| `attempts` | Setup attempt[] | Every attempt, in order |
| `failure` | string or null | The single failure message when `resolved` is null |

## State transitions

```text
primary attempt 1..3 --first success--> validate against both requirements
        | accepted -> Decision(source=primary, fallbackUsed=false)
        | rejected or all attempts failed -> runner toolchain
runner SDKs --highest satisfying--> Decision(source=runner, fallbackUsed=true)
        | none satisfying -> alternative feed install
alternative --installed and accepted--> Decision(source=alternative, fallbackUsed=true)
        | failed or rejected -> Decision(resolved=null, failure=...)  (exit non-zero)
```
