# Research: Deterministic Fail-Once Retry of the Simulated Agent Workload

## Decision: Evidence first, then a targeted fix

- **Decision**: Reproduce the failure and identify the race before changing code; remove the evidenced race where it lives.
- **Rationale**: Three verifications saw the same test pass only on a rerun, so the failure is intermittent and the cause is not visible in a single log. A guess-based longer limit is excluded by the requester.
- **Alternatives considered**: Raising limits (excluded by the requester); retrying the test (excluded); skipping the test (excluded).

## Findings from reading the code

- `RunToEndAsync` starts the runner with `CancelAfter(60 s)` and polls with a separate `DateTime.UtcNow.AddSeconds(60)` deadline; the two limits start at different moments, so the runner can be stopped while the poll still waits.
- A retry needs the platform to enqueue a second ready notification through the outbox (poll interval 1 s) after the first attempt's failure report; the runner's `FetchExpiry` is 2 s.
- The runner acknowledges a notification only when `HandleAsync` settles it; a transient report failure leaves it for redelivery after the queue acknowledgement wait.
- The runner and the platform share `Factory.Time`; the `Stall` behavior advances it by 3 s, other behaviors do not.
- `FailRetryableEndsWithAttemptsExhausted` uses the same helper and was reported flaky as well, which points to the shared helper or runner rather than the fail-once behavior itself.

## Refined decision: the load failure is a stalled first claim, not the poll limits

- **Decision**: Add two tasks, T005 and T006, before the verification T003 to address the stalled claim. Superseded by the next section, which gives their final meanings: T005 is the per-fixture work queue and T006 the runner configuration of the two runner-driving test classes.
- **Rationale**: Reading the code found no failure on the unloaded runner, and T002 unified the poll and runner limits. T003 then showed that with every core busy the invocation stays `accepted`/`queued` with 0 attempts, 0 checkpoints, and an empty runner log for the full wait, and that `RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice`, which T002 did not touch, fails in the same runs. The limits are therefore not the cause; the runner never claims the work.
- **Rejected alternative**: Longer waits or a retry of the test (excluded by the requester); accepting the load failure as environmental (SC-001 requires passing under load).
- **Evidence**: T003 attempt 1: solo 20/20 passed, class under load failed about half the time with four burners and 2 of 5 with two; attempt 2: solo 45 of 46 (one 1 ms fixture-startup failure), four burners 4 of 5 failed, two burners 1 of 5 failed; the probe made the test fail on its assertions in both attempts.

## Refined decision: the stalled claim is a notification stolen through the shared queue

- **Decision**: T005 becomes the per-fixture work queue (stream, subject, consumer names) in the test fixture and T006 makes the two runner-driving test classes use it; the investigation T005 formerly asked for is closed, because it produced evidence only and a task without a committed change ends with "the agent made no changes" however well it succeeds.
- **Rationale**: Both attempts of the investigation reached the same cause with probes: with four CPU burners the outbox row was `published`, the stream and consumer showed no pending messages, and the runner's claim answered `404 not-found`, which the runner correctly treats as obsolete and acknowledges. All test classes share one NATS container (assembly fixture), one stream `AGENT_WORK`, and one durable consumer `agent-workload`, while each test has its own database, so under load overlapping classes steal each other's notifications. The runner's behavior is right for production; the race is in the test setup.
- **Rejected alternatives**: serializing the runner-driving tests in one collection (hides the shared state and slows the suite; isolation removes the cause); a retry, longer limits, or republish waits (excluded by the requester); changing the runner to not acknowledge a `404` (alters the documented claim semantics).
- **Not established**: the single 1 ms failure seen in 46 solo runs did not reproduce in any later run; it is not treated as part of this cause, and T003's repeated runs remain the check for it.
