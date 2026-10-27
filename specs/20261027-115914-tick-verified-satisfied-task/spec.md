# Feature Specification: Tick a Verified, Already-Satisfied Task

**Feature Branch**: `20261027-115914-tick-verified-satisfied-task`

**Created**: 2026-10-27

**Status**: Draft

**Related to**: the stopped implementation of `specs/20261026-100223-environment-guard-archive-rewrites` (pull request #344), which failed at tasks T004 and T005. This spec generalizes the cause of that stop but does **not** unblock it: T004 and T005 name only files under `.github/scripts/`, which no check covers, so they stay outside the new path. That implementation does not depend on this spec and must not wait for its merge; it continues through a push by a person or Copilot to its branch (`doneByPush`).

**Input**: User description: "Let the Spec Kit land job tick a task whose work is already on the implementation branch. Today the task land path in `.github/scripts/speckit-implement-work.mjs` (the `kind === 'task'` branch that calls `doneByPush` and `applyPatch`) rejects an empty agent change with `the agent made no changes` unless `doneByPush` finds a non-bot commit that is the branch head or came after the task's first attempt. When a Copilot or person's push added the task's code before the first attempt and the bot's commits for earlier tasks sit on top of it, the agent correctly changes nothing, the exemption does not apply, and the task fails until the attempt limit (this stopped spec `20261026-100223-environment-guard-archive-rewrites`, pull request #344, at tasks T004 and T005, although the guard code and tests are on its branch and the core and implement test suites pass). Change `doneByPush` and the land-tasks path (with tests in `.github/scripts/speckit-implement.test.mjs` or the matching test file, and the workflows README description if it states the rule) so that a task whose agent reports no change but whose verification of the unchanged branch passes can be ticked by the land job, while an agent that merely skips a task whose work is missing still fails verification and the task. Keep the attempt limits (`MAX_TASK_ATTEMPTS`), the existing `doneByPush` rule, and the existing rules for tasks that change files (protected paths, environment exclusivity, covered-file checks). The guidance of the person who asked (@markusheiliger): change doneByPush and the land-tasks path so an already-satisfied, verified task can be ticked by the land job."

## Problem

The implementation automation lets an agent implement one task at a time, verifies the result, and lets a separate land job commit the change and tick the task's checkbox. When the agent finds nothing to change, the land job refuses the attempt with `the agent made no changes`, unless a commit that is not the workflow's (a person's, or GitHub Copilot's for them) is the branch head or reached the branch after the task's first attempt.

That exemption misses a legitimate case. A person or Copilot pushes the task's work to the implementation branch before the task's first attempt, and the workflow's own commits for earlier tasks then land on top of that push. The agent correctly finds the task already satisfied and changes nothing. The push is neither the head nor after the first attempt, so the exemption does not apply, the empty change fails, and the task fails until the attempt limit stops the implementation and waits for a person. This is the shape of the stop of `specs/20261026-100223-environment-guard-archive-rewrites` (pull request #344) at tasks T004 and T005, although the guard code and its tests were on the branch and the test suites passed. This spec fixes the general cause for tasks that a test or build check covers; it does not unblock #344 itself, because T004 and T005 name only uncovered files under `.github/scripts/` and still need a push by a person or Copilot (`doneByPush`).

The exemption asks *who pushed and when*, which says nothing about whether the task's work is really there. What matters is whether the unchanged branch passes the task's verification, and whether the agent merely skipped a task whose work is missing.

## Clarifications

### Session 2026-10-27

- Q: How does the land job decide that the verification of the unchanged branch 'covered the task's work' when the agent's change is empty and there is no diff to match against the task? → A: The verification passed and ran at least one check (test or build step) selected for the files the task names in tasks.md; a task naming no verifiable file cannot be ticked this way and fails as an empty change.
- Q: How is the spec's relation to the stopped implementation #344 stated, given that T004 and T005 name only uncovered `.github/scripts/` files? → A: Reframed as "Related to": the spec does not unblock #344, which does not depend on it and continues through a push by a person or Copilot (`doneByPush`); the Problem and Assumptions say so (correction round 1, F2).
- Q: Should FR-009 also cover `AGENTS.md`, which plan and tasks change? → A: Yes; FR-009 names the `AGENTS.md` sentence as a supporting change (correction round 1, F4).
- Q: When the passed verification ran on a different branch head than the one the land job lands on (the branch moved after the work job), should the empty change fail the attempt (counting toward the attempt limit) or be refused without consuming an attempt so the task runs again on the new head? → A: Fail the attempt with a reason naming the head mismatch; it counts toward MAX_TASK_ATTEMPTS like any other failed attempt.
- Q: Should a task ticked through the new already-satisfied path ever hold the pull request for a person's review, or is it ticked and continued automatically like any other verified task? → A: Tick and continue automatically; the comment and check run only mark the task as already satisfied (FR-007).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A task already satisfied by an earlier push is ticked (Priority: P1)

A person or Copilot pushed the code and tests of a task to the implementation branch before the task's first attempt. The workflow later reaches the task, its agent finds nothing left to change, and the verification of the unchanged branch passes. The land job ticks the task without a change of its agent, and the implementation continues with the next task.

**Why this priority**: it removes a stop that needs a person although nothing is wrong with the branch, for every task whose named files a configured test or build check covers. Tasks that name only files under `.github/scripts/` are not covered by any check and are outside this story; they still need a push by a person or Copilot (`doneByPush`).

**Independent Test**: Run the land path for a task whose agent change is empty, whose pushed work predates the first attempt with workflow commits on top, and whose verification of the unchanged branch passed; the task is ticked and the attempt does not fail.

**Acceptance Scenarios**:

1. **Given** a task whose agent reports no change and whose verification of the unchanged branch passed, **When** the land job runs, **Then** it ticks exactly that task's box and records no failed attempt.
2. **Given** the push that did the work lies before the task's first attempt and workflow commits for earlier tasks sit on top of it, **When** the agent changes nothing and verification passes, **Then** the task is ticked.
3. **Given** a person's push after the task's first attempt or at the branch head, **When** the agent changes nothing, **Then** the task is ticked as before.

---

### User Story 2 - An agent cannot skip a task whose work is missing (Priority: P1)

An agent that changes nothing for a task whose work is not on the branch must still fail, because the verification of the unchanged branch does not pass, or does not cover the task's work.

**Why this priority**: the empty-change refusal exists so that a skipped task is never ticked; relaxing it must not let unfinished work through.

**Independent Test**: Run the land path for a task whose agent change is empty and whose work is missing, with the verification of the unchanged branch failing; the attempt fails and the task stays unticked.

**Acceptance Scenarios**:

1. **Given** an agent that changes nothing and a verification of the unchanged branch that fails, **When** the land job runs, **Then** the attempt fails with a reason and the task is not ticked.
2. **Given** an agent that changes nothing and no verification that checks the task's work (verification missing, not configured, or not run), **When** the land job runs, **Then** the attempt fails and the task is not ticked.
3. **Given** two tasks that name the same file, an earlier task that changed that file, and an agent that changes nothing for the second task with a passing verification, **When** the land job runs, **Then** the attempt fails with the cause "no named file is specific to this task" and the second task is not ticked.
4. **Given** a failed attempt, **When** the attempts of the task reach `MAX_TASK_ATTEMPTS`, **Then** the person who requested the implementation is asked, as before.

---

### User Story 3 - Tasks that change files keep every existing rule (Priority: P2)

A task whose agent does change files is landed under the same rules as today: protected paths, environment exclusivity, and covered-file checks, and the existing push exemption keeps its meaning.

**Why this priority**: the change must not weaken the safeguards that protect the repository from agent-written changes.

**Independent Test**: The existing land-path tests for changing tasks, refused protected paths, environment exclusivity, and uncovered files still pass unchanged.

**Acceptance Scenarios**:

1. **Given** an agent that changes a protected path, **When** the land job runs, **Then** the attempt fails as before.
2. **Given** an agent that changes files not covered by verification, **When** the land job runs, **Then** the task is held for review as before.
3. **Given** a person's push after the first attempt or at the head and an empty agent change, **When** the land job runs, **Then** the existing exemption still lets the task land.

### Edge Cases

- The agent changes nothing, but only some of the task's work is on the branch: verification fails or does not cover the missing work, and the task fails.
- The agent changes nothing and verification of the unchanged branch passes only because the task has no verifiable work (the task names no file that a test or build step is selected for, for example a documentation-only task): the land job does not tick a task on a verification that checks nothing of it; the attempt fails as an empty change, as today.
- Every file the task names is also named by another task of `tasks.md`: the branch difference of such a file may stem from the other task, so the work of this task is not evidenced and the attempt fails with the cause "no named file is specific to this task".
- Verification reports files not covered by any check, or a failed or flaky-only result: the task is not ticked by this path.
- The branch moved between the work job's verification and the land job (new commits since the verified head): the verification no longer describes the branch, the empty change is not accepted on it, and the attempt fails with a reason naming the head mismatch and counts towards `MAX_TASK_ATTEMPTS`.
- The commit listing or comments cannot be read: the existing exemption stays closed (as today), and this path depends only on the verification result.
- The task is already ticked on the branch: the land job accepts it as before.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: When the agent's change for a task is empty and the verification of the unchanged branch passed, the land job MUST tick exactly that task's box and MUST NOT treat the empty change as a failure.
- **FR-002**: The land job MUST accept this only when the passed verification was run on the very branch head the land job lands on, and it MUST refuse it when the verification did not run, was not configured, failed, covered none of the task's work, or ran on a different head. The verification covers the task's work only when it passed and ran at least one check (a test or build step) selected for the files the task names in `tasks.md`; a task that names no verifiable file cannot be ticked this way and fails as an empty change. `environment-verify` selects no check for files under `.github/scripts/`, so a task that names only such files (Spec Kit tooling) is not covered and fails as an empty change with the named cause; it still needs a push by a person or Copilot (`doneByPush`). The "Markdown check" does not count as a qualifying check: only test or build checks qualify, so a task that names only Markdown files cannot be ticked this way. The land job MUST also recompute that the task's work is evidenced on the branch: at least one file the task names that no other task in `tasks.md` names (a file specific to this task) differs from the base branch (the existing `branchPaths` diff against `origin/<base>`); a passing verification of files that exist unchanged on the base branch, or that another task names too, does not satisfy the task, and a task none of whose named files is specific to it is refused with the named cause "no named file is specific to this task". The "very branch head" is the pull request's current head (the tip fetched before the land checkout), not the detached checkout of the work head.
- **FR-003**: When the agent's change is empty and the verification of the unchanged branch does not pass (including a verification that ran on a different head than the one the land job lands on, which is named as a head mismatch), the attempt MUST fail with a reason that names the cause, the task MUST NOT be ticked, and the failed attempt MUST count towards `MAX_TASK_ATTEMPTS` like any other failed attempt; a head mismatch is not refused without consuming an attempt.
- **FR-004**: The existing `doneByPush` rule (a commit that is not the workflow's is the branch head or reached the branch after the task's first attempt) MUST keep its meaning and result; this feature adds a second, independent way for an empty change to land and does not remove or narrow the first.
- **FR-005**: The rules for tasks that change files MUST NOT change: protected paths, environment exclusivity, covered-file checks, and the `tasks.md` rule (the land job ticks exactly that task's box).
- **FR-006**: The attempt limit `MAX_TASK_ATTEMPTS` and the way attempts are counted MUST NOT change.
- **FR-007**: The pull request comment and the check run of a task landed this way MUST say that the task was already satisfied and ticked on the verification of the unchanged branch, so a person can tell it from a task the agent implemented. The marking is informational only: such a task is ticked and the implementation continues automatically, and the pull request is never held for a person's review because of this path.
- **FR-008**: Automated tests MUST cover: an already-satisfied task with passing verification (including the case of a pushed commit before the first attempt with workflow commits on top), an agent skipping a task with failing verification, with no verification, and with a verification of a different head, an agent skipping the second of two tasks that name the same file, a first push rejected because the branch moved with the empty patch not re-applied (for both the verified-unchanged path and the `doneByPush` path), the unchanged results of the existing `doneByPush` cases, and the work-job half of the path: `runPackage` on an empty patch writes `taskFiles` and `changed-files.txt` with `changedFiles` still empty, `runVerdict` on an empty patch with a failed or not-run verification writes `unchanged` while `reasons` and `verified` stay as they were, and a non-empty patch writes no `unchanged` object.
- **FR-009**: The workflows README MUST describe the rule: the existing push exemption and the verified, already-satisfied path, and that an agent that changes nothing for a task whose work is missing still fails. The sentence of `AGENTS.md` that states the `doneByPush` rule is updated the same way as a supporting change so both descriptions stay consistent.

### Key Entities

- **Task attempt**: one run of the agent for one task, with an agent change (possibly empty), a verification result, and an outcome counted towards the attempt limit.
- **Verification of the unchanged branch**: the result of the task's verification for the branch head the agent started from, when the agent made no change.
- **Already-satisfied task**: a task whose work is on the implementation branch (by a person's or Copilot's push, or an earlier task) and that the agent leaves unchanged.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An implementation whose task work was pushed before the task's first attempt proceeds past that task without a person for a task that names at least one file covered by a configured test or build check: the task is ticked at the first attempt whose verification passes, and no attempt fails for an empty change. SC-004 states the uncovered case.
- **SC-002**: In every automated test where the agent skips a task whose work is missing, the attempt fails and the task stays unticked (zero tasks ticked on a failing, missing, or mismatched verification).
- **SC-003**: Every existing test of the task land path and of the `doneByPush` rule still passes with unchanged expectations.
- **SC-004**: A task that names at least one file covered by a configured test or build check and whose work was pushed earlier is ticked without editing code. Tasks that name only files under `.github/scripts/`, such as T004 and T005 of `specs/20261026-100223-environment-guard-archive-rewrites`, are covered by no check and still need a push by a person or Copilot (`doneByPush`) to continue.
- **SC-005**: A person reading the pull request can tell from its comment that a ticked task was already satisfied rather than implemented by the agent.

## Assumptions

- The work job of a task already verifies the working tree of its start, including when the agent changed nothing, and records its result (verification status, covered files, the verified head); if it does not, producing that record is part of this feature.
- The verification of a task is the configured verification of the environment; a task whose work no configured check exercises (no test or build step selected for the files it names in `tasks.md`) cannot be ticked through this path and fails as an empty change, as today.
- The land job already trusts only values it can recompute itself (the checked-out head); it does not trust the work result's claims without comparing them.
- The existing exemption and its tests stay as they are; the new path is additive.
- The change is limited to the Spec Kit tooling under `.github/scripts/`, its tests, and the workflows README; no product code, contract, or `docs/architecture/` statement is affected.
- No check covers files under `.github/scripts/` (`environment-verify` selects checks only for static covered paths, components, and Python projects), and this spec adds none; adding one would need a standalone environment spec and is out of scope.
- This spec modifies protected tooling paths (`.github/scripts/`), which the land job rejects for agents. It is implemented locally (`SPECKIT_IMPLEMENT_MODE=local`) or by a person's or Copilot's push, not by the unattended agent, and its pull request is held for review because changes under `.github/scripts/` are covered by no check.
- The spec `specs/20261026-100223-environment-guard-archive-rewrites` does not depend on this spec: its stopped tasks T004 and T005 are not covered by this path, so waiting for this spec would not unblock it. It continues through a push by a person or Copilot to its implementation branch; any dependency of its twin on this spec's twin is to be removed on the spec twins.
