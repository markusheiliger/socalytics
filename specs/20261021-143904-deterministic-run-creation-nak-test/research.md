# Research: Deterministic Unavailable-Lookup NAK Test

No `NEEDS CLARIFICATION` remains. The concrete cause is established during implementation (spec FR-001); this document records what the code shows and the decisions that follow.

## Observed facts (code)

- The test (`RunCreationTests.UnavailableLookupNaksAndRunIsCreatedAfterRecovery`) sleeps three seconds, asserts no receipt, clears the switch, then polls 120 times at 500 ms for one run.
- `WorkerSettings()` sets `Analysis:Notifications:NakDelaySeconds` to `1` and provisions the NATS topology.
- `RecordingsFinalizedConsumerWorker` fetches up to 10 messages with a 2 s expiry from the durable consumer named by `Analysis:Notifications:ConsumerName` (default `analysis-scheduler-recordings-finalized`) on the events stream, with `MaxDeliver = -1`, and postpones (NAK with delay) on `DependencyUnavailable`, `WorkflowUnresolvable`, or an exception.
- `NatsTopology.EnsureAsync` creates or updates the streams and that consumer; the NATS container is an assembly fixture shared by every test of the `NatsFaultInjection` collection, so stream content and the durable consumer outlive a test's host.
- `SwitchLookup` only throws while the flag is set; nothing counts or exposes its calls.

## Decisions

### Signal for "lookup attempted while unavailable"

- **Decision**: `SwitchLookup` counts all calls that threw and exposes a bounded awaitable wait for a minimum count. The test waits for at least two unavailable calls before asserting no receipt. Counting all calls is sufficient because the test owns its host and database; the test has no recording set id before the lookup is first called (it is known only after `FinalizeAsync`), so a per-id counter is not used. A per-id count may be used only if the test reads its recording set id after finalization, for example from the database, and passes it to the wait.
- **Rationale**: One call proves the consumer reached the lookup; the second proves the postponed notification was redelivered, so the "no receipt" assertion is made at a point provably after at least one postponement. It replaces the sleep with the awaited condition.
- **Alternatives considered**: Raising the sleep (a longer fixed limit, forbidden); reading the consumer's redelivery count from NATS (works but couples the test to JetStream internals and is not scoped to this test's notification); a log scrape (fragile).

### Isolation of shared NATS state

- **Decision**: Give the test a test-specific consumer name through `Analysis:Notifications:ConsumerName`, and isolate the notifications by an evidenced mechanism: T001 first proves with evidence that leftover messages are the cause, and applies isolation only then. The mechanism is a test-specific consumer with a deliver-new or filtered policy (T001 first establishes from `NatsTopology.EnsureAsync` what a new durable consumer delivers, all stream messages or only new ones), or, if that cannot isolate, one fixture method used only by this test that purges the leftover notifications after the host has provisioned the topology (`Messaging:Nats:ProvisionTopology`) and before the finalization event is published, because `NatsContainerFixture` has no purge today. Keep the stream names unchanged. The mechanism MUST NOT affect the other tests of the collection, including the `NatsFaultInjection` tests that share the stream.
- **Rationale**: The durable consumer and stream are shared; messages left by earlier tests refer to recording sets absent from this test's database and consume the consumer's fetch batch and time or add receipts.
- **Alternatives considered**: A new NATS container per test (slow and changes every test of the collection); filtering messages in production code (changes consumer behavior).

### Outcome after recovery

- **Decision**: Await the exact-one-run state with a condition-driven loop with an explicit finite deadline (for example a linked `CancellationTokenSource` with a stated timeout, because the test's cancellation token is not guaranteed to be finite) that fails the test with a clear message on expiry, instead of a fixed number of polls, then read the run count and receipt count once. The deadline is a failure bound, not the expected duration. The existing shared `WaitAsync` (120 polls of 500 ms) keeps its behavior for the other tests of `RunCreationTests`; only this test uses the new wait or a new helper.
- **Rationale**: The redelivery after recovery arrives within about the postponement delay plus the fetch expiry; the wait must follow the outcome, remain bounded, and keep `== 1` exact.
- **Alternatives considered**: More polls or a longer interval (a longer fixed limit, forbidden); accepting at least one run (weakens the assertion).

### Production code

- **Decision**: Not changed unless the repeated runs put the cause there; then only without weakening postponement, redelivery, idempotency, and single-run semantics.
- **Rationale**: The consumer's behavior matches `docs/architecture/`; the race is between the test and real time and shared state.

### Scope boundaries

- `WorkflowAvailabilityTests.Pending_notifications_wait_while_unresolvable_and_produce_one_run_after_recovery` belongs to [Deterministic Pending-Notification Recovery Test](../20261021-010051-deterministic-workflow-availability-test/spec.md). If both specs change `NatsContainerFixture.cs`, the later one merges the base branch and keeps the earlier behavior.
