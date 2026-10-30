# Data Model: Fix the Case-Sensitivity Probe of the Sample Tooling Test

The feature persists no data and adds no types. It touches two transient test-local values.

## Case probe

| Aspect | Description |
| ------ | ----------- |
| Location | `probe/a.mp4` below a folder made by the test's `folder` helper under its temporary directory |
| Content | One file with the content `1` (never read) |
| Result | Boolean: whether `probe/A.mp4` is visible (file system ignores case) |
| Lifetime | Removed with the temporary directory by the test's `after` hook |

## Case-clash assertion

| Aspect | Description |
| ------ | ----------- |
| Input | Folder holding `x/a.mp4` and `X/A.mp4` |
| Expectation | `discoverVideos` throws an error matching `/differ only in case/` |
| Condition | Runs only when the case probe finds no `probe/A.mp4` (case-sensitive file system) |

## State

Probe: not created → created → result read. No other transitions.
