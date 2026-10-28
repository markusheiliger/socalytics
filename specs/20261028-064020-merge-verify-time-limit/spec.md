# Feature Specification: Merge Verification of Environment Specs Within Its Time Limit

**Feature Branch**: `20261028-064020-merge-verify-time-limit`

**Created**: 2026-10-28

**Status**: Draft

**Input**: User description: "Let the merge verification of environment specs finish within its time limit. Where it lives: the workflow `.github/workflows/speckit-implement-merge.yml` (job `verify`, \"Verify the merged result\", timeout-minutes 90), the composite action `.github/actions/environment-verify` (its README and tests), and, if it states the limit, `.github/workflows/README.md`. Cause: in an environment spec the job runs the full finalize verification (about 70 minutes) and then the steps `Self-test the changed environment setup` and `Self-test the changed environment verification` (mode finalize), which repeats the whole finalize verification a second time; the job is cancelled at 90 minutes before `Record the verdict`, so merge-land reports `the merged result was not verified`. Runs 40626069677, 40617220411, 40607738496, and 40597182107 of spec #382 were all cancelled this way, although its implementation and convergence are sound. Required change, without weakening any check: raise the timeout-minutes of the `verify` job so both runs fit with margin (at most 360), and, if possible, make the self-test of the changed environment verification run only the action's own tests and fixtures instead of repeating every component test already run by the preceding `Verify the merged result` step, while the self-test still exercises the changed action version from the workspace and still feeds the verdict (SPECKIT_SELFTEST_OUTCOME). Update the workflows README and the environment-verify README where they state the 90-minute limit or describe the self-test, and keep the Spec Kit tooling tests and the environment action tests passing. Why: the stopped spec `specs/20261027-205629-environment-speckit-tooling-tests` (twin #382, pull #383) cannot merge until the merge verification completes, and implementation agents may not change `.github/workflows/`. That spec depends on this one. Requested by @markusheiliger."

## User Scenarios & Testing *(mandatory)*

The users are the people who request implementations and the merge automation. An implementation that changes the trusted environment actions is verified twice before merging: once as the full verification of the merged result, and once as a self-test of the changed actions. Together they take longer than the time limit of the merge verification job, so the job is cancelled before it records its verdict and the merge reports that the result was not verified, although the implementation is sound.

### User Story 1 - An environment spec's merge verification records its verdict (Priority: P1)

A person requests the implementation of an environment spec. When every task and the convergence are done, the merge verification runs the full verification of the merged result and the self-test of the changed actions, and records its verdict before the job's time limit ends.

**Why this priority**: Until the verdict is recorded, no environment spec can merge. The stopped spec of twin #382 (pull #383) is blocked on exactly this.

**Independent Test**: Review the merge verification job's time limit against the measured durations of the full verification (about 70 minutes) and of the self-test, and run the merge verification of an environment spec to its verdict.

**Acceptance Scenarios**:

1. **Given** an environment spec whose full verification takes about 70 minutes, **When** the merge verification runs the full verification and then the self-test, **Then** the job records its verdict and ends before its time limit, with a margin of at least one third of the time both runs need.
2. **Given** the job's time limit, **When** it is read, **Then** it is larger than the 90 minutes that cancelled four runs of the stopped spec and at most 360 minutes.
3. **Given** a spec that is not an environment spec, **When** the merge verification runs, **Then** it behaves as before: no self-test runs and the verdict comes from the full verification alone.

---

### User Story 2 - The self-test of the changed verification does not repeat the component tests (Priority: P1)

The self-test of the changed environment verification action exercises the changed version of the action from the workspace by running the action's own tests and their fixtures, instead of repeating every component test that the preceding full verification already ran.

**Why this priority**: It removes the repeated hour of work, so the merge of an environment spec is fast and the raised time limit is a margin, not a requirement.

**Independent Test**: Run the self-test step against a fixture workspace with a changed action and compare the checks it reports with those of the full verification.

**Acceptance Scenarios**:

1. **Given** a changed environment verification action in the merged tree, **When** the self-test runs, **Then** it loads the action from the workspace (not from the trusted tooling checkout) and runs the environment action tests and their fixtures, and no platform, Analyst Manager, Web UI, Analyst Python project, or agent Python project check.
2. **Given** the self-test fails (an action test or the changed action itself), **When** the verdict is recorded, **Then** the self-test outcome still feeds the verdict, so the merge is refused with the failed check named.
3. **Given** the changed action is broken in a way that only a repeated real run on the merged tree would show, **When** the self-test runs, **Then** the action's own fixture tests (with stubbed tools) exercise the changed action script for every component kind (verification plan, .NET, Node, Python, Markdown), so a regression in the changed action still fails the self-test; the full verification of the merged result, which runs with the trusted action, is not a substitute for that coverage.
4. **Given** the self-test of the changed environment setup fails, **When** the verification self-test would start, **Then** it is skipped as before and the verdict reports the failure.

---

### User Story 3 - The documentation states the limit and the self-test as they are (Priority: P2)

A maintainer reads the workflows README and the environment-verify README and finds the merge verification's time limit (where stated) and the scope of the self-test described as implemented.

**Why this priority**: The repository documentation must describe the implemented state, and a wrong limit or a self-test described as a repeated full verification misleads the next person who diagnoses a cancelled merge.

**Independent Test**: Search both READMEs for the limit and for the self-test and compare each statement with the workflow and the action.

**Acceptance Scenarios**:

1. **Given** the READMEs, **When** they state a time limit for the merge verification, **Then** it equals the limit of the workflow.
2. **Given** the READMEs, **When** they describe the self-test of the changed environment verification, **Then** they say that it runs only the action's own tests and fixtures from the workspace version and feeds the verdict, and that the full verification of the merged result runs every component test.

---

### Edge Cases

- The full verification itself fails: the self-test still runs and the verdict reports both outcomes, as before.
- The full verification takes longer than measured (a slower runner, a rerun of flaky tests): the raised limit leaves margin; the limit is not raised beyond 360 minutes.
- The self-test's own tests start verification again: the existing nested-run guard keeps the fixtures from recursing.
- The environment verification action does not exist in the workspace: the self-test is skipped, as before.
- The self-test and the full verification both run the environment action tests: the repeat is only the action's own tests, which are small; no component test repeats.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The merge verification job of an environment spec MUST finish and record its verdict when the full verification takes about 70 minutes and the self-test follows; its time limit MUST leave a margin of at least one third over the time both runs need and MUST NOT exceed 360 minutes.
- **FR-002**: The self-test of the changed environment verification MUST load the changed action version from the workspace and MUST run only the environment action tests and their fixtures, never the component checks (platform, Analyst Manager, Web UI, Analyst Python projects, agent Python projects) that the preceding full verification already ran. The fixture tests MUST exercise the changed action script for every component kind (verification plan, .NET, Node, Python, Markdown), so a regression in the changed action still fails the self-test. The merge workflow passes `environment-tests-only` to the self-test step; that workflow edit is made by a person outside the implementation (FR-008).
- **FR-003**: The self-test outcome MUST continue to feed the verdict, and a failing self-test MUST continue to refuse the merge with the failed check named.
- **FR-004**: No check MUST be weakened: the full verification of the merged result keeps every check and every test, runs in the same mode, and still decides the verdict for specs that are not environment specs; no test is removed, skipped, or relaxed.
- **FR-005**: The self-test of the changed environment setup, its skip rules, and its effect on the verdict MUST stay as they are.
- **FR-006**: The Spec Kit tooling tests and the environment action tests MUST keep passing, and the changed self-test behavior MUST be covered by a test in the environment action's own tests, including fixture tests that run the changed action script for every component kind (verification plan, .NET, Node, Python, Markdown) with stubbed tools.
- **FR-007**: The workflows README and the environment-verify README MUST state the time limit of the merge verification (where they state one) and MUST describe the self-test as it is implemented, correcting any statement the change makes false.
- **FR-008**: The change MUST be limited to the merge workflow, the environment-verify action with its README and tests, and the workflows README, and MUST NOT change product code. The edits of the merge workflow and of the workflows README are made by a person directly on the default branch, outside the implementation, because implementation agents may not change `.github/workflows/`; the implementation's tasks change only the environment-verify action, its tests, and its README.

### Key Entities

- **Merge verification job**: the read-only job of the merge workflow that checks the prospective merged tree and records the verdict; it has a time limit.
- **Full verification**: the finalize-mode run of every check and every test on the merged result, using the trusted action.
- **Self-test**: for environment specs only, the run of the changed environment actions from the workspace; its outcome feeds the verdict.
- **Verdict**: the recorded result of the verification that the merge land step reads (`the merged result was not verified` when it is missing).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The merge verification of an environment spec records its verdict in every run where the full verification takes up to 70 minutes; the four cancelled runs of the stopped spec no longer end without a verdict.
- **SC-002**: Observed after the first environment merge (quickstart step 5), not a gate that the tasks implement: the self-test of the changed verification takes minutes, not an hour, and adds less than 10% to the duration of the full verification.
- **SC-003**: The time limit is at most 360 minutes and at least one third above the time of the full verification plus the self-test.
- **SC-004**: The stopped spec of twin #382 reaches a merge decision after its merge verification completes, without a person changing the workflow by hand.
- **SC-005**: Every check of the full verification of the merged result still runs in it, and the changed action's component, plan, and Markdown logic is still exercised by the action's own fixture tests (stubbed tools) in the self-test, not by a repeated real run; the existing Spec Kit tooling tests and environment action tests all pass.

## Assumptions

- The full verification of an environment spec takes about 70 minutes, as measured in the four cancelled runs; the other steps are estimates, not measurements (environment-setup self-test about 5 minutes, narrowed verification self-test about 2 minutes, setup and integrate about 3 minutes), which sum to about 80 minutes. The limit of 150 minutes is derived from that sum plus margin and capped at 360 minutes; SC-002 is checked as an observed outcome after the first environment merge.
- The self-test of the changed environment setup is unchanged; only the verification self-test narrows.
- The action's own tests are those the "Environment action tests" check already runs, together with their fixtures; the self-test needs no new test inputs.
- Narrowing the self-test does not remove a component test from the merge gate, because the full verification of the merged result runs every component test, but that run uses the trusted action from the tooling checkout. The changed action's component, plan, and Markdown logic is therefore covered by the action's own fixture tests (stubbed tools), not by a repeated real run on the merged tree; this is a bounded difference in how the changed action is exercised, accepted because the fixtures run the changed script for every component kind (FR-002, FR-006).
- The edits of `.github/workflows/speckit-implement-merge.yml` and `.github/workflows/README.md` are made by a person directly on the default branch, outside the implementation (the Scope Note of `tasks.md`); they must be on the default branch before this spec's merge verification runs, or in the same change.
- The stopped spec of twin #382 depends on this spec; implementing this spec is the way its merge verification can complete. Implementation agents may not change `.github/workflows/`, so this change is made through this spec's own review and merge.
- The platform's production limits and values are unaffected; no product behavior changes.
