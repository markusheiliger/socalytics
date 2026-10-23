# Feature Specification: Deterministic Upload Recovery Browser Test

**Feature Branch**: `20261023-031208-deterministic-upload-recovery-test`

**Created**: 2026-10-23

**Status**: Draft

**Fix for**: #239 (implementation of `specs/20261022-190935-web-ui-run-comparison`)

**Input**: User description: "Make the Web UI simulated browser test `[simulated] tests/e2e/simulated/recovery.spec.ts` › `refuses a different file and changes nothing (scenario 2)` deterministic. It lives in `src/clients/web/tests/e2e/simulated/recovery.spec.ts` (with the fake object store in `src/clients/web/tests/e2e/support/`), owned by the Web UI connected recording upload and resume feature. It failed and then passed on its one rerun in the verifications of implementation pull requests #216, #221 and #239, so it is flaky; it is unrelated to the stopped spec `specs/20261022-190935-web-ui-run-comparison`, whose own changes do not touch uploads. Find and remove the cause of the flakiness (timing, ordering, shared state, external dependencies) in the test or in the code it covers, without weakening, skipping, or deleting the test or any assertion, and without retries, fixed sleeps, or longer timeouts as the fix. The spec `specs/20261022-190935-web-ui-run-comparison` (implementation pull request #239) depends on this spec."

## Clarifications

### Session 2026-10-23

- Q: May the fix change Web UI code (the upload worker or coordinator) if the cause lives there? → A: Yes, but only where the evidence puts the cause, and without changing the documented behavior of upload, resume, retry, or the different-file refusal. Test-only causes are fixed in the test, its shared helpers, or the fake object store.
- Q: Are fixed sleeps, retries, or longer timeouts acceptable? → A: No. The sequence must wait on an observable condition or control the racing input. The existing fixed 500 ms wait in the interrupted-upload helper is the kind of timing guess to remove, and no replacement may be a sleep.
- Q: Which tests are in scope? → A: The named test, and every test of `recovery.spec.ts` that shares the interrupted-upload helper, which must stay passing and deterministic. Other flaky tests and the platform browser projects are out of scope.

## Problem

The test starts an upload whose third part never succeeds, so that two parts are stored, and then reloads the match page so that the upload is remembered as interrupted. It then selects a different file for the remembered upload, expects the refusal ("not the file that was selected"), and asserts that the fake object store recorded no request at all. It fails in the verification of implementation pull requests and then passes on its single rerun, so the failure depends on timing and not on the behavior under test.

The suspected cause, to be confirmed by evidence before it is fixed, is in the shared helper that prepares the interrupted upload: after two parts are stored it waits a fixed 500 ms, resets the fake object store (clearing its faults and its recorded requests), and reloads the page. The upload worker of the first page load keeps retrying the failing part with its own retry delays, so a request can be in flight or be sent after the reset and before the page is gone. Such a request is recorded in the cleared log and, with the fault gone, can even be stored; the later "no requests" assertion then counts a request that the different-file refusal did not cause. Other candidates the implementation must rule in or out:

- shared state of the fake object store between the serial tests of the file (faults, parts, requests) and between a test and the end of the previous one, whose worker may still be running;
- upload sessions, journal entries, or workers left over from a previous test or from the page before the reload;
- the order in which the page's journal state, the pending-upload list, and the refusal message appear after the reload.

The implementation first reproduces the failure and identifies which of these (or another cause) produces it, using evidence such as repeated runs of the file, runs under CPU load, request logs with timestamps of failing runs, and traces, and then removes that cause.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The different-file refusal test fails only when behavior is wrong (Priority: P1)

A maintainer running the Web UI simulated browser tests, or the verification of any implementation pull request, sees the test pass whenever choosing a different file for a remembered upload is refused and changes nothing, however loaded the runner is, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, which then pass only on a rerun and report a flaky test that none of those features caused; the stopped implementation of #239 needs it reliable.

**Independent Test**: Run the file repeatedly alone and under CPU load; every run passes. Then break the behavior with a probe (for example a refusal that still sends a part, or a different file that is accepted) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** an upload whose last part keeps failing and two stored parts, **When** the test prepares the interrupted state, **Then** it proceeds to the reload only once no upload activity of the first page can send another request, observed through a condition and not through elapsed time, and the store's recorded requests are cleared only after that point.
2. **Given** the page was reloaded and the interrupted upload is listed, **When** a different file is chosen for it, **Then** the refusal is shown, and the store's recorded requests are empty because no request was sent after the preparation, with the assertion unchanged.
3. **Given** the preparation is shared by the other tests of the file, **When** they run in the same serial order, **Then** each starts from a store and a page with no activity left over from a previous test, and each keeps its assertions.
4. **Given** the different-file refusal were broken so that it sends a request, **When** the test runs, **Then** the unchanged assertions fail and the test does not hang.

### Edge Cases

- A worker that retries after a retry delay MUST NOT be able to record a request after the log was cleared; the preparation MUST NOT clear the log while any such retry can still happen.
- A request that is in flight when the preparation ends MUST be finished (or its page gone) before the log is cleared, so its late arrival is not counted.
- A failure in the middle of a test MUST NOT leave faults or activity that affect the next serial test.
- The fix MUST NOT depend on the order in which the tests of the file run, beyond the file's existing serial mode.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (repeated runs of `recovery.spec.ts` with and without CPU load and the recorded request log with timestamps of failing runs) and record it in a code comment next to the fix and in `src/clients/web/tests/e2e/simulated/recovery-evidence.md`, which the `## Evidence` section of `research.md` points to (the commit message, written by the person or automation that commits, repeats it).
- **FR-002**: The cause MUST be removed where it lives (the test, its shared helper, the fake object store and its control endpoints, or the upload code) by controlling the racing input, isolating shared state, or waiting on an observable condition, not by retrying the test, skipping it, adding a fixed sleep, or lengthening a timeout. The fixed 500 ms wait MUST NOT remain or be replaced by another fixed wait.
- **FR-003**: The recorded requests used by the "no requests" assertion MUST be cleared only after no upload activity of the page before the reload can send a request, and the assertion MUST count only requests caused after that point.
- **FR-004**: Every assertion of every test of `recovery.spec.ts`, including that the store recorded no request after the different-file refusal, MUST remain, and no assertion, expected value, or step MAY be removed, loosened, or skipped. Opening the match page again with `goto` instead of `reload` in the shared helper `startInterrupted` (the page is at `about:blank` by then) is the permitted equivalent of the reload, with the same resulting page state; the reloads inside individual tests are unchanged.
- **FR-005**: Production behavior MUST NOT be changed: any Web UI change MUST keep the documented behavior of upload, resume, retry, and the different-file refusal in `docs/architecture/`.
- **FR-006**: A change to shared support code (helpers or the fake object store) MUST keep the other simulated specs that use it (such as `uploads.spec.ts`) behaviorally unchanged and passing.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The named test passes in 100% of at least 30 consecutive runs of `recovery.spec.ts` in the `simulated` project alone, and in at least 10 consecutive runs under CPU load (one busy-loop process per core for the duration of the runs). If the failure does not reproduce naturally, the evidence additionally contains a deliberate stress reproduction (the race widened test-side only) in which the old helper fails and the new helper passes.
- **SC-002**: Every test of `recovery.spec.ts` passes in the same consecutive runs, and `uploads.spec.ts` and the other simulated specs that use the fake object store still pass.
- **SC-003**: Each of two probes (a different-file refusal that sends a part, and a different file that is accepted and uploaded) makes the named test fail on its assertions; the probes are removed afterward.
- **SC-004**: The code comment and `src/clients/web/tests/e2e/simulated/recovery-evidence.md` name the evidenced cause, and the change touches only the files the cause lives in plus that evidence file; no assertion is textually removed and no fixed wait or retry is added.
- **SC-005** (post-merge evidence, observed when #239 retries and not verified by a task of this spec): the verification of #239 reports no flaky `recovery.spec.ts` test.

## Assumptions

- **Cause**: The test belongs to the Web UI connected recording upload and resume behavior (the merged specs under `specs/archive/`). The cause is presumed to be a still-running upload retry racing the fixed wait and the store reset in the shared helper; this is a hypothesis, and the concrete cause is established during implementation (FR-001).
- **Dependencies**: [Web UI Run Comparison](../20261022-190935-web-ui-run-comparison/spec.md) (implementation #239) depends on this spec. Related specs that must not be repeated: [Deterministic Club Structure Browser Test](../20261022-173520-deterministic-club-structure-test/spec.md) (a different test and project).
- **Out of scope**: Other flaky tests, the platform browser projects, and production values (retry delays and limits of the upload worker are development values that stay as they are).
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
