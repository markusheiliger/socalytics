# Feature Specification: Environment Verification Coverage

**Feature Branch**: `20261007-115855-environment-verification-coverage`

**Created**: 2026-10-07

**Status**: Draft

**Input**: User description: "Environment feature that extends the repository's automated build-and-verify environment so that changes to the planned repository-root machine-readable contracts folder and to the planned Analyst Manager solution under the analysts source area are verified like platform changes, before Recording Lineage and Upload, Durable Analysis Workflow, and Analyst Manager Registration start. It changes only the environment setup and verify extension points."

## Clarifications

### Session 2026-10-07

- Q: Which extra tooling does the Analyst Manager's automated verification need on the Linux runner? → A: A software key-store token (SoftHSM2) so the Manager's Linux key-provider tests run against a real PKCS#11 token; its desktop UI tests run headless and need nothing extra. Windows and macOS key-store tests are not run by this environment.
- Q: Must a change to the Analyst Manager's shared reference examples under the contracts folder also run the Manager's tests? → A: Yes; changes under `contracts/analyst-manager/` trigger the Manager check as well, while those files stay covered through the platform checks.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Contract changes are verified automatically (Priority: P1)

A contributor (human or automated implementation agent) changes a machine-readable contract in the repository-root contracts folder, for example an event or job schema. The automated verification runs the platform checks that validate contracts, reports the result, and does not hold the change for manual review merely because no check covers contract files.

**Why this priority**: Recording Lineage and Upload creates the contracts folder first, and Durable Analysis Workflow extends it. Without coverage, every contract change would be held for review as unverified, and contract validation would not run automatically.

**Independent Test**: With this feature merged and before any contract exists, run the verification in finalize mode and confirm it still succeeds; then, on a branch that adds a sample contract file and a matching platform contract test, run it in task mode and confirm the platform checks run and the contract file is not reported as uncovered.

**Acceptance Scenarios**:

1. **Given** the platform solution exists, **When** a change touches only files in the contracts folder, **Then** the platform build and test checks run and none of the changed contract files is reported as uncovered.
2. **Given** a contract change that breaks a platform contract test, **When** verification runs, **Then** it fails and names the failing check.
3. **Given** the platform solution does not exist, **When** a change touches non-Markdown files in the contracts folder, **Then** those files are reported as uncovered instead of silently passing (Markdown files stay covered by the existing Markdown check).

---

### User Story 2 - Analyst Manager changes are verified automatically (Priority: P2)

A contributor changes the Analyst Manager, which lives in its own solution in the analysts source area. The automated verification restores, builds, and tests that solution and fails when any of these steps fails.

**Why this priority**: Analyst Manager Registration is the first feature with executable code outside the platform area. Its tasks cannot be verified or merged automatically until the environment covers its solution.

**Independent Test**: Before the Analyst Manager solution exists, run verification in finalize mode and confirm it skips the Manager check and still succeeds; then, on a branch with a minimal Manager solution and one passing and one failing test, confirm the check runs and reports the failure.

**Acceptance Scenarios**:

1. **Given** the Analyst Manager solution does not exist yet, **When** verification runs in any mode, **Then** the Manager check is skipped and does not fail verification.
2. **Given** the Analyst Manager solution exists, **When** a change touches files of the Manager area, **Then** the solution is restored, built, and tested, and those files are not reported as uncovered.
3. **Given** the Analyst Manager solution exists, **When** verification runs in finalize mode, **Then** the Manager check runs even if no Manager file changed.
4. **Given** a Manager change with a failing test, **When** verification runs, **Then** it fails and names the Manager check as the failing check.

---

### User Story 3 - The environment provides what the Manager build needs (Priority: P3)

The setup step installs everything the Analyst Manager build and tests need on the automation runner, using the same pinned software development kit version as the platform, so that the Manager build never depends on whatever happens to be preinstalled.

**Why this priority**: The Manager reuses the platform's toolchain; recording that invariant prevents silent drift but adds no new tool.

**Independent Test**: Inspect the setup step output on a run where the Manager solution exists and confirm the installed development kit version equals the version pinned for the platform.

**Acceptance Scenarios**:

1. **Given** the setup step runs, **When** the Manager solution later builds, **Then** it uses the same pinned development kit version as the platform.
2. **Given** the setup step runs, **When** the Manager's key-provider tests run, **Then** a software PKCS#11 token is available to them without any test-time installation.

### Edge Cases

- A change touches both platform files and contract files: the platform checks run once, not twice.
- A change touches the Manager area while the Manager solution was deleted: the Manager files are reported as uncovered rather than silently passing.
- Operating-system-specific Manager tests that need protected key stores unavailable on the automation runner are skipped by the Manager's own test design; the environment does not try to emulate those stores.
- A change touches a path in the analysts source area outside the Manager solution: it stays uncovered and is held for review.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The verification MUST treat changes under the repository-root contracts folder as covered only while the platform solution exists, and MUST then run the platform restore, build, and test checks when such a change is present or in finalize mode.
- **FR-002**: The verification MUST NOT run the platform checks twice when a change touches both platform and contract files.
- **FR-003**: The verification MUST treat changes under the Analyst Manager area as covered only while the Manager solution exists.
- **FR-004**: When the Manager solution exists, the verification MUST restore, build, and test it whenever a Manager file or a Manager golden-fixture contract file under the contracts folder's Analyst Manager area (`contracts/analyst-manager/`) changed, or in finalize mode, and MUST fail when any of these steps fails, naming the Manager check. Such contract files remain covered only through the platform checks (FR-001).
- **FR-005**: While the Manager solution does not exist, the verification MUST skip the Manager check without failing.
- **FR-006**: The setup MUST provide the development kit version pinned for the platform to the Manager build, MUST install a software PKCS#11 token (SoftHSM2) that the Manager's key-provider tests use, and MUST NOT install other tools unless the Manager build requires them.
- **FR-007**: The feature MUST change only the environment setup and environment verify extension points (the only paths an environment feature may change); it MUST NOT add product code, tests, contracts, documentation outside those extension points, or the Manager solution.
- **FR-008**: The order of checks, their names in the verification output, and the uncovered-file reporting MUST keep their existing behavior for platform and Markdown changes.
- **FR-009**: The descriptions inside the two extension points MUST describe the new contract and Manager coverage, including that each becomes active only once its project exists.

### Key Entities

- **Verification scope**: A path pattern that a check covers, active only while the project it verifies exists.
- **Check**: A named verification step (platform build and tests, Analyst Manager build and tests, Markdown check) with a pass or fail outcome.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After this feature merges, 0 contract-only or Manager-only changes are held for review solely because no check covers them, once their owning project exists.
- **SC-002**: In 100% of verification runs before the contracts folder or the Manager solution exists, verification succeeds exactly as it did before this feature.
- **SC-003**: In 100% of runs where a contract or Manager test fails, verification fails and names the failing check.
- **SC-004**: 0 product, contract, or Manager source files are added or changed by this feature.

## Assumptions

- **Dependents**: Recording Lineage and Upload (`specs/20261005-130702-recording-lineage-upload`), Durable Analysis Workflow (`specs/20261005-130703-durable-analysis-workflow`), and Analyst Manager Registration (`specs/20261005-130704-analyst-manager-registration`) depend on this feature, which is reviewed and merged before they start, as the constitution's environment-feature rule requires.
- **Contracts validation lives in the platform**: the platform contract test project created by Recording Lineage and Upload validates the contracts folder, so contract coverage reuses the platform checks instead of adding a separate tool.
- **Manager location and toolchain**: the Analyst Manager is a separate solution in the analysts source area at the path its plan defines, pinned to the same development kit version as the platform.
- **Runner capabilities**: the automation runner is Linux; the Manager's PKCS#11 key provider is tested against the software token and its UI view models headless, while the Windows and macOS key-store providers are verified manually on their operating systems, as the Manager plan states.
- **Self-test**: the merge workflow self-tests a branch's own environment actions, so this feature is verified by its own merge run; environment changes are held for human review by design.
- **Repository documentation**: the repository README describes only active coverage. Recording Lineage and Upload updates its description of the verification when it creates the contracts folder, and Analyst Manager Registration does so when it creates the Manager solution.
- **Architecture References**: [docs/architecture/contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md), [docs/architecture/platform-implementation.md](../../docs/architecture/platform-implementation.md), [docs/architecture/analyst-manager.md](../../docs/architecture/analyst-manager.md).
