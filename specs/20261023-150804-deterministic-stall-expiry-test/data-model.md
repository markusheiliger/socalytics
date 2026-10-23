# Data Model: Deterministic Stall Expiry of the Simulated Agent Workload

No new entities, tables, or migrations. The test observes existing Agent Orchestration state:

- `socalytics.agent_attempts`: `invocation_id`, `attempt_number`, `state` (`active`, then `expired` for the stalled first attempt), `lease_expires_at`, `end_reason` (`lease-expired`).
- `socalytics.agent_invocations`: `state` moves `running` → `queued` (requeue after the expiry) → `running` → `succeeded`.
- `socalytics.agent_lineage_entries`: `kind` `lease-expired` and `checkpoint`.

Only the fake clock (`MutableTimeProvider`, shared by the test, the runner, and the platform host) and, if the evidence requires it, a runner-side observable of the handled stall are touched by the fix.
