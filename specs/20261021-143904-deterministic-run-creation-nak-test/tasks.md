---

description: "Task list for Deterministic Unavailable-Lookup NAK Test"
---

# Tasks: Deterministic Unavailable-Lookup NAK Test

**Input**: Design documents from `specs/20261021-143904-deterministic-run-creation-nak-test/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md)

**Tests**: The change is to a test and its support code; verification is running it (SC-001 to SC-003).

**Organization**: One user story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1)
- Every task names its exact file paths

---

## Phase 1: User Story 1 - The NAK-and-recover test fails only when behavior is wrong (Priority: P1) 🎯 MVP

**Goal**: `UnavailableLookupNaksAndRunIsCreatedAfterRecovery` passes reliably under load and still fails when an unavailable lookup does not postpone the notification without a receipt, or when recovery does not create exactly one run.

**Independent Test**: Run `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~RunCreationTests.UnavailableLookupNaksAndRunIsCreatedAfterRecovery"` repeatedly, alone and with its class under CPU load.

### Establish and remove the cause

- [ ] T001 [US1] Reproduce the intermittent failure of `UnavailableLookupNaksAndRunIsCreatedAfterRecovery` by running it and `RunCreationTests` repeatedly alone and with CPU load (also after other `NatsFaultInjection` tests), capturing the consumer and platform logs of failing runs, and identify which race of `plan.md` § Design (the fixed three-second sleep, shared NATS stream and durable consumer state, redelivery timing after recovery, or the unobservable `SwitchLookup`) or another produces it; then remove that cause where it lives (if the failure cannot be reproduced after the documented runs, record that result, base the cause on the log and timing evidence available, for example the code path showing the sleep or shared consumer racing, state it explicitly as the most likely cause in the code comment, and still apply the deterministic waits), in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs` (for example `SwitchLookup` recording each unavailable call (a count of all unavailable calls, sufficient because the test owns its host and database, so no recording set id is needed before the lookup is first called) with a bounded awaitable wait for a minimum count replacing the `Task.Delay`, a test-specific `Analysis:Notifications:ConsumerName` that the test always uses so it sees only its own notification, with any additional deliver-new or filter policy or fixture purge applied only on evidence, as described in `research.md` § Isolation of shared NATS state, and an exact-one-run wait that is a condition-driven loop with an explicit finite deadline, for example a linked `CancellationTokenSource` with a stated timeout, that fails the test with a clear message on expiry, where the deadline is a failure bound and not the expected duration; the shared `WaitAsync` keeps its behavior for the other tests and only this test uses the new wait or a new helper) and, only if the cause lives in shared NATS state, `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Infrastructure/NatsContainerFixture.cs` (or platform code only when the evidence requires it), by controlling the racing input, isolating shared state, or waiting on the awaited condition, with no retry, skip, or longer fixed limit; keep every assertion (no receipt while unavailable, exactly one run after recovery) and the behavior of the other tests that use the shared code; record the evidenced cause (what raced, and the reproduction and log evidence) in a code comment next to the fix and summarize the reproduction and log evidence in the task result or pull request comment, without leaving probe code or logs in the change (FR-001 to FR-007)
- [ ] T002 [US1] Verify T001 and finish: run the test at least 20 times alone and `RunCreationTests` at least 5 times with CPU load; temporarily acknowledge the notification while the lookup is unavailable and, separately, create two runs after recovery, confirm the test fails on its assertions without hanging each time, then remove the probes; confirm the test's consumer receives no notification of another test; confirm the test contains no fixed-duration sleep standing in for a condition, the changed files are only those of T001 plus this spec folder, and no assertion was removed; confirm `docs/architecture/` needs no change (the plan's Architecture Updates section is `None`; if T001 changed platform code, correct every `docs/architecture/` statement it made false). Any needed correction is made in the files of T001. CPU load may come from a portable generator, for example parallel busy-loop processes started by a shell loop, instead of `stress-ng`; if the environment limits the run counts, they may be reduced to a documented minimum and the reduction is reported (SC-001 to SC-003)

---

## Dependencies & Execution Order

T002 depends on T001 and is a verification step: it makes only a correction its checks require.

## Notes

- Never skip, loosen, or remove an assertion; never add a retry or a longer fixed limit as the fix
- Out of scope: `WorkflowAvailabilityTests` (owned by [Deterministic Pending-Notification Recovery Test](../20261021-010051-deterministic-workflow-availability-test/spec.md)); if both specs change `NatsContainerFixture.cs`, the later one merges the base branch and keeps the earlier behavior
