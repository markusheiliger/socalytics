# Data Model: Deterministic Retryable Failure of the Simulated Agent Workload

The feature adds, changes, and removes no persisted entity, column, migration, or contract. It observes the existing Agent Orchestration records.

## Observed entities

| Entity | Table | Fields the test reads | Notes |
| ------ | ----- | --------------------- | ----- |
| Agent invocation | `socalytics.agent_invocations` (read through `GET /api/v1/agent-invocations/{id}`) | `state`, `failureReason` | Expected end: `failed` with `attempts-exhausted` |
| Agent attempt | `socalytics.agent_attempts` | `invocation_id`, `attempt_number`, `state`, `end_reason`, `lease_expires_at` | Expected: exactly 3 rows |
| Lineage entry | `socalytics.agent_lineage_entries` | `invocation_id`, `kind` | Evidence only (`claimed`, `failed`, `exhausted`) |

## State transitions under test

```text
queued --claim--> running --failure(retryable, attempts remain)--> queued   (attempt 1, 2)
queued --claim--> running --failure(retryable, none remain)------> failed / attempts-exhausted   (attempt 3)
```

Each attempt row goes `active` to `failed` (`failed-retryable`). The terminal invocation state and the third attempt row commit in the same transaction, so the count is exact once the invocation reads as terminal and the runner has stopped.

## Validation rules

- The attempt count is exactly 3; no wait on "count matches" is allowed.
- A work notification neither adds nor hides an attempt.
