# Feature Specification: Remove the Remaining Flakiness of the Club Structure Browser Test

**Feature Branch**: `20261026-014846-fix-club-structure-flake`

**Created**: 2026-10-26

**Status**: Draft

**Fix for**: #314 (implementation of `specs/20261023-074600-analysis-fact-timeline`, twin #253)

**Input**: User description: "Make the Web UI platform browser test `[platform-club-structure] tests/e2e/platform/club-structure.spec.ts:36` ('creates and activates a season; creates two teams; and edits one (SC-002)') deterministic. It failed and then passed on its one rerun in the verifications of implementation pull requests #235, #284, and #314, so it is flaky, and it reports a flaky test that none of those features caused. Find the cause by reproduction and remove it in the test, its support code, or the Web UI or platform code it covers, without changing the documented behavior of the season and team screens or the club structure operations, without weakening, skipping, or deleting the test or any assertion, and without retries or longer timeouts as the fix. The earlier spec `specs/archive/20261022-173520-deterministic-club-structure-test` already hardened this test once and the flake remains, so the remaining cause must be found by reproduction and not assumed. The analysis fact timeline spec depends on this spec."

## Clarifications

### Session 2026-10-26

- Q: May the fix change Web UI or platform code if the cause lives there? → A: Yes, but only where the evidence puts the cause, and without changing the documented behavior of the season and team screens or the club structure operations. A test-only cause is fixed in the test or its support code under `src/clients/web/tests/e2e/`.
- Q: Are longer timeouts, fixed sleeps, or retries acceptable? → A: No. A wait on the awaited condition or control of the racing input is the only acceptable fix; existing explicit `timeout` margins must not be lengthened.
- Q: How does this spec relate to the merged [Deterministic Club Structure Browser Test](../20261022-173520-deterministic-club-structure-test/spec.md) and [Deterministic Web UI Keyboard and Club Structure Browser Tests](../20261014-172346-fix-web-ui-keyboard-flake/spec.md)? → A: It must not repeat them. Both hardened this test and the flake remains, so whatever they already changed is not the cause to remove again; this spec covers the cause that remains after them, found by reproduction. Where their hypotheses were already addressed, the evidence of this spec must show why the failure still occurs.
- Q: Are other tests of the `platform-club-structure` project in scope? → A: Only as protected behavior: a changed shared helper must keep them passing and deterministic.
- Q: What should the implementation do if repeated runs, runs under CPU load, and runs after the other platform projects still cannot reproduce the failure? → A: B) Harden only the candidate causes that the traces and logs of the three recorded failures (#235, #284, #314) support, record that evidence in the code comment, and state that the failure was not reproduced.
- Q: How must the test guarantee that no other season is active when it starts, and that a mid-test failure leaves the club without an active season for the next spec? → A: A) The test (or its support code) establishes the state itself through the club structure operations of the platform before it starts and in a cleanup that also runs after a failure.
- Q: How are 20 repeats of the target obtained on a platform that `session.spec.ts` has already prepared, given that a Playwright run stops its AppHost when it ends? → A: The AppHost is started once and kept running across both commands (`npm run build`, then `SOCALYTICS_APPHOST_MODE=e2e dotnet run --project ../../platform/SocAlytics.Platform.AppHost` in a separate terminal, with the same `SOCALYTICS_E2E_ADMIN_PASSWORD` as the Playwright config, until `http://127.0.0.1:4174` answers); with `CI` unset, Playwright reuses it (`reuseExistingServer`), so the dependency projects run once and then only the target runs with `--no-deps --repeat-each 20`.

## Problem

The test signs in as the seeded Club Admin, creates a season and activates it, selects the season on the Teams screen, creates two teams, renames the second, and checks that the Teams list shows the renamed team and the first team and no longer the second. In the verifications of three implementation pull requests (#235, #284, #314) it failed and passed on its single rerun, so the platform behavior is correct when it passes and the failure comes from timing, order, or shared state that the test does not control. It is unrelated to what those features change, yet it holds back the analysis fact timeline implementation (#314), which needs a reliable verification.

An earlier spec already removed the causes it could evidence, and the failure persists, so the remaining cause is not known. Candidate sources the implementation must examine by reproduction, removing only the ones the evidence supports:

- ordering between the UI and the refetches or cache invalidation of the season and team lists, including a refetch that resolves after the test already acted on a newer state, a form that is remounted or re-initialized from stale data, and the moment at which the heading, the field, and the Teams list show a saved name;
- state shared between specs that run against one platform instance and one Club Admin: a season left active by an earlier spec or a failed cleanup, a changed password, a session or anti-forgery token invalidated by another spec, and the order or parallelism of the platform projects;
- names or other inputs that can collide between attempts, reruns, or workers;
- the platform's own behavior behind the club structure operations under concurrent requests (for example an activation and a team creation that interleave, or a version or concurrency check the UI does not refresh after);
- external dependencies of the platform started in end-to-end mode (database, object store, mail catcher, startup readiness) and the load of the runner.

The implementation first reproduces the failure and identifies which of these (or another cause) produces it, using evidence such as repeated runs of the project alone, under CPU load, and after the other platform projects, and the traces and logs of failing runs, and then removes that cause.

If the failure still cannot be reproduced by any of these, the implementation hardens only the candidate causes that the traces and logs of the three recorded failures (#235, #284, #314) support, records that evidence in the code comment, and states that the failure was not reproduced; it does not harden candidates without such support.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The club structure test fails only when behavior is wrong (Priority: P1)

A maintainer running the Web UI browser tests, or the verification of any implementation pull request, sees the club structure test pass whenever a Club Admin can create and activate a season, create two teams, and edit one, however loaded the runner is and in whatever order the platform projects run, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, which then pass only on a rerun, and the stopped implementation of the analysis fact timeline (#314) needs it reliable; the person who asked wants the cause removed rather than rerun.

**Independent Test**: Run the project repeatedly alone, under CPU load, and after the other platform projects; every run passes. Then break the behavior with a probe (for example a team list that is not refreshed after creation, a season that stays in draft, or a save that does not persist the new name) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** a loaded runner, **When** the test creates and activates a season, selects it, and creates two teams, **Then** each step continues only once the screen shows the awaited state and never because time passed.
2. **Given** a refetch or cache invalidation of the season or team list is still in flight, **When** the test edits the second team and checks the lists, **Then** the saved name is observed on the screen and the absence of the old name is asserted at a point that provably follows the refreshed list.
3. **Given** other platform specs ran before it against the same platform instance and Club Admin, **When** this test starts, **Then** the state it depends on (no other active season, the credentials it signs in with) is the one it expects: the test (or its support code) establishes that state itself through the club structure operations of the platform before it starts, so no other season is active.
4. **Given** the behavior is wrong, **When** the test ends, **Then** its unchanged assertions fail and it does not hang.
5. **Given** the cause is in shared code, **When** the other platform specs that use the changed helper run, **Then** they keep their assertions and behavior and pass at least as reliably.

### Edge Cases

- A rerun of the test or a repeat-each iteration MUST NOT see seasons or teams of a previous attempt as its own.
- A failure in the middle of the test MUST still leave the club without an active season for the next spec: a cleanup through the club structure operations of the platform runs after the test whether it passed or failed.
- If the failure cannot be reproduced, only the candidate causes supported by the traces and logs of the three recorded failures (#235, #284, #314) are hardened, and the evidence and the missing reproduction are recorded in the code comment.
- If the failure is not reproduced and the recorded logs of #235, #284, and #314 are no longer available or support no candidate, the implementation changes no behavior and only updates the code comment in `club-structure.spec.ts` to state that the failure was not reproduced and that no logs were available; the verification runs then confirm the unchanged test.
- The absence assertion for the old team name MUST hold after the refreshed list was shown, not merely before the list was refetched.
- A change to shared support code MUST keep the other platform specs that use it behaviorally unchanged.
- A fix in the Web UI MUST NOT change what a member sees or can do on the season and team screens; a fix in the platform MUST NOT change the club structure operations' documented results, concurrency, or audit behavior.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (repeated runs alone and under CPU load, runs after the other platform projects, and the traces or logs of failing runs) and record it in a code comment next to the fix; the commit message, written by the person or automation that commits, repeats it. The evidence MUST also show why the earlier hardening did not remove it. If the failure cannot be reproduced, the implementation MUST harden only the candidate causes that the traces and logs of the three recorded failures (#235, #284, #314) support, record that evidence in the code comment, and state that the failure was not reproduced.
- **FR-002**: The cause MUST be removed where it lives (the test, its support code, the Season or Team screens, or the platform code it covers) by controlling the racing input, isolating shared state, or waiting on the awaited condition, not by retrying, skipping, adding fixed sleeps, or lengthening a timeout.
- **FR-003**: Every assertion of the test, including the absence of the old team name in the Teams list, MUST remain, and no assertion, expected value, or step MAY be removed, loosened, or skipped.
- **FR-004**: The documented behavior of the season and team screens and the club structure operations in `docs/architecture/` MUST NOT change.
- **FR-005**: A change to shared support code MUST keep every other platform spec that uses it behaviorally unchanged and deterministic.
- **FR-006**: The change MUST NOT repeat the scope of the two earlier specs named in the Clarifications; it addresses the cause that remains after them.
- **FR-007**: The test (or its support code) MUST establish through the club structure operations of the platform that no other season is active before the test starts, and MUST run a cleanup through the same operations after the test, also after a failure, that leaves the club without an active season.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The test passes in 100% of at least 20 consecutive runs of the `platform-club-structure` project alone (on one AppHost that is started once and kept running across both commands and that Playwright reuses with `CI` unset: its dependency projects run once, then only the target is repeated with `--no-deps --repeat-each 20`), in at least 5 consecutive runs under CPU load (one busy-loop process per core for the duration of the runs), and in at least 5 consecutive runs after the platform projects it follows and as part of the whole `platform` chain.
- **SC-002**: Each of three probes (a team list not refreshed after creation, a season that is not activated, and a save that does not persist the new name) makes the test fail on its assertions; the probes are removed afterward.
- **SC-003**: The code comment next to the fix names the evidenced cause, the change touches only the files the cause lives in, and no assertion is textually removed.
- **SC-004** (post-merge evidence, observed when #314 retries and not verified by a task of this spec): the verification of #314 reports no flaky `platform-club-structure` test.

## Assumptions

- **Cause**: The test belongs to the Web UI club structure behavior (merged Web UI foundation and club structure specs under `specs/archive/`). The cause is unknown; every candidate above is a hypothesis to confirm or reject by reproduction (FR-001).
- **Dependencies**: [Analysis Fact Timeline](../../20261023-074600-analysis-fact-timeline/spec.md) (implementation #314, twin #253) depends on this spec. Related specs that must not be repeated: [Deterministic Club Structure Browser Test](../20261022-173520-deterministic-club-structure-test/spec.md) and [Deterministic Web UI Keyboard and Club Structure Browser Tests](../20261014-172346-fix-web-ui-keyboard-flake/spec.md).
- **Out of scope**: Other flaky tests, the simulated browser projects, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
