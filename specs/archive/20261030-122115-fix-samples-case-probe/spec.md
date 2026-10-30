# Feature Specification: Fix the Case-Sensitivity Probe of the Sample Tooling Test

**Feature Branch**: `20261030-122115-fix-samples-case-probe`

**Created**: 2026-10-30

**Status**: Draft

**Input**: User description: "Fix the case-sensitivity probe in `tools/samples.test.mjs` (the sample tooling owned by the samples feature under `tools/`). On case-sensitive file systems such as Linux, the test 'refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links' fails: its probe runs the sample discovery on a folder holding both `probe/a.mp4` and `probe/A.mp4`, and the discovery throws 'Paths differ only in case' instead of returning, so `node --test tools/samples.test.mjs` fails on the default branch (12 pass, 1 fails). Probe the file system directly instead (create `probe/a.mp4` only and check whether `probe/A.mp4` exists), so the case-collision assertion runs only on case-sensitive file systems. Change only `tools/samples.test.mjs`, do not weaken or remove any assertion, change no production code or environment action. Earlier spec `20261028-235035-case-insensitive-samples-test` (#398) intended this, but the throwing probe remains on the default branch. The stopped spec `20261030-102557-environment-sample-tooling-tests` (twin #418, pull request #419) needs this: its baseline task runs `node --test tools/samples.test.mjs`, and as an environment spec it may not edit that file, so its new sample tooling checks would fail otherwise. That spec therefore depends on this spec."

## Problem

The test decides whether its file system is case-insensitive by running the sample discovery on a folder with `probe/a.mp4` and `probe/A.mp4` and counting the videos. On a case-sensitive file system (Linux) both files exist, so the discovery refuses the folder with "Paths differ only in case". That refusal is the behavior under test, but here it escapes from the probe and fails the test instead of selecting the branch. The earlier spec `20261028-235035-case-insensitive-samples-test` (twin #398) set out to remove this dependency on the code under test, yet the throwing probe is still on the default branch, so `node --test tools/samples.test.mjs` still fails there (12 pass, 1 fails).

The stopped spec `20261030-102557-environment-sample-tooling-tests` (twin #418, pull request #419) runs that command as its baseline and, being an environment spec, may not change `tools/samples.test.mjs`. It cannot proceed until the default branch passes the command.

The probe must ask the file system directly, without calling the sample discovery: create only `probe/a.mp4` and check whether `probe/A.mp4` is visible. Where it is, the file system ignores case, so the case clash cannot be created and must not be demanded; where it is not, the clash can be created and the assertion runs.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The sample tooling test passes on Linux (Priority: P1)

A maintainer or the merge verification runs `node --test tools/samples.test.mjs` on a case-sensitive file system. Every test passes, including the case-clash test, and the case-clash assertion is executed rather than skipped.

**Why this priority**: The failing test on the default branch blocks the stopped implementation #419, whose baseline needs a passing run.

**Independent Test**: Run `node --test tools/samples.test.mjs` on Linux; all tests pass and the case-clash assertion runs.

**Acceptance Scenarios**:

1. **Given** a case-sensitive file system, **When** the test runs, **Then** the probe creates only `probe/a.mp4`, finds no `probe/A.mp4`, does not call the sample discovery, and the test goes on to assert that the discovery refuses `x/a.mp4` next to `X/A.mp4` with "differ only in case".
2. **Given** a case-sensitive file system, **When** the whole file runs, **Then** every other check of the test (missing folder, no videos, empty video, links not followed) still runs and passes, and the file reports no failure.

---

### User Story 2 - The test stays correct on case-insensitive file systems (Priority: P2)

A developer runs the same test on macOS or Windows, whose default file systems treat names that differ only in case as the same name.

**Why this priority**: The probe exists so that the test runs on every developer platform; the fix must not trade Linux for macOS or Windows.

**Independent Test**: Run the command on macOS or Windows; the test passes and does not demand a refusal that the file system makes impossible to provoke.

**Acceptance Scenarios**:

1. **Given** a file system on which `probe/A.mp4` is visible after `probe/a.mp4` was created, **When** the test runs, **Then** the case-clash assertion is not run, and the rest of the test passes.

---

### User Story 3 - The refusal of case-only clashes stays enforced (Priority: P2)

A reviewer needs to be sure the fix weakens neither the test nor the protection against case-only path clashes.

**Why this priority**: Two sample paths that differ only in case would silently overwrite each other on case-insensitive systems; the refusal is what the assertion guards.

**Independent Test**: Review the change: only `tools/samples.test.mjs` differs outside this spec folder, no assertion is removed or loosened, and disabling the refusal in the discovery on Linux makes the case-clash test fail.

**Acceptance Scenarios**:

1. **Given** the change, **When** the discovery's refusal of case-only clashes is disabled on a case-sensitive file system, **Then** the test fails at the case-clash assertion.
2. **Given** the change, **When** the diff is reviewed, **Then** it touches no file other than `tools/samples.test.mjs` and this spec folder.

### Edge Cases

- The probe cannot create its file (permissions, a full disk): the error surfaces as a test error with its real cause and is not read as a detection result.
- The probe folder lies under the test's temporary folder, in the same location as the clash folder, so both see the same case behavior and nothing is left in the repository.
- A file system that is case-insensitive only in some folders: the probe runs in the same temporary location in which the clash folder is created.
- The sample discovery's refusal must not decide the branch; the probe must not call it.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The test MUST decide whether names that differ only in case coexist by creating one probe file and checking directly whether the same name in another case exists, without calling the sample discovery.
- **FR-002**: Where the other-case name does not exist (case-sensitive file systems), the test MUST run the case-clash assertion that the discovery refuses `x/a.mp4` next to `X/A.mp4` with an error matching "differ only in case", and that assertion MUST remain textually unchanged.
- **FR-003**: Where the other-case name exists (case-insensitive file systems), the test MUST NOT demand the refusal and MUST pass.
- **FR-004**: Every other check of the test (missing folder, no videos, empty video, links never followed, the link skip) MUST keep running and keep its behavior.
- **FR-005**: The change MUST be limited to `tools/samples.test.mjs`: no assertion is removed, loosened, or skipped on a case-sensitive file system, and `tools/samples-lib.mjs`, other tests, other tooling, product code, and the environment actions MUST NOT change.
- **FR-006**: An error of the probe unrelated to case handling MUST fail the test visibly.

### Key Entities

- **Case probe**: One file created in a temporary folder, whose other-case name's visibility tells whether the file system distinguishes case.
- **Case-clash assertion**: The check that the sample discovery refuses a folder holding two paths that differ only in case.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: `node --test tools/samples.test.mjs` passes with zero failures on Linux (13 of 13 tests), and the case-clash assertion is executed in that run.
- **SC-002**: On macOS and Windows the same command passes with zero failures (verified by running it there; Linux exercises only the case-sensitive branch).
- **SC-003**: Disabling the discovery's case-only refusal for a run on Linux makes exactly the case-clash test fail.
- **SC-004**: The change set contains one changed file outside this spec folder, `tools/samples.test.mjs`.
- **SC-005**: The baseline run of `20261030-102557-environment-sample-tooling-tests` (twin #418, pull request #419) no longer fails on this test (post-merge evidence, observed when that implementation continues, not verified by a task of this spec).

## Assumptions

- **Cause**: The probe and the test were introduced with the sample-video tooling feature; the test is owned by that feature and the discovery's refusal is correct.
- **Earlier attempt**: Spec `20261028-235035-case-insensitive-samples-test` (#398) intended the same outcome, but its throwing probe remains on the default branch; this spec supersedes its probe and does not depend on it.
- **Dependencies**: The spec `20261030-102557-environment-sample-tooling-tests` (twin #418, pull request #419) depends on this spec; the dependency is recorded between the spec twins on GitHub, and this spec depends on no other spec.
- **Approach**: The direct filesystem probe described in the input is the chosen approach; the exact code is decided at planning.
- **Out of scope**: Changes to `tools/samples-lib.mjs`, other tests, other tooling, product code, `.github/` actions, and `docs/architecture/`.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
