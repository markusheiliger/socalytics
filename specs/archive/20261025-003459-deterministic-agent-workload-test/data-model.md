# Data Model: Deterministic Simulated Agent Workload Invocation Test

The feature adds no persisted entity, table, or contract. It changes how a test observes existing data.

## Observed entities (unchanged)

| Entity | Fields the test observes | Notes |
| --- | --- | --- |
| Agent invocation (`GET /api/v1/agent-invocations/{id}`) | `state`, `failureReason` (the failure message includes `state` and `failureReason`), `advice`, `agent.version`, `agent.digest`, `agent.artifacts[].kind` | The wait ends on a terminal state; only `succeeded` passes. |
| Outbox message (`socalytics.outbox_messages`) | `state`, `attempt_count`, `next_attempt_at` | Read for the evidence only. |
| Agent attempt (`socalytics.agent_attempts`) | `state`, `lease_expires_at`, `deadline_at` | Read for the evidence only. |
| Fixture NATS topology | stream `AGENT_WORK_<guid>`, consumer `agent-workload-<guid>` | Unique per test; created before the runner starts if the evidence requires it. |

## State transitions (existing, from the architecture)

`accepted/queued` → `claimed/running` → `succeeded` | `failed` | `lifecycle-suppressed`. The test treats the last
three as terminal and fails on any terminal state other than `succeeded`, naming the state and reason.

## Fixture clock

One `TimeProvider` per fixture, shared by the API, its workers, the runner, and the test. Its `GetUtcNow()` and its
timers must agree (no frozen reading with real timers); the concrete form is decided by the evidence.
