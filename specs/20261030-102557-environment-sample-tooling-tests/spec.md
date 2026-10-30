# Feature Specification: Sample Tooling Tests in Environment Verification

**Feature Branch**: `20261030-102557-environment-sample-tooling-tests`

**Created**: 2026-10-30

**Status**: Draft

**Input**: User description: "Environment spec: change only `.github/actions/environment-setup` and `.github/actions/environment-verify` (plus the spec's own `tasks.md` ticks). Add real verification coverage for the repository sample tooling under `tools/`: environment-verify runs `node --test tools/samples.test.mjs` when any of exactly `tools/samples.test.mjs`, `tools/samples-lib.mjs`, `tools/upload-samples.mjs`, or `tools/download-samples.mjs` changes, and always in finalize mode; it covers exactly those four files (no broader `tools/` glob), propagates test failures, fails visibly when `tools/samples.test.mjs` is missing, and works with the existing planning, scope, and sharding model. Regression tests in `verification-plan.test.mjs` and `verification-scope.test.mjs`. No path whitelisting without a test, no change to the required-sync barrier or its uncovered-path filter in `.github/scripts/`, no change to pending repair scope, write-job isolation preserved. The stopped spec `specs/20261028-235035-case-insensitive-samples-test` (twin #401, pull request #404) changes only `tools/samples.test.mjs` and its own spec folder; its required sync stopped twice with 'required synchronization has files not covered by verification'. That spec depends on this one."

## User Scenarios & Testing *(mandatory)*

The users are contributors and the implementation automation. Today no check selects or runs the tests of the sample tooling under `tools/`. A change to one of its four files is reported as "files not covered by verification", which holds implementation pull requests for review and makes the required `/speckit sync` barrier fail.

### User Story 1 - Run the sample tooling tests as a verification check (Priority: P1)

A contributor or implementation agent changes a sample tooling file or its test. Verification runs the sample tooling's own tests with the Node.js the caller provides and fails, with the command to reproduce and one recommended action, when a test fails or the test file is missing.

**Why this priority**: Without the check, a broken sample tooling change passes verification and no coverage claim for those files can be honest.

**Independent Test**: Run verification against a fixture workspace with a changed sample tooling file, once with passing tests, once with failing tests, and once without `tools/samples.test.mjs`, and compare the reported checks, the outcome, and the failure message.

**Acceptance Scenarios**:

1. **Given** a changed `tools/samples.test.mjs`, `tools/samples-lib.mjs`, `tools/upload-samples.mjs`, or `tools/download-samples.mjs` (a deletion included), **When** verification runs in task or phase mode, **Then** a named check "Sample tooling tests" runs `node --test tools/samples.test.mjs` and appears in the checks the result reports.
2. **Given** a failing sample tooling test, **When** the check runs, **Then** verification fails and its message names the failed check, the exact command to reproduce, and one recommended action.
3. **Given** `tools/samples.test.mjs` does not exist and the check is selected, **When** the check runs, **Then** verification fails visibly with a named failed check instead of skipping or passing.
4. **Given** verification runs in finalize mode, **When** no sample tooling file changed, **Then** the check still runs.
5. **Given** a change that touches none of the four files and a mode other than finalize, **When** verification runs, **Then** the check is skipped and does not appear in the reported checks.
6. **Given** the check runs inside a test that is itself run by verification, **When** the nested-run guard of the environment tests is set, **Then** the check does not start again.

---

### User Story 2 - Count exactly the four sample tooling files as covered (Priority: P1)

A maintainer or the implementation automation reads the verification result for a branch. The four sample tooling files are covered by the new test check, so a change that only touches them is no longer reported as uncovered, and no other `tools/` path is.

**Why this priority**: The coverage decides whether an implementation merges without review and whether the required sync barrier passes; it is what unblocks twin #401.

**Independent Test**: Run the scope and plan rules for changes in fixtures and assert the covered and uncovered outcomes.

**Acceptance Scenarios**:

1. **Given** a change to only `tools/samples.test.mjs` plus its own spec folder, **When** verification plans, **Then** no file is reported uncovered and the check "Sample tooling tests" is the covering check.
2. **Given** a change to any of the other three files, **When** verification plans, **Then** each is covered by the same check.
3. **Given** a changed `tools/` path that is not one of the four files (for example `tools/other.mjs`, `tools/samples.test.mjs.bak`, or a nested path), **When** verification plans, **Then** it stays uncovered and holds the pull request for review as before.
4. **Given** a changed path that is protected for implementation tasks, **When** verification plans, **Then** it stays protected; coverage never lifts protection.

---

### User Story 3 - Fit the planning, scope, and sharding model (Priority: P2)

A maintainer of the environment actions reads how verification is planned. The check is a shared check of the first shard, not a component, whose budget is added to the first shard's shared-check budget (`MAIN_MINUTES`), so sharded verification neither drops nor duplicates it.

**Why this priority**: A check outside the sharding model would be skipped or run twice in sharded runs.

**Independent Test**: Plan a sharded verification in a fixture and assert the check lands in exactly one shard with a budget, and a failing check fails the aggregate.

**Acceptance Scenarios**:

1. **Given** a sharded plan that selects the check, **When** shards are assigned, **Then** the check is in exactly one shard with its budget.
2. **Given** a failing check in any shard, **When** the verification result is aggregated, **Then** the overall verification fails.

---

### User Story 4 - Keep the new coverage proven by the action's own tests (Priority: P2)

The action's own tests fail when the check, its triggers, its coverage, or its sharding change by accident.

**Why this priority**: The coverage rules decide what merges without review, so they must not drift silently.

**Independent Test**: Run the Node tests of the environment actions; the new cases exist and assert each rule (check, triggers, coverage, sharding). That they fail when a rule is removed is manual evidence (quickstart section 5), not a verified outcome.

**Acceptance Scenarios**:

1. **Given** the action's own tests, **When** they run, **Then** `verification-plan.test.mjs` and `verification-scope.test.mjs` cover selection (only the four paths select it, finalize always does), coverage (the four paths covered, other `tools/` paths uncovered), the scope reason, and the sharding budget, and `analyst-projects.test.mjs` (the existing home of the shell fixtures of the Spec Kit tooling tests) covers the shell-level execution (the exact command), failure propagation, a missing test file failing visibly, the nested-run guard, and the finalize cases.
2. **Given** the action's README, **When** a maintainer reads it, **Then** it documents the check, its trigger, its command, its failure message, and the covered scope.

---

### Edge Cases

- The caller provides no Node.js: the check fails naming the missing tool, like the other Node-based checks.
- `tools/samples.test.mjs` is deleted while another of the four files changes: the check is still selected and fails visibly for the missing test file.
- Only the four files change together with the spec's own folder: nothing is uncovered.
- A change touches the four files and an uncovered `tools/` path: only the other path is reported uncovered.
- A path differing only in case or by a suffix or directory from the four files is not covered.
- Markdown under `tools/` stays covered by the Markdown check as before.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Verification MUST provide a check named "Sample tooling tests" that runs exactly `node --test tools/samples.test.mjs` with the Node.js the caller provides, and installs or downloads nothing.
- **FR-002**: The check MUST be selected in task and phase mode when any of exactly `tools/samples.test.mjs`, `tools/samples-lib.mjs`, `tools/upload-samples.mjs`, or `tools/download-samples.mjs` changed (a deletion included), MUST always be selected in finalize mode, and MUST NOT be selected otherwise.
- **FR-003**: The check MUST fail verification when a test fails, with a message that names the check, the command to reproduce, and one recommended action.
- **FR-004**: The check MUST fail visibly, as a named failed check and never as a silent skip, when `tools/samples.test.mjs` is missing or Node.js is unavailable.
- **FR-005**: The check MUST cover exactly the four files and no broader `tools/` glob; coverage MUST come from the check that runs the test, and no path may be reported covered without a check that runs a test for it.
- **FR-006**: Every other `tools/` path MUST stay uncovered and keep holding the pull request for review; protected paths MUST stay protected.
- **FR-007**: The check MUST be part of the existing planning, scope, and sharding model: a shared check of the first shard, not a component, whose budget is added to the first shard's shared-check budget (`MAIN_MINUTES`), assigned to exactly one shard in sharded runs; failure in any shard MUST fail the verification.
- **FR-008**: The check MUST honor the nested-run guard of the existing environment tests and appear in the reported checks whenever it ran.
- **FR-009**: The change MUST NOT alter the required-sync barrier or its uncovered-path filter in `.github/scripts/`, pending repair scope, repair attempt limits, or the isolation of write jobs; no agent-written code runs in a job with a write token.
- **FR-010**: The action's own tests MUST cover selection, coverage, the scope reason, and the sharding budget in `verification-plan.test.mjs` and `verification-scope.test.mjs`, and the shell-level execution, failure propagation, missing test file, nested-run guard, and finalize cases in `analyst-projects.test.mjs` (the existing home of the shell fixtures of the Spec Kit tooling tests), as listed in User Story 4.
- **FR-011**: The action's README MUST document the check, its trigger, command, failure message, and covered scope.
- **FR-012**: Every existing check, input, output, mode rule, cache boundary, and result field MUST be preserved.
- **FR-013**: The change MUST be limited to `.github/actions/environment-setup`, `.github/actions/environment-verify`, and the spec's own `tasks.md` ticks.

### Key Entities

- **Sample tooling tests check**: The verification check that runs the sample tooling test and is named in the reported checks.
- **Sample tooling scope**: The covered-path declaration for exactly the four files, tied to the check.
- **Verification result**: The reported statuses, covered and uncovered files, and checks that the sync barrier and the task path inspect.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For 100% of changes to any of the four files, verification runs the sample tooling test and reports them covered by that check.
- **SC-002**: 0 other `tools/` paths become covered, and every previously uncovered or protected path keeps its status.
- **SC-003**: A change of only `tools/samples.test.mjs` and its own spec folder reports no file as not covered by verification, so the required sync of twin #401 is no longer stopped for that reason.
- **SC-004**: A failing or missing sample tooling test is reported with the check name, reproduce command, and one recommended action in 100% of failures.
- **SC-005**: All existing environment-action tests pass, apart from the two assertions that name the check order and the shard log text, which are updated to include the Sample tooling tests, and the new tests contain assertions for the check, its triggers, its scope, and its sharding (that they detect the removal of each rule is manual evidence, not verified by the implementation).

## Assumptions

- The caller (setup or the workflow job) provides the Node.js that the existing Node-based checks use; this change adds no tool installation.
- The sample tooling tests need no network, credentials, or services.
- The merge of this environment spec is held for a person's review, as for every environment spec.
- Dependencies: the stopped spec `specs/20261028-235035-case-insensitive-samples-test` (twin #401, pull request #404) depends on this spec; this spec has no dependency on it.
- Plan: Architecture Updates are expected to be `None`, because the change affects only the environment's verification coverage.
- Out of scope: changing the sample tooling, the workflows, the required-sync barrier, its uncovered-path filter, pending repair scope, or the stopped spec's tasks.
