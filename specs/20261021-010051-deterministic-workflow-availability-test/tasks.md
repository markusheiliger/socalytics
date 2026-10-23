---

description: "Task list for Deterministic Pending-Notification Recovery Test"
---

# Tasks: Deterministic Pending-Notification Recovery Test

**Input**: Design documents from `specs/20261021-010051-deterministic-workflow-availability-test/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md)

**Tests**: The change is to a test and its support code; verification is running it (SC-001 to SC-003).

**Organization**: One user story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1)
- Every task names its exact file paths

---

## Phase 1: User Story 1 - The pending-notification test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: The test passes reliably under load and still fails when the pending-notification behavior is broken.

**Independent Test**: Run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~WorkflowAvailabilityTests"` repeatedly.

### Establish and remove the cause

- [x] T001 [US1] Reproduce the intermittent failure of `Pending_notifications_wait_while_unresolvable_and_produce_one_run_after_recovery` by running `WorkflowAvailabilityTests` repeatedly alone and with CPU load (also after other `NatsFaultInjection` tests), capturing the consumer and platform logs of failing runs, and identify which race of `plan.md` § Design (or another) produces it; then remove that cause where it lives, in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/WorkflowAvailabilityTests.cs` and, only if the cause lives in shared NATS state, `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Infrastructure/NatsContainerFixture.cs` (or platform code only when the evidence requires it), by controlling the racing input, isolating shared state, or waiting on the awaited condition, with no retry, skip, or longer fixed limit; keep every assertion and the behavior of the other tests that use the shared code; record the evidenced cause (what raced, and the reproduction and log evidence) in a code comment next to the fix and in the commit message, without committing probe code or logs (FR-001 to FR-006)
- [x] T002 [US1] Verify T001 and finish: run the test at least 20 times alone and `WorkflowAvailabilityTests` at least 5 times with CPU load; temporarily create a run while the workflow is unresolvable and, separately, two runs after recovery, confirm the test fails on its assertions each time, then remove the probes; confirm the changed files are only those of T001 and no assertion was removed; confirm `docs/architecture/` needs no change (the plan's Architecture Updates section is `None`; if T001 changed platform code, correct every `docs/architecture/` statement it made false). Record the counts and outcomes of these checks (solo runs, runs under load, and both probes) as one short verification note in the code comment next to the T001 fix in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/WorkflowAvailabilityTests.cs` (the task's only expected change, so it always has one; agents cannot write under `specs/`, so no file in this spec folder is created), and commit any further correction its checks require in the files of T001 (SC-001 to SC-003)

---

## Dependencies & Execution Order

T002 depends on T001 and is a verification step: it always records the evidence as a verification note in the code comment of the T001 fix (never in a file under `specs/`, which agents may not change) and commits a correction in the files of T001 only if its checks require one.

## Notes

- Never skip, loosen, or remove an assertion; never add a retry or a longer fixed limit as the fix

---

## Phase 2: Convergence

- [ ] T003 [US1] Replace the fixed `await Task.Delay(TimeSpan.FromSeconds(4), ct)` in `Pending_notifications_wait_while_unresolvable_and_produce_one_run_after_recovery` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/WorkflowAvailabilityTests.cs` with a bounded wait on an observable sign that the platform has the finalization notification and is holding it back while the workflow is unresolvable (for example the `SOCALYTICS_EVENTS` stream holding the message and the durable finalization consumer reporting it pending or delivered and not acknowledged, read through `NatsJSContext`). Keep the "no receipt, no run, unhealthy check" assertions unchanged and run them only after that state is reached. Add no retry, skip, or longer fixed limit, and change no other test or file, per US1/AC1 and Edge Cases "provably after the consumer has handled the notification" (partial)
- [ ] T004 [US1] After T003, run `Pending_notifications_wait_while_unresolvable_and_produce_one_run_after_recovery` at least 20 times alone and `WorkflowAvailabilityTests` at least 5 times under CPU load, and run both probes again (a run created while the workflow is unresolvable, and two runs after recovery) to confirm that each makes the test fail on its assertions, then remove the probes. Update the code comment next to `ResetEventsStreamAsync` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/WorkflowAvailabilityTests.cs` so that it records the evidenced cause, which is leftover notifications from earlier `NatsFaultInjection` tests in the shared `SOCALYTICS_EVENTS` stream and durable consumer, together with its reproduction and log evidence and the new counts and probe outcomes, per FR-001 and SC-001 to SC-003 (partial)
