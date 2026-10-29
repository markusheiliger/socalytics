# Research: Deterministic Retryable Failure of the Simulated Agent Workload

All Technical Context items are resolved. The cause of the flakiness is deliberately not decided here: FR-001 requires it to be established with evidence during implementation. This document records what the code reading shows, the procedure that selects the cause, and the fix shape per cause.

## Code facts

- `FailAgentAttempt` commits, in one unit of work, the attempt's `failed` state, the lineage entry, and either the requeue (invocation `queued`, `ReadyOccurrence + 1`, an outbox message for the work notification) or, once `HasAttemptsRemaining` is false, the invocation's `failed` state with `attempts-exhausted`. The attempt row of the third attempt and the terminal state therefore commit together.
- `ClaimAgentInvocation` creates the active attempt row in the claim transaction, before the runner reads the claim response; it refuses with `attempt-active` while the current attempt is active and its lease is not expired against the platform's fake clock.
- The redelivery is the outbox message published to NATS JetStream (work-queue stream, explicit-ack durable consumer, `MaxDeliver` unlimited). The runner acknowledges a notification only after its report is settled; on a transient failure it leaves the message for redelivery and sleeps its retry delay on the fake clock.
- `RunToEndAsync` reads the attempt count only after the invocation is terminal and the runner has stopped (`run.CancelAsync()`, `await worker`), so no report of this path is in flight at the count; the fail-once and stall specs already made the helper's wait deterministic for their paths.
- The fixture's `LeaseSeconds` is 2, the monitor interval 1 s, and the runner's fetch expiry 2 s with a 200 ms retry delay; the platform clock is a fake clock set once to the real UTC time.

## Candidate causes and how the evidence separates them

| # | Candidate | Evidence that confirms it | Fix where it lives |
| --- | --------- | ------------------------- | ------------------ |
| 1 | A redelivered notification is claimed while the previous attempt's lease is still unexpired against the fake clock (`attempt-active` refusal), or a duplicate notification is claimed after the invocation moved on | Runner log shows a claim answered 409/`attempt-active` or an unacknowledged message redelivered after `AckWait`; attempt count stays below 3 until the monitor expires | Runner or helper: control the racing input (acknowledge only after the settled report, deliver one notification per occurrence) |
| 2 | The test reads state before the third attempt's failure is committed | Last read in the failure message shows `running` or `queued` with 2 attempts, or the terminal read precedes the count | Helper: read the count only after the terminal read, as it already does; confirm no path reads earlier |
| 3 | A report is still in flight at the count | Runner log shows a report after the cancellation | Helper: stop the runner after the terminal read and before the count (existing order) |
| 4 | State shared between attempts: a DPoP proof or nonce, a token, or a fence that one attempt leaves behind | Runner log shows a 401/409 on a checkpoint, lease, or failure report of attempt 2 or 3 | Runner or platform, depending on which side rejects a valid request |
| 5 | Another cause shown by the logs: container startup, scheduling under load, an unobserved event, or a transient iteration failure of the runner that is never retried because its pause runs on the frozen fake clock (the mechanism T017 names) | The captured logs name it; for the frozen-clock pause, the code plus an injected probe evidence the mechanism only | Where the logs place it; for the frozen-clock pause, the clock of that pause in the runner |

## Decision

- **Decision**: Establish the cause with repeated runs and captured evidence, then fix it at its home by awaiting an observable condition or controlling the racing input.
- **Rationale**: The code reading shows no deterministic defect, and the sibling fixes (fail-once, stall) each found a different race, so guessing a fix would repeat past mistakes. The spec forbids retries, sleeps, longer limits, and weakened assertions.
- **Alternatives considered**: Awaiting until the count equals 3 (rejected: hides a missing or duplicate attempt, the count is exact); lengthening the runner's retry delay or fetch expiry (rejected by FR-002); a second rerun policy (rejected: it is the symptom the spec removes); changing platform code speculatively (rejected: only evidence of a platform defect justifies it).

## Evidence procedure

1. Run the test 20 times alone and its class at least 5 times without synthetic CPU load, with a temporary capture (runner log, attempt rows, lineage entries, outbox rows, consumer info) on failure; report the results in the corresponding task summaries. CPU-loaded runs are optional supplemental evidence.
2. If optional CPU-loaded runs are performed and none fails, widen the load and report the rate in the task summary; add a temporary delay at the suspected boundary only to prove a candidate, then remove it.
3. After the fix, repeat step 1 and report the results in the corresponding task summaries. The two probes of SC-002 (stop redelivery after attempt 2; make attempt 3 succeed) are manual local evidence that a person runs outside the automated implementation, each applied temporarily to `SimulatedAgentWorkloadRunner.cs` and reverted; no task runs them and they are not claimed as verified.
