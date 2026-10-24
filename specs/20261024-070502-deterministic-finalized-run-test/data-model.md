# Data Model: Deterministic Finalized Recording Set Run Test

No persisted or contract entity is added or changed. The test observes existing state only:

| Observed state | Source | Condition the test waits for |
| --- | --- | --- |
| Analysis run count | `socalytics.analysis_runs` | exactly 1 after finalization |
| Outbox message state | `socalytics.outbox_messages` (`message_type = 'matches.recordings-finalized'`) | `state = 'published'` count of 1, reached by a bounded wait |
| Duplicate handling | durable consumer `analysis-scheduler-recordings-finalized` on `SOCALYTICS_EVENTS` | nothing pending or unacknowledged and the ack floor at the duplicates' stream sequence |
| Notification receipts | `socalytics.analysis_notification_receipts` | exactly 1 after the consumer settled |
