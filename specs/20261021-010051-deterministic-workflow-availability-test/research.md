# Research: Deterministic Pending-Notification Recovery Test

## Decision: Evidence first, then a targeted fix

- **Decision**: Reproduce the failure and identify the race before changing code; remove the evidenced race where it lives.
- **Rationale**: Three verifications saw the same test pass only on a rerun, so the failure is intermittent and not visible in one log. Longer limits and retries are excluded by the requester.
- **Alternatives considered**: Raising limits (excluded); retrying the test (excluded); skipping the test (excluded).

## Findings from reading the code

- The test sleeps a fixed 4 s before asserting that nothing was created while the workflow is unresolvable; it does not observe that the consumer actually handled and postponed the notification.
- `WorkflowDefinitionAvailability.Current` caches the status for `ResolveRecheckSeconds` (1 s in the test) and refreshes on read, so the health check and the consumer can observe the switch at different moments.
- `RecordingsFinalizedConsumerWorker` sleeps 500 ms while the workflow is unresolvable, fetches with a 2 s expiry, and postpones a notification by `NakDelaySeconds` (1 s in the test); the handler resolves the workflow again.
- The class is in the `NatsFaultInjection` collection and shares one NATS container; topology provisioning is enabled by the test, so streams and the durable consumer may hold messages from earlier tests whose recording sets do not exist in this test's database.
- The final assertions read the run count (awaited) and then the receipt count as separate statements.
