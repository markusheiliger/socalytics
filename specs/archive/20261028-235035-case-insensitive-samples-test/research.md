# Research: Case-Sensitivity-Independent Sample Tooling Test

## Decision 1: Detect case handling directly, not through the discovery

- **Decision**: Create the probe files `probe/a.mp4` and `probe/A.mp4` with the test's `folder()` helper and read the folder back with `readdirSync`; `length === 2` means the file system keeps case-only differing names apart.
- **Rationale**: The probe no longer calls the code under test, so the discovery's refusal cannot escape from it (FR-001). On a case-insensitive file system the second write replaces the first and one entry remains; on a case-sensitive one two entries remain. Probe errors other than case handling (permissions, full disk) throw from `writeFileSync` or `mkdirSync` and fail the test with their real cause (FR-006). The probe lives under the test's temporary folder and in the same location as the clash folder (spec edge cases).
- **Alternatives considered**: Wrapping the existing `discoverVideos` probe call in try/catch. Rejected: it still depends on the code under test and on its error message, and a catch that swallows every error would hide unrelated probe failures (FR-006). Probing with `existsSync` on the swapped-case name. Rejected: it also reports true on case-insensitive systems after only one file exists, so it needs extra logic to tell the cases apart; counting entries is unambiguous.

## Decision 2: Keep the case-clash assertion textually unchanged and conditional

- **Decision**: Keep `assert.throws(() => discoverVideos(clash), /differ only in case/)` as is, inside the conditional on the probe's entry count (`readdirSync(path.join(probeRoot, 'probe')).length === 2`). Nothing is skipped or loosened elsewhere.
- **Rationale**: FR-002, FR-003 and FR-005: on a case-sensitive file system the assertion runs and guards the refusal; where the clash cannot be created it cannot be demanded.
- **Alternatives considered**: `t.skip` for the case-insensitive branch. Rejected: the test also runs its other checks, and the later symbolic-link branch already uses `t.skip` with an early `return`, so skipping here would stop the link checks from running (FR-004).

## Decision 3: No other changes

- **Decision**: `tools/samples-lib.mjs` and all other tests stay untouched.
- **Rationale**: FR-005 and SC-005.
- **Alternatives considered**: Moving the probe into a shared helper. Rejected as a speculative abstraction (Principle V).

## Verification Approach

- Linux: run `node --test tools/samples.test.mjs`; the case-clash assertion executes (SC-001). For SC-003, copy `tools/` to a temporary folder outside the repository, remove the case-only refusal from the copy of `samples-lib.mjs`, run `node --test samples.test.mjs` there, observe exactly the case-clash test fail, and delete the copy.
- macOS and Windows: not available in CI; the logic is verified by reasoning about the replacement semantics and by running the test on those systems (SC-002).

No NEEDS CLARIFICATION items remain.
