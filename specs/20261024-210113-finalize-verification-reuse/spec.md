# Feature Specification: Finalize Verification Without Repeated Work

**Feature Branch**: `20261024-210113-finalize-verification-reuse`

**Created**: 2026-10-24

**Status**: Draft

**Input**: User description: "Environment-only spec: change the composite action `.github/actions/environment-verify`
(`action.yml`, its README, and its Node test files next to it; no other file, and nothing under `.github/workflows`)
so that finalize mode no longer repeats expensive work within one job. Today the work job of the implementation-task
workflow (90-minute limit) runs 'Verify the change' in finalize mode for a conflict resolution (about 76 minutes) and
then, when the merge changed the environment actions, runs 'Self-test the synchronized environment verification' in
finalize mode again with the same workspace and the same changed files; the second full run pushes the job over 90
minutes, the job is cancelled before it records a verdict, the land job reports 'required synchronization needs
configured verification', and every conflict resolution attempt counts as failed. The workflow file is protected, so
the fix has to live in the action: when a finalize run in the same job has already completed successfully for the
identical workspace state, the later finalize run reuses that result and reports it instead of running every check
again, and/or, when every changed file lies in the two environment action folders, finalize mode runs only the
environment action tests and the Markdown check instead of the platform, Web UI, Manager, and Analyst projects. A
reused or reduced run must never turn a failed or missing earlier result into a pass: a failed or absent earlier run
is always repeated in full. Verification plus self-test must then finish well under the 90-minute job limit. Keep
unchanged: the Agent Python projects check and its stages, the rerun of failed tests with its flaky output, the
browser tests retry, the task-mode Markdown check, the covered paths, and the check order; add tests for the reuse and
the reduced scope to the existing Node tests and update the action README. Why the stopped spec needs it: the spec
`20261018-110526-agents-python-environment` (twin #174, implementation pull request #195) is an environment-only spec
whose conflict resolution (required synchronization with main) was cancelled twice by the 90-minute limit, and
`/speckit sync` fails the same way on the unchanged branch. Requested by @markusheiliger. The spec
`specs/20261018-110526-agents-python-environment` depends on this spec, and this spec must be implemented and merged
to main before that implementation can continue."

## Clarifications

### Session 2026-10-24

- Q: When every changed file lies in the two environment action folders, should the reduced finalize run also run the Agent Python projects check, or only the environment action tests and the Markdown check? → A: Only the environment action tests and the Markdown check; the Agent Python projects check is skipped in the reduced scope
- Q: How does the action decide that two finalize runs belong to the same job, and where is the result record kept? → A: A file in the runner's temporary folder named by the workflow run id, run attempt, and job id; without those values no record is written or reused
- Q: What should the finalize run do when the workspace state identity cannot be derived with certainty (for example, no Git metadata or untracked ignored files that affect the build)? → A: Record nothing and reuse nothing: run every check of its scope in full and report that reuse was not possible

## User Scenarios & Testing *(mandatory)*

The users are the Spec Kit implementation automation and the contributors who rely on it. Their need is that the
finalize verification of one job finishes inside the job's time limit and still means what it says: a pass is a pass
of the checks the change needs, never a pass inherited from a failed or missing result. The work is build-environment
tooling only. It changes nothing but the verification action folder `.github/actions/environment-verify` (its action
definition, its README, and its Node tests next to it) and the spec's own task ticks; it changes no workflow, no
product code, and no other action.

### User Story 1 - A conflict resolution that also self-tests the synchronized environment finishes in time (Priority: P1)

A conflict resolution for a required synchronization with the base branch runs the finalize verification of the
merged change. Because the merge changed the environment action folders, the same job then runs the finalize
verification again as the self-test of the synchronized environment verification, with the same workspace and the same
changed files. The second run must not repeat the expensive work of the first: it reports the first run's successful
result instead, so the job records its verdict well before its time limit and the conflict resolution attempt is not
counted as failed for running out of time.

**Why this priority**: This is the defect that stops the dependent environment-only spec; every conflict resolution
and every `/speckit sync` on it is cancelled by the job time limit and counted as a failed attempt.

**Independent Test**: In a test fixture, run the action in finalize mode twice in one job for the identical workspace
state, mode, and changed files, with a first run that passes. Confirm that the second run does not execute the checks
again, reports the first run's result as reused, and ends with the same verdict, and that the total time is about that
of one run.

**Acceptance Scenarios**:

1. **Given** a finalize run that completed successfully in this job for a workspace state, **When** a later finalize
   run starts in the same job for the identical workspace state, the same mode, and the same changed files, **Then**
   it executes none of the checks again, reports the earlier result as reused, and passes with the same checks,
   uncovered paths, and flaky tests the earlier run reported.
2. **Given** the conflict resolution scenario of the stopped spec (a first finalize run of about 76 minutes followed by
   the self-test), **When** both run in one job, **Then** the verification and the self-test together finish well under
   the 90-minute job limit and the job records its verdict.

---

### User Story 2 - A reused or reduced run never turns a failure or a gap into a pass (Priority: P1)

Reuse and reduced scope are only shortcuts for work that has demonstrably been done. When the earlier run failed, was
cancelled, never ran, or ran for a different workspace state, mode, or changed files, the later finalize run repeats
every check in full and reports its own result. A reduced run never hides a failing check that the reduction would
have skipped for a change that touches anything beyond the two environment action folders.

**Why this priority**: The verification is the quality gate before a merge. A shortcut that can pass on stale,
failed, or missing evidence would defeat the gate, so it is as important as the speed-up itself.

**Independent Test**: With fixtures for a failed earlier run, an absent earlier result, a result for another workspace
state, another mode, and other changed files, and a damaged or incomplete result record, run finalize mode and confirm
that every check runs in full each time and that the verdict is the verdict of that full run.

**Acceptance Scenarios**:

1. **Given** an earlier finalize run in the job that failed, **When** a later finalize run starts for the identical
   workspace state and changed files, **Then** it repeats every check in full and fails or passes on its own result.
2. **Given** no earlier finalize run in the job, a result recorded for a different workspace state, a different mode,
   or different changed files, or a result record that is incomplete or unreadable, **When** a finalize run starts,
   **Then** it runs every check in full and never reports a reused result.
3. **Given** a workspace that changed between two finalize runs (even by a single file), **When** the later run
   starts, **Then** it does not reuse the earlier result.

---

### User Story 3 - An environment-only change is verified by the environment checks alone (Priority: P2)

When every changed file of a finalize run lies in the two environment action folders (`.github/actions/environment-setup`
and `.github/actions/environment-verify`), the finalize run executes only the environment action tests and the
repository Markdown check, instead of the platform, Web UI, Analyst Manager, and Analyst project checks that such a
change cannot affect. Any changed file outside the two folders, or an empty or unknown list of changed files, keeps the
full finalize scope.

**Why this priority**: It keeps even the first, unreused finalize run of an environment-only change short, so the
dependent spec's merge verification fits the job limit on its own; the reuse of User Story 1 already removes the
repeated run.

**Independent Test**: Run finalize mode with changed files only in the two folders and confirm that only the
environment action tests and the Markdown check run and are reported; add one file outside the folders (and, separately,
pass no changed files) and confirm that the full scope runs.

**Acceptance Scenarios**:

1. **Given** changed files that all lie in `.github/actions/environment-setup` or `.github/actions/environment-verify`,
   **When** the finalize verification runs, **Then** it runs the environment action tests and the Markdown check and
   no platform, Web UI, Manager, or Analyst project check, and it reports that the scope was reduced and why.
2. **Given** changed files that include at least one path outside the two folders, or no changed files at all, **When**
   the finalize verification runs, **Then** every check of the full finalize scope runs as it does today.
3. **Given** a reduced run in which an environment action test or the Markdown check fails, **When** it ends, **Then**
   the verification fails and names the failing check, exactly as in the full scope.

---

### User Story 4 - Everything else about verification stays as it is (Priority: P2)

Reviewers and the automation see no change to the checks that are not the subject of this spec: the Agent Python
projects check and its stages, the rerun of failed tests with its flaky output, the browser tests retry, the task-mode
Markdown check, the covered paths, and the order of the checks behave as before in every mode and scope that this spec
does not reduce.

**Why this priority**: The action is a shared quality gate; a regression in an unrelated check would be worse than the
timeouts this spec removes.

**Independent Test**: Run the existing Node tests of the action unchanged and confirm they pass; run the task and
phase modes and the unreduced finalize mode and confirm their checks, order, outputs, and covered paths are the same
as before.

**Acceptance Scenarios**:

1. **Given** task mode, phase mode, or a finalize run whose scope is not reduced and whose result is not reused,
   **When** the action runs, **Then** its checks, their order, the covered paths, and the `checks`, `uncovered`, and
   `flaky` outputs are the same as before this change.
2. **Given** the change under review touches only the verification action folder, **When** the existing Node tests run,
   **Then** they pass together with the new tests for the reuse and the reduced scope.

### Edge Cases

- The earlier finalize run was cancelled by the job time limit or by a runner failure before it recorded a result: no
  result exists, so the later run repeats every check in full.
- The earlier run passed but the workspace was modified afterwards (a file edited, added, removed, or committed, or a
  different commit checked out): the identity of the workspace state differs, so nothing is reused.
- The changed-files list has the same files in a different order or with different line endings or blank lines: the
  list is treated by its content, so the same set of changed files counts as identical, and a different set does not.
- Two jobs, or two attempts of one job, use the same runner folder or result folder: a result recorded by another job
  or attempt must never be reused, because the result belongs to one job.
- The workflow run id, run attempt, or job id is missing or empty, or the workspace state identity cannot be derived
  with certainty (no Git metadata, untracked ignored files that affect the build): no record is written or reused,
  every check of the scope runs in full, and the run reports that reuse was not possible.
- A result record is empty, truncated, from another version of the record, or claims a pass without the data needed
  to report it: it is ignored and the checks run in full.
- A changed-files list names the two folders only through a name-prefix lookalike (a sibling folder whose name starts
  with the same text) or through a path that leaves the folder: it does not count as inside the two folders.
- The earlier run passed with uncovered paths or flaky tests: the reused run reports the same, so no information is
  lost and no uncovered path becomes covered.
- The later finalize run uses another copy of the action than the earlier one (the self-test runs the synchronized
  workspace copy, the first run the trusted tooling copy): the record does not name the action copy, so the later run
  reuses the earlier result for the same workspace state, mode, changed-files content, and scope.
- Both shortcuts apply (an environment-only change whose first run was reduced): the later run reuses the result only
  when its scope is the same as the earlier run's, and otherwise runs in full for its own scope.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: When a finalize run completes successfully, the action MUST record a result that identifies the workspace
  state it verified (including every committed and uncommitted difference from the checked-out commit), the mode, the
  content of the changed-files list, and the scope that ran, and MUST keep it where only the current job can read it: a
  file in the runner's temporary folder whose name is derived from the workflow run id, the run attempt, and the job
  id (never in the repository, never in an artifact that outlives the job). When any of the three values is missing or
  empty, the action MUST NOT write or reuse a record.
- **FR-002**: A later finalize run in the same job MUST reuse a recorded result and execute none of the checks again
  only when the recorded workspace state, mode, changed-files content, and scope are identical to its own and the
  record says the earlier run passed completely.
- **FR-003**: A reused run MUST report that the result was reused and from which earlier run, MUST pass with the same
  `checks`, `uncovered`, and `flaky` outputs the earlier run reported, and MUST list them in its log and step summary
  as a reused result, not as a freshly executed one.
- **FR-004**: A failed, cancelled, missing, unreadable, incomplete, or mismatching earlier result MUST never be reused:
  the finalize run MUST repeat every check of its scope in full and report its own result. No later input, retry, or
  failure of reading the record may turn a failure or an absent result into a pass.
- **FR-005**: The action MUST record a result only for a finalize run that passed, and MUST NOT record, update, or keep
  a passing result for a run that failed or did not complete.
- **FR-006**: When every changed file lies inside `.github/actions/environment-setup` or
  `.github/actions/environment-verify`, finalize mode MUST run only the environment action tests and the Markdown check
  (the Agent Python projects check is skipped in the reduced scope), and MUST report that the scope was reduced to the
  environment actions and why.
- **FR-007**: Finalize mode MUST keep its full scope when any changed file lies outside the two folders, when the
  changed-files list is empty or missing, or when a path cannot be classified with certainty (including paths that
  only share a name prefix with a folder or leave it).
- **FR-008**: A reduced run MUST fail when any of its checks fails, MUST report its checks and its uncovered paths
  truthfully for the reduced scope, and MUST NOT claim coverage for any path whose checks it did not run.
- **FR-009**: The Agent Python projects check and its stages, the rerun of failed tests with its flaky output, the
  browser tests retry, the task-mode Markdown check, the covered paths, the existing check order, and the action's
  inputs and outputs MUST remain unchanged in every mode and scope that this change does not reduce or reuse.
- **FR-010**: The change MUST be confined to `.github/actions/environment-verify` (the action definition, its README,
  and its Node test files next to it) and the spec's own task ticks; it MUST NOT change any workflow, any other
  action, or any product file, and the action MUST need no input or setup the existing callers do not already provide.
- **FR-011**: Tests added to the existing Node tests MUST cover: a reused result after a passing run; the repeat in
  full after a failed run, an absent record, a record for another workspace state, another mode, and other changed
  files, and a damaged record; the identity of the workspace state when only a file changes; the equivalence of an
  identically-contented changed-files list; the reduced scope for changed files only in the two folders; and the full
  scope for a file outside them, for a name-prefix lookalike, for a leaving path, and for an empty list.
- **FR-012**: The README of the action MUST describe the reuse and the reduced scope as they behave: what identifies a
  reusable result, where the result lives and how long, when the full scope runs, what is reported, and that a failed
  or missing earlier result is always repeated in full.
- **FR-013**: The two finalize runs of the conflict resolution scenario (the verification and the self-test of the
  synchronized environment verification) MUST together take about as long as one finalize run, and MUST finish well
  under the 90-minute job time limit. The minute figures are confirmed by the next real conflict resolution, not by a
  test; the executable evidence is that the reused run executes no check.
- **FR-014**: When the workspace state identity cannot be derived with certainty (for example, no Git metadata, or
  untracked ignored files that affect the build), the finalize run MUST record nothing and reuse nothing, MUST run
  every check of its scope in full, and MUST report that reuse was not possible.

### Key Entities

- **Finalize result record**: The proof that a finalize run passed completely: the identity of the verified workspace
  state, the mode, the content of the changed-files list, the scope that ran, the reported checks, uncovered paths, and
  flaky tests, and the run it came from. It lives only as long as the job and is never a source of a pass unless every
  identity part matches.
- **Workspace state identity**: A value that is the same for two runs only when the checked-out commit and every
  committed and uncommitted file content are the same.
- **Changed-files content**: The set of changed paths a run receives, compared by its content rather than by its
  formatting.
- **Environment-only scope**: The reduced finalize scope that applies when all changed files lie in the two
  environment action folders.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A conflict resolution job that runs the finalize verification and then the self-test of the synchronized
  environment verification for the same workspace and changed files finishes with a recorded verdict in under 80
  minutes in total, where the same job exceeds the 90-minute limit today, and the second run adds under 2 minutes to
  the first. The minute figures are confirmed by the next real conflict resolution, not by a test.
- **SC-002**: A finalize verification of an environment-only change completes in under 15 minutes on its own, running
  only the environment action tests and the Markdown check.
- **SC-003**: In 100% of the tested cases of a failed, cancelled, missing, damaged, or mismatching earlier result,
  the later finalize run repeats every check in full, and in none of them does a reused or reduced run pass where a
  full run of the same workspace would fail.
- **SC-004**: In 100% of the tested cases with at least one changed file outside the two environment action folders, or
  with no changed files, finalize mode runs the same checks in the same order as before this change.
- **SC-005**: The existing Node tests of the action pass unchanged, and the new tests for the reuse and the reduced
  scope pass.
- **SC-006**: The stopped spec `20261018-110526-agents-python-environment` can complete its required synchronization
  with the base branch without a conflict resolution attempt ending by the job time limit.

## Assumptions

- The two finalize runs of one job receive the same workspace and the same changed-files content, as the stopped
  conflict resolution shows, and run in one job whose runner temporary folder or result folder is private to that
  job; a result is therefore trusted only within the job that recorded it.
- The workspace state identity can be derived from the checked-out commit and the working tree's committed and
  uncommitted content; where it cannot be derived with certainty, no result is recorded or reused and every check of
  the scope runs in full (FR-014).
- The first finalize run of the conflict resolution takes about 76 minutes, as observed, and a reused run adds only the
  time to read and report the record; the reduced scope for an environment-only change is a further safeguard that
  also applies to the first run.
- The reduced scope relies on the environment action tests and the Markdown check covering everything an
  environment-only change can break; platform, Web UI, Manager, and Analyst project checks run on every change that
  touches anything else.
- A finalize result is keyed by the workspace state, the mode, the changed-files content, and the scope, not by the action
  copy that produced it: the self-test of the synchronized environment verification intentionally accepts the earlier
  trusted result of the same job even when it runs the workspace copy of the action, so a changed action script or its
  tests are not executed again by the self-test.
- The root `README.md` and `AGENTS.md` wording of the finalize mode (every check with all tests) stays unchanged by this
  environment-only spec; the action README is the authority for the finalize reuse and the reduced scope.
- The workflow files that call the action are protected and stay unchanged, so the action alone provides the behavior,
  and the callers need no new input.
- Out of scope: any change to workflows, to the setup action, to product code or documentation outside the action
  folder, to timeout values, to the checks' content, or to the covered paths; a persistent cache across jobs; and
  production readiness evidence.
- Dependency: this spec must be implemented and merged to the default branch before the implementation of
  `specs/20261018-110526-agents-python-environment` (twin #174, pull request #195) can continue.
