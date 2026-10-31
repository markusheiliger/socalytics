# Data Model: Case-Sensitivity-Independent Sample Tooling Test

The feature has no persisted data and no product entities. The test works with two temporary values.

## Case probe

- **What**: A temporary folder `samples-<n>/probe/` created through the test's `folder()` helper holding the writes `a.mp4` (content `1`) and `A.mp4` (content `2`).
- **Result**: The number of entries `readdirSync` returns for the folder: `2` means names that differ only in case coexist (case-sensitive file system); `1` means they cannot coexist (case-insensitive or case-preserving file system).
- **Validation**: An error while creating the probe is not caught and fails the test (FR-006).
- **Lifetime**: Removed with the test's temporary folder in the existing `after` hook.

## Case clash folder

- **What**: A temporary folder holding `x/a.mp4` and `X/A.mp4`, created by `folder()` as before.
- **Use**: Passed to `discoverVideos`, which must refuse it with an error matching "differ only in case"; asserted only when the probe result is `2`.
