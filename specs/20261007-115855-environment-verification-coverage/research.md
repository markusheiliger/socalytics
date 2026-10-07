# Research: Environment Verification Coverage

## R1: How contract files are verified

- **Decision**: Add `contracts/` to the existing platform scope instead of a
  separate contract check.
- **Rationale**: Recording Lineage and Upload creates the contract test project
  `src/platform/Tests/SocAlytics.Platform.Contracts.Tests` inside the platform
  solution, so the platform test run already validates every schema. One
  shared scope also guarantees the platform check runs once when both areas
  change.
- **Alternatives considered**: A dedicated `dotnet test` of the contract
  project only (faster, but duplicates the platform restore and build and can
  run twice); a Node-based JSON Schema validator (adds a tool the architecture
  does not choose).

## R2: When contract coverage is active

- **Decision**: `contracts/` counts as covered only while
  `src/platform/SocAlytics.Platform.slnx` exists.
- **Rationale**: Matches the existing rule that a scope is covered only when
  the check that verifies it can run; a deleted solution must surface contract
  changes as uncovered.
- **Alternatives considered**: Covering `contracts/` unconditionally (would
  silently pass unverified contracts).

## R3: Analyst Manager check shape

- **Decision**: A separate check named `Analyst Manager build and tests` that
  runs restore, build, and test on
  `src/analysts/manager/SocAlytics.Analysts.Manager.slnx`, placed between the
  platform and Markdown checks and skipped while the solution is missing.
- **Rationale**: A separate name makes failures attributable (FR-004);
  skipping while missing lets this feature merge before the Manager exists, as
  the environment-feature rule requires.
- **Alternatives considered**: Folding the Manager into the platform check
  (failure attribution lost); building the Manager from a solution filter in
  the platform solution (crosses source-area ownership).

## R4: SDK provisioning for the Manager

- **Decision**: No new install. The Manager's `global.json` pins the same SDK
  as `src/platform/global.json`; the verify block fails fast with a clear
  message when the two pins differ.
- **Rationale**: Keeps setup unchanged (FR-006) while preventing silent drift.
- **Alternatives considered**: A second `actions/setup-dotnet` step reading the
  Manager's `global.json` (more setup time, and a missing file would break
  setup before the Manager exists).

## R5: Windows-only Manager tests

- **Decision**: Nothing in the environment; the Manager's tests skip
  Windows-only protected-store tests on Linux, as its plan defines.
- **Rationale**: The runner cannot provide Windows CNG or DPAPI; emulation
  would test nothing real.
- **Alternatives considered**: A Windows runner job (out of scope for the
  verify contract, which runs in one Linux job).

## R6: Documentation

- **Decision**: Describe coverage in the action files; the README is updated
  by the dependent features when coverage becomes active.
- **Rationale**: Environment features may change only the two action folders;
  the README describes active coverage only.
- **Alternatives considered**: Updating the README in this feature (fails the
  exclusivity check).
