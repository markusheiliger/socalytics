# Feature Specification: Deterministic Web UI Keyboard and Club Structure Browser Tests

**Feature Branch**: `20261014-172346-fix-web-ui-keyboard-flake`

**Created**: 2026-10-14

**Input**: User description: "Make `expectAllReachableByKeyboard` in `src/clients/web/tests/e2e/support/keyboard.ts` deterministic (wait for focus to settle after each Tab press, reporting the unreached elements) so the ClubAdmin keyboard test in `simulated/navigation.spec.ts` no longer misses elements 9 and 11, and stabilize `platform/club-structure.spec.ts` where the created team link is sometimes not visible within 5 s; do not weaken any assertion."

**Fix for**: #94 (implementation of `specs/archive/20261013-122132-web-ui-manager-fleet`)

## Problem

The Web UI browser tests of [Web UI Foundation](../20261012-150657-web-ui-foundation/spec.md) fail intermittently and stop the verification of other features:

- `[simulated] navigation.spec.ts` "shell navigation and home view of a ClubAdmin are operable by keyboard" fails with interactive elements at indexes 9 and 11 reported as not reachable with Tab. The helper `expectAllReachableByKeyboard` presses Tab and reads the focused element immediately, with no wait for the focus change to take effect, so a slow runner makes it read a stale focus and skip elements. It also reports only element indexes, which makes a failure hard to diagnose.
- `[platform] club-structure.spec.ts` "creates and activates a season, creates two teams, and edits one" once failed because the created team link was not visible within the default 5 s assertion timeout, while the list refetches after creation on a loaded runner.

Evidence: in the verification of #94, the keyboard test failed in 3 of 3 attempts of T002 and in the last verification of T001 (indexes [9, 11] at `support/keyboard.ts`), and the club-structure test failed once (`getByRole('link', ...)` not found after 5000 ms). The implementation changes no Web UI file, so it cannot fix the tests.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The Web UI browser tests pass reliably (Priority: P1)

A maintainer runs the Web UI browser tests on a slow or loaded machine. The keyboard reachability check follows the real Tab order without depending on timing, and the club structure test waits long enough for the created team to appear, while both still prove exactly what they proved before.

**Why this priority**: The intermittent failures block the verification and merge of other features.

**Independent Test**: Run `npm run test:e2e -- --project simulated` and the club-structure spec repeatedly (also with CPU load) and they pass every time.

**Acceptance Scenarios**:

1. **Given** a page whose interactive elements are all keyboard reachable, **When** the helper tabs through it on a slow machine, **Then** every element is reached and reported as reached.
2. **Given** an element that cannot be reached with Tab, **When** the helper finishes, **Then** the test fails and the message names each unreached element (tag, id, and accessible text), not only its index.
3. **Given** a reached element without a visible focus indicator, **When** the helper finishes, **Then** the test still fails naming that element.
4. **Given** a created team, **When** the list refetches slowly, **Then** the team link is found as soon as it appears within a longer, explicit timeout, and a team that never appears still fails the test.

### Edge Cases

- Tab at the end of the document cycle moves focus to the browser chrome or back to the start; the helper must not hang or loop forever.
- A Tab press whose focus does not change (the last element) must not fail by itself.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: After each Tab press, `expectAllReachableByKeyboard` MUST wait until the focus has settled (the active element changed from before the press, or a short bounded wait shows it does not change) before it evaluates the focused element.
- **FR-002**: The helper MUST fail with a message that lists every unreached interactive element by tag, id, and accessible text.
- **FR-003**: The helper MUST keep checking that every interactive element is reached and that each reached element shows a visible focus indicator; the set of checked elements and the limit on Tab presses stay as before.
- **FR-004**: The created-team link assertions of `club-structure.spec.ts` MUST use an explicit, longer timeout than the default 5 s; the asserted facts stay unchanged.
- **FR-005**: No production code, test of another behavior, threshold, or assertion is changed, weakened, removed, or skipped.

## Success Criteria *(mandatory)*

- **SC-001**: The keyboard navigation tests (all three roles) and every other simulated test that uses the helper pass in 20 consecutive runs of `--project simulated`, also with the CPU loaded.
- **SC-002**: The club-structure platform test passes in repeated runs of `--project platform`.
- **SC-003**: The verification of #94 (`Web UI step failed: browser tests`) passes without these tests failing.
- **SC-004**: A deliberately unreachable element makes the helper fail with its tag, id, and text in the message.

## Assumptions

- The smallest fix is in the test support code and in the one spec: a bounded wait for focus change after each Tab and a named report in `keyboard.ts`, and an explicit timeout on the created-team link assertions in `club-structure.spec.ts`. The cause of the club-structure symptom is assumed to be refetch latency; if a longer timeout does not remove it in repeated runs, the task reports the evidence instead of loosening other assertions.
- Dependencies: the feature owning the tests is [Web UI Foundation](../20261012-150657-web-ui-foundation/spec.md); the implementation this fix unblocks is #94 (`specs/archive/20261013-122132-web-ui-manager-fleet`).
