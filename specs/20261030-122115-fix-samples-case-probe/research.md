# Research: Fix the Case-Sensitivity Probe of the Sample Tooling Test

No `NEEDS CLARIFICATION` remains in the Technical Context.

## Decision 1: Probe the file system directly

- **Decision**: Create only `probe/a.mp4` with the test's `folder` helper and treat the file system as case-insensitive when `existsSync(path.join(probeFolder, 'probe', 'A.mp4'))` is true. Run the case-clash assertion only when it is false.
- **Rationale**: `discoverVideos` refuses a case-only clash by design; using it as the probe makes the behavior under test decide the branch and throws on case-sensitive systems. A direct check asks the file system the one question that matters and never calls the code under test (FR-001).
- **Alternatives considered**:
  - Catch the refusal around the probe: couples the probe to the message and still runs the code under test.
  - Detect by operating system (`process.platform`): wrong for case-sensitive volumes on macOS or Windows and case-insensitive ones on Linux.
  - Compare `readdirSync` results after creating both files: needs both files, which the case-insensitive file system collapses; the single-file check is simpler.

## Decision 2: Probe in the same temporary location as the clash folder

- **Decision**: The probe uses the existing `folder` helper, which creates folders under the test's `mkdtempSync` directory, the same location where the clash folder is created.
- **Rationale**: Case behavior can differ per folder or volume; the same parent guarantees the probe predicts the clash folder (spec edge case). Nothing is left in the repository, and the `after` hook removes it.
- **Alternatives considered**: A probe under `os.tmpdir()` directly or in the repository: may sit on a different volume and leaves files behind.

## Decision 3: Probe errors fail visibly

- **Decision**: No `try`/`catch` around the probe. A failure to create `probe/a.mp4` throws inside the test and fails it with the real cause (FR-006).
- **Rationale**: Swallowing errors would read a permission or disk error as a detection result.
- **Alternatives considered**: Treating any probe error as "case-insensitive": hides real faults and silently skips the assertion.

## Decision 4: Keep the assertion and test title unchanged

- **Decision**: Keep `assert.throws(() => discoverVideos(clash), /differ only in case/)` and the clash folder creation as they are; change only the probe condition.
- **Rationale**: FR-002 and FR-005 forbid weakening or rewording; a minimal diff makes the review in User Story 3 trivial.
- **Alternatives considered**: Splitting the case-clash check into its own test: changes the test count (SC-001 expects 13) and is beyond the requested change.
