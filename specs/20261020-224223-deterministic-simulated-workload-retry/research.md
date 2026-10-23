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

- **Decision**: Add T005 (establish the cause of the stalled claim) and T006 (remove it) before the verification T003.
- **Rationale**: Reading the code found no failure on the unloaded runner, and T002 unified the poll and runner limits. T003 then showed that with every core busy the invocation stays `accepted`/`queued` with 0 attempts, 0 checkpoints, and an empty runner log for the full wait, and that `RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice`, which T002 did not touch, fails in the same runs. The limits are therefore not the cause; the runner never claims the work.
- **Rejected alternative**: Longer waits or a retry of the test (excluded by the requester); accepting the load failure as environmental (SC-001 requires passing under load).
- **Evidence**: T003 attempt 1: solo 20/20 passed, class under load failed about half the time with four burners and 2 of 5 with two; attempt 2: solo 45 of 46 (one 1 ms fixture-startup failure), four burners 4 of 5 failed, two burners 1 of 5 failed; the probe made the test fail on its assertions in both attempts.
