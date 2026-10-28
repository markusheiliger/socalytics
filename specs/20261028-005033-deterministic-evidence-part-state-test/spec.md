# Feature Specification: Deterministic Evidence Part State Test

**Feature Branch**: `20261028-005033-deterministic-evidence-part-state-test`

**Created**: 2026-10-28

**Status**: Draft

**Fix for**: #370 (implementation of `specs/20261027-082724-docker-push-digest-status-line`, twin #357)

**Input**: User description: "`EvidencePartStateTests.Never_materialized_segment_is_materializing_then_available_with_a_link_after_the_worker_ran` in SocAlytics.Platform.Integration.Tests/Analysis/Evidence failed and then passed on its one rerun in the verifications of three implementation pull requests (#225, #345, #370), so it is flaky. It is not caused by the stopped spec `specs/20261027-082724-docker-push-digest-status-line`, which does not touch segments or evidence. Find and remove the real cause where it lives (the test, its shared scenario helper `EvidenceScenario`, or the code it covers) without weakening, skipping, or deleting the test or its assertions, without retries, fixed sleeps, or longer timeouts as the fix, and without changing the documented behavior of part states, demand recording, or materialization. The spec `specs/20261027-082724-docker-push-digest-status-line` depends on this spec."

## Clarifications

### Session 2026-10-28

- Q: May the fix change the test, the scenario helper, or platform code? → A: Yes. The cause may live in the test, in `EvidenceScenario` (`Analysis/Evidence/Support/EvidenceScenario.cs`), or in the code the test covers; the fix goes where the cause lives, and no documented behavior may change.
- Q: Are longer limits, fixed sleeps, or retries acceptable? → A: No. A bounded wait on an observable condition, or control of the racing input, is allowed; nothing else.
- Q: Is the suspected cause already established? → A: No, it is a hypothesis to confirm with evidence first (see Problem).

## Problem

The test resolves evidence media for a fact whose segment has never been materialized. It first deletes every segment row, then expects the resolution to report one part in state `materializing` with a positive retry hint and no media link, and to have recorded exactly one demanded segment. It then runs the Segment Service materialization itself through `MaterializeSegmentsAsync` and expects a second resolution to report `available` with a media link. It failed and passed only on its single rerun in the verifications of three implementation pull requests (#225, #345, #370), so the documented behavior is correct when it passes and the failure comes from an uncontrolled race or from state shared between steps.

The suspected cause is that the scenario helper `EvidenceScenario` does not turn the analysis background workers off (`Analysis:Workers:Enabled` defaults to on). The API host under test then runs its real segment materialization worker (poll interval 5 seconds) while the test drives the same segment rows by hand. Run creation has already demanded the required segments, the test deletes all rows, asserts the `materializing` state and the demanded count of 1, and materializes explicitly; the background worker can claim, materialize, or find rows between those steps and so change the observed state or count. The same helper serves the other evidence tests, so the cause may affect them too.

The implementation first confirms or refutes this with evidence, and considers other causes if the evidence shows them: ordering between steps, state shared with the other tests that use the scenario, the host's other background workers (outbox, run creation, lease expiry, monitors) touching segment rows, and external dependencies such as object storage or the media toolchain.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The part state test fails only when behavior is wrong (Priority: P1)

A maintainer running the Integration suite, or the verification of any implementation, sees the test pass whenever a never-materialized segment is reported as `materializing` with one demanded segment and then as `available` with a link after the explicit materialization, however loaded the runner is, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, and the implementation of `specs/20261027-082724-docker-push-digest-status-line` (#370) is held by its report; the person who asked wants the cause removed rather than rerun.

**Independent Test**: Run the test repeatedly, alone and with its class under CPU load, with a poll interval and timing that would let a background worker act; every run passes. Then break the behavior with a probe (for example, make the resolution not record demand, or report `available` before materialization) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** a scenario whose segment rows were deleted, **When** the evidence resolution runs, **Then** exactly one part is reported as `materializing` with a positive retry hint, no media, and exactly one demanded segment, with no other actor able to change the segment rows between the steps.
2. **Given** the explicit `MaterializeSegmentsAsync` call is the only materialization, **When** it has completed, **Then** the next resolution reports `available` with a non-empty media link.
3. **Given** the part states, demand recording, or materialization are broken, **When** the test runs, **Then** it fails on its unchanged assertions.
4. **Given** the cause is in shared code, **When** the other tests of the evidence test classes (part state, resolution, authorization, rate limit, audit and logging) run, **Then** they keep their assertions and pass at least as reliably.

### Edge Cases

- Tests of the evidence classes that rely on background behavior, if any, must keep it explicitly; the change must not silently make them depend on a worker that is off.
- Tests that call `MaterializeSegmentsAsync` or `MaterializeAllAsync` must stay the only source of materialization in their scenario.
- Run creation must still demand the required segments exactly as before; only who materializes them in the test changes.
- No fixed sleep may stand in for an awaited condition, and no limit may be lengthened to hide the race.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (the code, or repeated runs with and without CPU load and the logs of failing runs, for example by making the suspected background worker act in the window and observing the failure) and record it in the Evidence section of `research.md` and in the code comment next to the fix.
- **FR-002**: The cause MUST be removed where it lives (the test, `EvidenceScenario`, or the code it covers) by controlling the racing input, for example by letting the scenario own the worker so that the only materialization is the explicit `MaterializeSegmentsAsync` call, or by waiting on an observable condition; not by retries, fixed sleeps, skipping, or lengthening a limit.
- **FR-003**: Every assertion of the test (`materializing` state, positive retry hint, no media, demanded count of 1, `available` state, non-empty media link) MUST remain, and no assertion, expected value, or test of the evidence test classes MAY be removed, loosened, or skipped.
- **FR-004**: The documented behavior of part states, demand recording, and segment materialization MUST NOT change: any platform change MUST keep the semantics documented in `docs/architecture/`.
- **FR-005**: A change to the shared `EvidenceScenario` MUST keep every other test that uses it passing and deterministic, and MUST NOT make any of them depend on timing.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The test passes in 100% of at least 20 consecutive runs alone and in at least 5 consecutive runs of the evidence test classes under CPU load.
- **SC-002**: The before-fix reproduction is the T003 result recorded in `research.md` (the real segment worker re-enabled through `extraSettings`: `Analysis:Workers:Enabled=true` with a short `Segments:Materialization:PollInterval`); after the fix, a temporary run that re-enables the worker the same way is only a still-reproduces check, and the default scenario (workers off) passes the same probe-free run 20 times; and a probe that breaks demand recording or part states, or reports `available` early, makes the test fail on its unchanged assertions; the probes are removed afterward.
- **SC-003**: The pull request names the evidenced cause and changes only the files the cause lives in, a focused test for a platform fix, no `docs/architecture/` change unless the fix makes a statement false, and this spec folder; no assertion is textually removed.
- **SC-004** (post-merge evidence, observed when #370 retries and not verified by a task of this spec): the verification of #370 reports no flaky evidence test.

## Assumptions

- **Cause**: The test belongs to evidence playback (merged evidence media and Segment Service part state work). The suspected cause is the default-on background segment worker in the scenario's API host; the concrete cause is established during implementation (FR-001).
- **Dependencies**: [Docker Push Digest Status Line](../20261027-082724-docker-push-digest-status-line/spec.md) depends on this spec.
- **Out of scope**: Other flaky tests, the Web UI and End-to-End test projects, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
