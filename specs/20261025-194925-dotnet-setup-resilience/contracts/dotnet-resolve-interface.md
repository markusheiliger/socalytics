# Contract: `.NET` resolution step of `environment-setup`

This is the interface the action keeps and adds. It is a component-local contract of `.github/actions/environment-setup/`; it is not an API contract and adds nothing under the repository-root `contracts/`.

## Unchanged action interface

- Inputs `workspace` and `install-tools`, with unchanged semantics: the resolution step runs for both `install-tools` values.
- All outputs of `action.yml`, including those derived from `steps.cache-keys`.
- The step id `dotnet` and its output `dotnet-version` (the resolved SDK version string such as `10.0.403`), consumed by `Calculate dependency cache keys` as `CACHE_DOTNET_VERSION`.
- No new input, output, secret, token, or permission.

## Steps (in order)

| Step | Condition | Notes |
| --- | --- | --- |
| `Set up .NET (attempt 1)` | always | `actions/setup-dotnet` pinned to a full commit, `global-json-file` of the platform, `continue-on-error`, `timeout-minutes: 3` |
| `Pause before .NET attempt 2` | attempt 1 did not succeed | 10 s |
| `Set up .NET (attempt 2)` | attempt 1 did not succeed | as attempt 1 |
| `Pause before .NET attempt 3` | attempts 1 and 2 did not succeed | 30 s |
| `Set up .NET (attempt 3)` | attempts 1 and 2 did not succeed | as attempt 1 |
| `Resolve the .NET SDK` (`id: dotnet`) | always (no `if`) | runs `node "$GITHUB_ACTION_PATH/dotnet-resolve.mjs" resolve` |

## Helper CLI

`node dotnet-resolve.mjs resolve`

Environment read:

| Variable | Meaning |
| --- | --- |
| `DOTNET_WORKSPACE` | `inputs.workspace` |
| `DOTNET_ATTEMPT_OUTCOMES` | the outcomes of the three attempt steps (comma-separated) |
| `DOTNET_ATTEMPT_VERSIONS` | the `dotnet-version` outputs of the three attempts (comma-separated, empty when none) |
| `RUNNER_TEMP`, `GITHUB_OUTPUT`, `GITHUB_PATH`, `GITHUB_ENV`, `GITHUB_STEP_SUMMARY` | standard runner files |

Effects:

- Cross-check: after a candidate passes `acceptable`, the helper runs `dotnet --version` in each pinned folder (for the alternative source and for a runner SDK under another root with the candidate's install directory first on `PATH` and `DOTNET_ROOT` set to it in the child environment, so the muxer that is cross-checked is the one later steps will use); the reported version must satisfy every requirement through `acceptable` (it may be a higher satisfying patch than the candidate) and is the resolved version; otherwise the candidate is rejected with a cause naming the folder.
- Success: writes `dotnet-version=<resolved version>` to `GITHUB_OUTPUT`; appends the `.NET SDK` table to `GITHUB_STEP_SUMMARY`; for the alternative source also appends the install directory to `GITHUB_PATH` and `DOTNET_ROOT=<dir>` to `GITHUB_ENV`; exit code 0.
- Failure: prints the failure block (every source tried, its cause, and one `Recommended action`) to stderr and appends it to the summary; writes no `dotnet-version`; exit code 1.

The summary table columns: `Source`, `Fallback used`, `Attempts`, `Resolved SDK`; followed by one line per rejected or failed attempt with its cause.

## Exported pure functions (tested offline)

| Function | Contract |
| --- | --- |
| `parseRequirement(file, json)` | Returns the SDK requirement or throws naming `file` |
| `satisfies(version, requirement)` | `true` only if the `global.json` rule for `rollForward` and `allowPrerelease` accepts `version` |
| `acceptable(version, requirements)` | `satisfies` against every requirement; returns the first violated requirement file or `null` |
| `decide(sources, requirements, limits)` | Runs the ordered source adapters (injected, so tests simulate outcomes), enforces the order and the bounded attempts, returns the Decision |
| `formatFailure(decision)` | The single failure block |
| `formatSummary(decision)` | The summary Markdown |

## Failure message contract

One block, ASCII, no credentials:

```text
.NET SDK setup failed: no source yielded an SDK that satisfies src/platform/global.json and src/analysts/manager/global.json.
  primary (setup-dotnet), attempt 1: <cause>
  ...
  runner toolchain: <cause>
  alternative feed (dotnet-install): <cause>
Recommended action: <exactly one action>
```

The recommended action is: when the primary and the alternative both failed to download, restore access to the .NET download origins and rerun the job; when a candidate was rejected by the pinned files, correct the pins through a standalone environment change, never by loosening a `global.json` policy; when the alternative feed failed with an install script digest mismatch, the recovery is to update the committed digest constant through a standalone environment change after reviewing the new script.
