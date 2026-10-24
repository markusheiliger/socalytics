# Research: Deterministic Finalized Recording Set Run Test

## Findings from the code

- `PublishOutboxBatch.HandleAsync` opens one unit of work, claims due rows (`FOR UPDATE SKIP LOCKED`), and for each row publishes to the broker and then calls `MarkPublishedAsync`. `scope.CommitAsync` runs once after the whole batch, so the `published` state becomes visible to other connections only after the broker already has the message. The row stays locked and `pending` for readers meanwhile.
- `RecordingsFinalizedConsumerWorker` fetches with a 200 ms loop and 2 s fetch expiry, so it can create the run and its receipt before the publisher's commit. The test's `WaitAsync` returns as soon as `analysis_runs` has one row and immediately counts `published` rows, which can still be 0.
- The duplicate proof publishes the payload twice with fresh `Nats-Msg-Id` values (so the broker does not drop them), then sleeps 4 s and counts. A slow consumer that has not handled them yet makes the count a false pass; it never fails for a correct system except through the `published` assertion above.
- Consumer and stream names come from `AnalysisNotificationOptions.ConsumerName` and `NatsOptions.EventsStream`; `FinalizationSetup` provisions the topology with `Messaging:Nats:ProvisionTopology`, but the class receives one shared `NatsContainerFixture`, so whether the stream and the durable consumer are per test or shared, and therefore whether leftover state between tests exists, is unverified until T004.

## Decision 1: Wait for the `published` state

- **Decision**: Replace the immediate `published` count with `WaitAsync` on that count, then assert it with the unchanged `ShouldBe(1)`.
- **Rationale**: The state is eventually consistent by design (commit after batch); waiting on it observes the real condition and keeps the assertion.
- **Alternatives**: Commit per row in `PublishOutboxBatch` (changes production transaction behavior and the documented at-least-once semantics, rejected by FR-006); sleep (rejected by FR-002).

## Decision 2: Wait for both duplicates to be handled

- **Decision**: After the two `PublishAsync` calls, wait until the durable consumer (`GetConsumerAsync(EventsStream, ConsumerName)`, refreshed per poll) reports `NumPending == 0`, `NumAckPending == 0`, and `AckFloor.ConsumerSeq` equal to `Delivered.ConsumerSeq`, with the stream's last sequence (from the two publish acknowledgements, `ack.Seq`) at or below `AckFloor.StreamSeq`. Only then count runs and receipts with the unchanged assertions.
- **Rationale**: The consumer acknowledges a message only after the handler returned, so an acknowledged duplicate is observably handled, and the broker's publish acknowledgement sequence identifies exactly the duplicates, independent of timing. A nak (redelivery) leaves `NumAckPending` or `NumPending` above zero, so the wait cannot pass early.
- **Alternatives**: Poll receipts by message identifier (duplicates add no receipt, so there is nothing to observe); a fixed sleep (rejected); a test-side consumer counter (needs production changes).

## Decision 3: Isolation

- **Decision**: Isolation between tests is unverified until T004. T004 confirms whether the stream and the durable consumer are per test or shared through `NatsContainerFixture` (the `NatsFaultInjection` collection serializes the class); only if state is shared or leftover is evidenced, add isolation or a consumer baseline in `RunCreationTests.cs`.
- **Rationale**: Spec edge cases "no state left over" and "does not depend on test order"; no change unless evidenced.

## Evidence

To be filled during implementation (FR-001, SC-001, SC-003): repeated-run results alone and under CPU load, the deliberate widening (a test-side delay of the publisher's commit under which the old test fails and the new one passes), and the two probes that make the test fail on its assertions.
