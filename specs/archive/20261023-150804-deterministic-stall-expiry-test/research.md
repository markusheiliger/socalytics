# Research: Deterministic Stall Expiry of the Simulated Agent Workload

## Decision: Evidence first, then a targeted fix

- **Decision**: Reproduce the failure and identify the race before changing code; remove the evidenced race where it lives.
- **Rationale**: Three verifications saw the test pass only on a rerun, so the failure is intermittent and the cause is not visible in a single log. Longer limits, fixed sleeps, and retries are excluded by the requester.
- **Alternatives considered**: Raising limits (excluded); retrying or skipping the test (excluded); repeating the shared-helper change of #209 (it left the Stall clock step unchanged on purpose).

## Findings from reading the code

- `RunToEndAsync` polls every 250 ms and, when exactly one `agent_attempts` row exists, calls `Time.Advance(3 s)` once. The row is committed by the claim operation, before the runner has read the claim response or decided to stall, and nothing observes that the stall is in effect.
- The runner stalls by returning from `HandleAsync` right after the claim of attempt 1 (no lease renewal, no report) and acknowledges the notification; it shares `Factory.Time` (a `MutableTimeProvider` whose `Advance` is not synchronized) with the platform, and uses it for DPoP `iat` and for its delays.
- The lease is 2 s (`AgentOrchestration:Execution:LeaseSeconds`) and `MaxAttemptSeconds` is 10 s; the monitor runs every 1 s of real time but reads the fake clock, so a blind 3 s jump can expire the attempt at any monitor tick, and an overshoot can interact with the second attempt's lease and deadline.
- `MonitorAgentAttemptsHandler.ExpireAsync` is idempotent under the invocation and attempt locks (an attempt not `active` is skipped), so the platform prevents a double expiry; the test and runner are therefore the first suspects.
- A requeue enqueues a ready notification through the outbox (real-time publication interval), so the second attempt starts after an unpredictable real delay; the helper's deadline (3 minutes) and the runner's limit (4 minutes) are independent.
- The test reads counts only after the runner is cancelled and awaited.

## Candidate causes to confirm or exclude during implementation

1. The jump precedes the stall being in effect (claim response not yet handled).
2. The jump is observed between a DPoP proof's `iat` and its validation, or between token issue and use.
3. The jump lands before the first attempt's lease is established, or overshoots into the second attempt's lease or deadline.
4. A late or duplicate ready notification makes the runner claim or settle out of order.
5. Another cause shown by the logs (timing, ordering, shared state, an external dependency).

## Observable conditions available for a bounded wait

- `agent_attempts.state` and `lease_expires_at` per attempt (the test already reads this table with `ScalarAsync`).
- The `lease-expired` lineage entry and the attempt's `end_reason`.
- A runner-side event when a stalled claim was handled (added only if the evidence needs it).
