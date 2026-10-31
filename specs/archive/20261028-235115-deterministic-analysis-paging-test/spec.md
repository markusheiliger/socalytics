# Feature Specification: Deterministic Keyboard Paging Test of the Connected Analysis Review

**Feature Branch**: `20261028-235115-deterministic-analysis-paging-test`

**Created**: 2026-10-28

**Status**: Draft

**Fix for**: #318 (implementation of `specs/archive/20261023-085421-cv-inference-environment`, twin #256)

**Input**: User description: "The Playwright test 'has no violations with facts, and every control is reachable by keyboard with visible focus' in `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts` fails intermittently, also on retry: after 'Apply range' the test activates the 'Next page' button by keyboard and the focus assertion fails for 5 seconds because the button is inactive. Investigate why the button is not focusable after the range-filter reload, make the test wait for the stable, enabled state of the control, or, if the page is at fault, fix the page so the control stays mounted and keeps focus. Do not weaken the assertions or the accessibility checks, add retries or sleeps, or change Web UI behavior beyond what the defect needs. The spec `specs/archive/20261023-085421-cv-inference-environment` depends on this spec."

## Clarifications

### Session 2026-10-28

- Q: Where may the fix live? → A: Where the evidence puts the cause: the test (or its support code under `src/clients/web/tests/e2e/`) or, only if the page is at fault, the connected analysis review (`FactBrowser.tsx`, `useFactPages.ts`). The expected cause is the test acting on a view that is still loading, but the implementation establishes it with evidence first.
- Q: Are retries, fixed sleeps, longer timeouts, or weaker checks acceptable? → A: No. A wait on an observable condition of the page is allowed; nothing else.

## Problem

The accessibility test of the connected analysis review pages through the facts of one stream with the keyboard only. Page size is two and the stream holds five facts. After the member filters the range with "Apply range", the test asserts the address and then `expect(facts).toHaveCount(2)` and activates "Next page". In the failing runs the assertion on "Next page" being focused fails after its 5-second wait because the button is not active. The test also fails on its retry, so the cause is not a one-off.

The review disables "Next page" and "Previous page" while the page has no next or previous page, and while a new selection loads it replaces the facts list and the page navigation with a loading state. The candidate causes the implementation has to separate with evidence are:

- the test acts before the filtered page has loaded, because the count of two facts is already satisfied by the facts of the previous selection or by a moment in which the new selection has not yet replaced them, so "Next page" is still disabled, or absent, or about to be replaced;
- the loading state unmounts the facts list and the page navigation, so a control the test located or focused is detached and the new one does not have the focus;
- the filtered selection really has no next page (for example because of how the simulated platform pages a range), so the test's expectation, not the timing, is wrong;
- the page keeps a stale or disabled control after the reload, which is a defect of the page;
- another cause that the evidence shows.

The implementation first establishes which of these produces the failure, from the code or from reproduced failures, and then removes it where it lives.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The keyboard paging test fails only when the review is wrong (Priority: P1)

A maintainer running the Web UI browser tests, or the verification of any implementation that runs the whole suite, sees the test pass whenever a member can filter the facts by range and then page forward and back by keyboard with visible focus, however loaded the runner is, and fail only when that behavior or its accessibility is broken.

**Why this priority**: The test failed on every attempt of the merge verification of #318, which changes nothing in the Web UI (415 tests passed, 1 failed), so that implementation cannot merge until the test is deterministic.

**Independent Test**: Run the test repeatedly, alone and with its file under CPU load and with the facts request delayed; every run passes. Then break the behavior with a probe (for example, keep "Next page" disabled for the filtered selection, or move the focus away from the control) and confirm the test fails on its unchanged assertions instead of passing.

**Acceptance Scenarios**:

1. **Given** a member who applied a range with the keyboard, **When** the filtered page is slow to load, **Then** the test acts on "Next page" only after the filtered facts and the enabled "Next page" control are present, and then reaches it, activates it, and sees it focused.
2. **Given** the test acts on the control, **When** it is activated by keyboard, **Then** the next page of the filtered facts shows, "Previous page" returns to the first, and the focus and visible-focus assertions run unchanged.
3. **Given** the filtered selection has more than one page, **When** the page reloads for the new range, **Then** the control the member reaches is the one that is enabled and stays attached while it has the focus.
4. **Given** the page, not the test, is at fault, **When** the range changes, **Then** the facts list and navigation stay mounted or the focus is kept as the contract of the review states, and no other behavior of the review changes.

### Edge Cases

- The wait must be on the filtered facts (their content, not only their count) so that the facts of the previous selection never satisfy it.
- The existing accessibility checks (no violations, every control reachable by keyboard, visible focus) run on the same states as before and are neither skipped nor moved to a state in which they check less.
- A filtered selection that really ends after one page must still make the test fail instead of silently passing.
- The other tests of the file, and the other browser projects of the Web UI, keep their assertions and behavior.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (the code, or repeated runs with and without CPU load and with a delayed facts request, with traces of failing runs) and record it in the code comment next to the fix and in the report of the implementing task (the Evidence section of `research.md` holds the analysis made when the spec was written; the implementation changes no file under `specs/`).
- **FR-002**: The cause MUST be removed where it lives. If it lives in the test, the test MUST wait on observable page conditions (the filtered facts present and "Next page" enabled) before it acts on the control; if it lives in the page, the page MUST keep the control mounted and focusable across the range reload, or keep the focus, without other behavior changes.
- **FR-003**: Every assertion of the test and every accessibility check (`expectNoAxeViolations`, `expectAllReachableByKeyboard`, `toBeFocused`, visible focus, keyboard activation) MUST remain on the same states and MUST NOT be removed, loosened, skipped, or moved to check less.
- **FR-004**: The fix MUST NOT use retries, fixed sleeps, longer timeouts, or forced actions as the way to pass.
- **FR-005**: A change to shared test support (`src/clients/web/tests/e2e/support/`) MUST keep every other test that uses it behaviorally unchanged.
- **FR-006**: The fix MUST change no Web UI behavior beyond what the evidenced defect needs, and no platform, contract, or OpenAPI change is needed or allowed.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The test passes in 100% of at least 20 consecutive runs alone and in at least 5 consecutive runs of its file under CPU load, including runs in which the filtered facts request is delayed by several seconds.
- **SC-002**: A probe that keeps "Next page" disabled for the filtered selection and a probe that moves the focus away from the control each make the test fail on an unchanged assertion; the probes are removed afterward.
- **SC-003**: The pull request names the evidenced cause and changes only the files the cause lives in and this spec folder; no assertion or accessibility check is textually removed; the rest of the Web UI browser suite passes as before.
- **SC-004** (post-merge evidence, observed when #318 retries and not verified by a task of this spec): the merge verification of #318 no longer fails on this test.

## Assumptions

- **Cause**: The test belongs to the merged Web UI connected analysis review; the concrete cause is established during implementation (FR-001). The review replaces the facts list and the page navigation with a loading state while a new selection loads, which makes a test that acts on the previous selection's facts racy.
- **Dependencies**: `specs/archive/20261023-085421-cv-inference-environment` (twin #256) depends on this spec and starts only after it is merged.
- **Out of scope**: Other flaky tests, the other browser projects, the platform, and the content or design of the review.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
