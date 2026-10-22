# Feature Specification: Deterministic Club Structure Browser Test

**Feature Branch**: `20261022-173520-deterministic-club-structure-test`

**Created**: 2026-10-22

**Status**: Draft

**Fix for**: #225 (implementation of `specs/20261017-224326-evidence-clip-access`, twin #162)

**Input**: User description: "The Web UI platform browser test `[platform-club-structure] tests/e2e/platform/club-structure.spec.ts` ('creates and activates a season; creates two teams; and edits one (SC-002)') failed and then passed on its one rerun in the verifications of three implementation pull requests (#214, #221, #225), so it is flaky. Find and remove the cause of the flakiness (timing and ordering between the UI and refetches or cache invalidation of the season and team lists, shared state between specs that run against one platform instance and one Club Admin, external dependencies) in the test, its support code, or the Web UI or platform code it covers, without weakening, skipping, or deleting the test or any of its assertions, and without adding retries or longer timeouts as the fix. The evidence clip access spec (`specs/20261017-224326-evidence-clip-access`) depends on this spec."

## Clarifications

### Session 2026-10-22

- Q: May the fix change Web UI or platform code if the cause lives there? → A: Yes, but only where the evidence puts the cause, and without changing the documented behavior of the season and team screens or the club structure operations. Test-only causes are fixed in the test or its support code.
- Q: Are longer timeouts or retries acceptable? → A: No. The requester excludes them; the test must wait on the awaited condition or control the racing input. Removing the existing explicit `timeout` margins is not required, and they must not be lengthened.
- Q: Does this repeat [Deterministic Web UI Keyboard and Club Structure Browser Tests](../archive/20261014-172346-fix-web-ui-keyboard-flake/spec.md)? → A: No. That spec hardened the test once (waiting for focus, the unmounted team form); this spec covers the cause that remains after it, which must be found by reproduction and not assumed.
- Q: Is the keyboard support of the simulated projects in scope? → A: No, only what the platform club structure test uses.

## Problem

The test signs in as the seeded Club Admin, creates a season and activates it, selects the season on the Teams screen, creates two teams, renames the second, and checks that the Teams list shows the renamed and the first team and no longer the second. It fails in the verification of implementation pull requests and then passes on its single rerun, so the failure depends on timing, order, or state and not on the behavior under test.

The path of the test contains several candidate sources of nondeterminism that the implementation must examine and remove where they cause the failure:

- the Teams list refetch after a team creation and the Season selector's own refetch or cache invalidation, which can resolve in a different order than the test assumes;
- the team form that is unmounted while the team refetches, and the saved name that the heading, the field, and the Teams list show at different moments;
- the single active season per club: a season left active by an earlier spec, a failed `afterEach` archival, or a parallel spec makes the activation or the selector option differ;
- the Club Admin account shared by every platform spec against one platform instance: the password that the session spec changes, specs running in parallel or in a different order, and a session or anti-forgery token that another spec invalidates;
- names derived from a time value (`Date.now()` in base 36) that can collide between a rerun or concurrent workers;
- external dependencies of the platform started in end-to-end mode (database, object store, mail catcher, startup readiness) that may not be ready when the first sign-in happens.

The implementation first reproduces the failure and identifies which of these (or another cause) produces it, using evidence such as repeated runs of the `platform-club-structure` project, runs under CPU load, runs after the other platform projects, and traces of the failing runs, and then removes that cause.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The club structure browser test fails only when behavior is wrong (Priority: P1)

A maintainer running the Web UI browser tests, or the verification of any implementation pull request, sees the club structure test pass whenever a Club Admin can create and activate a season, create two teams, and edit one, however loaded the runner is and in whatever order the platform projects run, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, which then pass only on a rerun and report a flaky test that none of those features caused; the stopped implementation of #225 needs it reliable.

**Independent Test**: Run the project repeatedly alone, under CPU load, and after the other platform projects; every run passes. Then break the behavior with a probe (for example a team list that is not refreshed after creation, a season that stays in draft, or a save that does not persist) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** a loaded runner, **When** a season is created, activated, and selected on the Teams screen, **Then** the test continues only once the screen shows the awaited state, and the creation of each team is observed through the Teams list and not through elapsed time.
2. **Given** the team form is unmounted while the team refetches, **When** the test edits the team, **Then** it fills and saves only the form that holds the team's current name, and the saved name is observed in the heading, the field, and the Teams list.
3. **Given** the platform projects it depends on (`platform-session`, `platform-navigation`) ran before it as part of the whole `platform` chain, **When** this test starts, **Then** the club has no leftover active season and the Club Admin's credentials are the ones the test signs in with, or the test establishes that state itself.
4. **Given** the behavior is wrong, **When** the test ends, **Then** the unchanged assertions fail and the test does not hang.

### Edge Cases

- A rerun of the same test, or a second worker, MUST NOT reuse a season or team name of a previous attempt in a way that makes the new attempt see the old data.
- A failure in the middle of the test MUST still leave the club without an active season for the next spec.
- The absence assertion for the old team name MUST hold at a point that is provably after the refreshed list was shown, not merely before the list was refetched.
- A change to shared support code MUST keep the other platform specs behaviorally unchanged.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (repeated runs with and without CPU load, runs after the other platform projects, and the traces of the failing runs) and record it in a code comment next to the fix and in the commit message.
- **FR-002**: The cause MUST be removed where it lives (the test, its shared support, the Season or Team screens, or the platform) by controlling the racing input, isolating shared state, or waiting on the awaited condition, not by retrying the test, skipping it, or lengthening a timeout.
- **FR-003**: Every assertion of the test, including the absence of the old team name in the Teams list, MUST remain, and no assertion, expected value, or step MAY be removed, loosened, or skipped.
- **FR-004**: Production behavior MUST NOT be changed: any Web UI or platform change MUST keep the documented behavior of the season and team screens and the club structure operations in `docs/architecture/`.
- **FR-005**: A change to shared support code MUST keep the other platform specs that use it behaviorally unchanged.
- **FR-006**: The change MUST NOT repeat the scope of [Deterministic Web UI Keyboard and Club Structure Browser Tests](../archive/20261014-172346-fix-web-ui-keyboard-flake/spec.md); it addresses the cause that remains after that spec.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The test passes in 100% of at least 20 consecutive runs of the `platform-club-structure` project alone, in at least 5 consecutive runs under CPU load, and in at least 5 consecutive runs after the platform projects it depends on (`platform-session`, `platform-navigation`) and as part of the whole `platform` chain.
- **SC-002**: Each of three probes (a team list not refreshed after creation, a season that is not activated, and a save that does not persist the new name) makes the test fail on its assertions; the probes are removed afterward.
- **SC-003**: The code comment and commit message name the evidenced cause, and the change touches only the files the cause lives in plus this spec folder; no assertion is textually removed.
- **SC-004** (post-merge evidence, observed when #225 retries and not verified by a task of this spec): the verification of #225 reports no flaky `platform-club-structure` test.

## Assumptions

- **Cause**: The test belongs to the Web UI club structure behavior (the merged Web UI foundation and club structure specs under `specs/archive/`); it races refetches or shared state between specs against one platform instance and one Club Admin. The concrete cause is established during implementation (FR-001).
- **Dependencies**: [Evidence Clip Access](../20261017-224326-evidence-clip-access/spec.md) (implementation #225, twin #162) depends on this spec. Related specs that must not be repeated: [Deterministic Web UI Keyboard and Club Structure Browser Tests](../archive/20261014-172346-fix-web-ui-keyboard-flake/spec.md).
- **Out of scope**: Other flaky tests, the simulated browser projects, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
