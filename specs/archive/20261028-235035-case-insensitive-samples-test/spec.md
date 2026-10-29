# Feature Specification: Case-Sensitivity-Independent Sample Tooling Test

**Feature Branch**: `20261028-235035-case-insensitive-samples-test`

**Created**: 2026-10-28

**Status**: Draft

**Input**: User description: "Make the repository tooling test in `tools/samples.test.mjs` ('refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links') pass on case-sensitive, case-insensitive, and case-preserving file systems (Linux CI, macOS, Windows), changing only the test, without weakening the refusal of case-only path clashes in `tools/samples-lib.mjs`, and without changing any other test. Guidance of the person who asked (@markusheiliger): make the probe wrap the call in try/catch (or detect case-insensitivity directly) so the test passes on Linux, macOS, and Windows, and keep the assertion that the discovery refuses case-only clashes."

## Problem

The test decides whether the file system it runs on is case-insensitive by running the sample discovery on a folder holding `probe/a.mp4` and `probe/A.mp4` and counting the videos. On a case-sensitive file system (Linux CI) both files exist, so the discovery itself refuses the folder with "Paths differ only in case". That refusal is the behavior under test, but here it escapes from the probe and fails the test instead of selecting the branch. The test therefore fails on the default branch on Linux. The merge verification of the stopped implementation of `specs/20261025-003459-deterministic-agent-workload-test` (twin #308, pull request #319) runs the repository tooling tests, so that implementation cannot merge although it changes nothing under `tools/`. The failure came in with the sample-video tooling commit `5a03454d`.

The test must tell, without relying on a refusal from the code under test, whether the file system keeps `a.mp4` and `A.mp4` apart, and then exercise the case-clash assertion wherever it can be exercised.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The tooling tests pass on Linux CI (Priority: P1)

A maintainer or the merge verification runs the repository tooling tests on a case-sensitive file system. The test passes, so an unrelated implementation is no longer blocked by it.

**Why this priority**: The failure currently blocks the merge of an implementation (#319) that does not touch `tools/`.

**Independent Test**: Run `node --test tools/samples.test.mjs` on Linux; every test passes, including the case-clash test, and the case-clash assertion is executed rather than skipped.

**Acceptance Scenarios**:

1. **Given** a case-sensitive file system, **When** the test runs, **Then** the probe does not fail the test, the case-clash assertion runs, and it passes because the discovery refuses `x/a.mp4` next to `X/A.mp4` with "differ only in case".
2. **Given** a case-sensitive file system, **When** the whole file is run, **Then** every other check of the test (missing folder, no videos, empty video, links not followed) still runs and passes.

---

### User Story 2 - The test stays correct on case-insensitive and case-preserving file systems (Priority: P1)

A developer runs the same test on macOS or Windows, whose default file systems preserve case but treat names that differ only in case as the same name.

**Why this priority**: The test exists to run on every developer platform; a fix for Linux that breaks macOS or Windows is no fix.

**Independent Test**: Run `node --test tools/samples.test.mjs` on macOS or Windows; the test passes and does not demand a refusal that the file system makes impossible to provoke.

**Acceptance Scenarios**:

1. **Given** a case-insensitive or case-preserving file system on which `a.mp4` and `A.mp4` cannot coexist, **When** the test runs, **Then** the case-clash assertion is not demanded (the clash cannot be created), the probe does not fail, and the rest of the test passes.

---

### User Story 3 - The refusal of case-only clashes stays enforced (Priority: P2)

A reviewer needs to be sure the fix does not weaken the protection against case-only path clashes in the sample discovery.

**Why this priority**: Two sample paths that differ only in case would silently overwrite each other on case-insensitive systems; the refusal is the protection that the test guards.

**Independent Test**: Review the change: only `tools/samples.test.mjs` differs, and its case-clash assertion is textually intact; temporarily disabling the refusal in the discovery makes the test fail on Linux.

**Acceptance Scenarios**:

1. **Given** the change, **When** the discovery's refusal of case-only clashes is disabled on a case-sensitive file system, **Then** the test fails at the case-clash assertion.
2. **Given** the change, **When** the diff is reviewed, **Then** it touches no file other than `tools/samples.test.mjs` and this spec folder, and no other test changes.

### Edge Cases

- The probe cannot create one of its two files for another reason (permissions, a full disk): this surfaces as a test error with its real cause instead of being read as "case-insensitive".
- The probe's folder is created under the test's temporary folder, so repeated runs do not interfere and nothing is left in the repository.
- A file system that is case-insensitive in only some folders (for example a per-directory setting): the probe runs in the same temporary location in which the clash folder is created, so both see the same behavior.
- The sample discovery refuses the probe folder on a case-sensitive file system: this refusal must not be what decides the branch; the probe must not depend on whether the discovery throws.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The test MUST determine whether names that differ only in case coexist on the file system it runs on, without the outcome depending on the sample discovery throwing or not throwing, and without letting that refusal fail the test.
- **FR-002**: Where such names coexist (case-sensitive file systems), the test MUST run the case-clash assertion that the sample discovery refuses `x/a.mp4` next to `X/A.mp4` with an error matching "differ only in case", and that assertion MUST remain textually unchanged.
- **FR-003**: Where such names cannot coexist (case-insensitive and case-preserving file systems), the test MUST NOT demand the refusal and MUST pass.
- **FR-004**: The test MUST pass on Linux, macOS, and Windows, and every other check of the test (missing folder, no videos, empty video, links never followed, the symbolic-link skip) MUST keep running and keep its behavior.
- **FR-005**: The change MUST be limited to `tools/samples.test.mjs`: the sample discovery in `tools/samples-lib.mjs` MUST NOT be changed or weakened, and no other test MUST be changed, removed, loosened, or skipped.
- **FR-006**: An error of the probe unrelated to case handling MUST fail the test visibly instead of being treated as a detection result.

### Key Entities

- **Case probe**: A pair of file names that differ only in case, created in a temporary folder, whose coexistence tells whether the file system distinguishes case.
- **Case-clash assertion**: The check that the sample discovery refuses a folder holding two paths that differ only in case.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: `node --test tools/samples.test.mjs` passes with zero failures on Linux, and the case-clash assertion is executed in that run.
- **SC-002**: On macOS and Windows the same command passes with zero failures.
- **SC-003**: Disabling the discovery's case-only refusal for a run on Linux makes exactly the case-clash test fail.
- **SC-004**: The merge verification of #319 no longer reports this test as failing, so that implementation is no longer blocked by it (post-merge evidence, observed when #319 continues, not verified by a task of this spec).
- **SC-005**: The change set contains one changed file outside this spec folder, `tools/samples.test.mjs`.

## Assumptions

- **Cause**: The failing test and its probe were introduced with the sample-video tooling feature (commit `5a03454d`); the test is owned by that feature and the discovery's refusal is correct.
- **Dependencies**: The stopped spec `specs/20261025-003459-deterministic-agent-workload-test` (twin #308, pull request #319) depends on this spec.
- **Approach**: Either wrapping the probe call in a try/catch or checking directly whether both probe files can coexist is acceptable; the choice is made at planning, and the direct check is the preferred one because it does not depend on the code under test.
- **Out of scope**: Changes to `tools/samples-lib.mjs`, other tests, other tooling, product code, and `docs/architecture/`; real case-insensitive file systems are verified by running the test on them, since Linux CI exercises only the case-sensitive branch.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
