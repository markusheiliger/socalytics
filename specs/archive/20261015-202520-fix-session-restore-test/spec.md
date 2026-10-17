# Feature Specification: Deterministic Web UI Session Restore Retry Test

**Feature Branch**: `20261015-202520-fix-session-restore-test`

**Created**: 2026-10-15

**Input**: User description: "Make the Web UI unit test 'retries restoring when connectivity returns' in `src/clients/web/tests/unit/session/session.test.tsx` deterministic: wrap the assertion on `api.restoreFailure?.kind` in `waitFor` (or await the rejected restore) instead of asserting right after the restore call count; do not change production code or weaken the assertion."

**Fix for**: #114 (implementation of `specs/archive/20261015-092159-docker-runtime-adapter`)

## Problem

The session provider unit test "retries restoring when connectivity returns" of [Web UI Foundation](../20261012-150657-web-ui-foundation/spec.md) is racy. It waits only until the mocked `restore` has been called once, then asserts synchronously that the status is `unknown` and that `api.restoreFailure?.kind` is `unreachable`. The state update caused by the rejected restore promise has not necessarily been applied at that moment, so `restoreFailure` can still be `undefined`.

Evidence: the phase verification of #114 (run 38780484080) failed with 1 failed and 506 passed Web UI tests: `AssertionError` at `session.test.tsx:176`, expected `undefined` to be `'unreachable'`. The implementation changes no file under `src/clients/web`, so it cannot fix the test, and a repair attempt that was forbidden to touch the Web UI made no changes. The flake would stop the final verification before the merge as well.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The session restore retry test passes reliably (Priority: P1)

A maintainer runs the Web UI unit tests on a slow or loaded machine. The retry test waits for the failed restore to be reflected in the session state before asserting it, and still proves the same behavior.

**Why this priority**: The intermittent failure blocks the verification and merge of other features.

**Independent Test**: Run `npm test -- tests/unit/session/session.test.tsx` repeatedly, also with CPU load; it passes every time.

**Acceptance Scenarios**:

1. **Given** a restore that first rejects as unreachable, **When** the provider has called restore once, **Then** the test waits until `api.restoreFailure?.kind` is `unreachable` and the status is `unknown` before it asserts them.
2. **Given** a restore that never reports the failure, **When** the wait times out, **Then** the test fails.

### Edge Cases

- The assertions on the status and on the failure kind keep their expected values; only when they are evaluated changes.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The test "retries restoring when connectivity returns" MUST wait, with `waitFor`, until `api.restoreFailure?.kind` equals `unreachable` before it finishes asserting the failure.
- **FR-002**: The test MUST keep asserting that the status is `unknown` and that the failure kind is `unreachable`, and MUST keep its restore call count wait.
- **FR-003**: No production code, other test, threshold, or assertion is changed, weakened, removed, or skipped.

## Success Criteria *(mandatory)*

- **SC-001**: `tests/unit/session/session.test.tsx` passes in 20 consecutive runs, also with the CPU loaded.
- **SC-002**: The full Web UI unit test run (`npm test` in `src/clients/web`) passes.
- **SC-003**: The phase verification of #114 no longer fails on this test.

## Assumptions

- The smallest fix is in the one test: the failure kind assertion is evaluated inside `waitFor`. The test's remaining behavior stays as it is.
- Dependencies: the feature owning the test is [Web UI Foundation](../20261012-150657-web-ui-foundation/spec.md); the implementation this fix unblocks is #114 (`specs/archive/20261015-092159-docker-runtime-adapter`).
