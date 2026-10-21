# Data Model: Deterministic Unavailable-Lookup NAK Test

The feature adds no persisted entity, table, migration, or contract. It observes existing state and adds test-only state.

## Existing state observed

| Entity | Source | Use in the test |
| --- | --- | --- |
| Notification receipt | `socalytics.analysis_notification_receipts` | Must have zero rows while the lookup is unavailable. |
| Analysis run | `socalytics.analysis_runs` | Exactly one row after recovery. |
| Outbox message | `socalytics.outbox_messages` (`matches.recordings-finalized`) | Published once before the consumer is observed. |
| Durable consumer and events stream | NATS JetStream, shared by the collection | Isolated per test (a test-specific consumer name; leftovers removed). |

## Test-only state

- **Unavailable-call record** (in `SwitchLookup`): the number of lookup calls that threw (all calls; the test owns its host and database), with a bounded awaitable wait for a minimum count. Created per test; no persistence.

## State transitions

`published` (outbox) → consumer fetches → lookup unavailable → postponed (no receipt, redelivered after the delay) → switch cleared → lookup succeeds → one run and one receipt.
