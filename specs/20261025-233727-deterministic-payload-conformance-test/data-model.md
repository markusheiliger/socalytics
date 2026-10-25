# Data Model: Deterministic Published Message Conformance Test

No persisted entity, table, or contract is added or changed. The test only reads existing state.

## Observed state

| Source | Fields read | Use |
| --- | --- | --- |
| `socalytics.outbox_messages` | `id`, `message_type`, `subject`, `state`, `failure_category`, `attempt_count` | The run's message set, defined as the rows with `run_id` = the run's id and `message_type` in (`analysis.ready-work`, `analysis.run-state-changed`) when the rows carry `run_id` (column from migration 0010), with the fallback join through `socalytics.analysis_logical_jobs` only for rows where `run_id` is null (T005 confirms which applies): all rows of the set must be `published`; `pending` means keep waiting; `failed` or `discarded` fails the test naming the row (`discarded` fails only because this test's single run has no supersession or cancellation). Other rows are ignored |
| `socalytics.analysis_logical_jobs` with `analysis_readiness_occurrences` | the run's nodes and logical jobs | The run's expected ready-work set is complete when every node of the run's pinned snapshot has its logical job row (the segment-scoped node's job exists) and every logical job is settled: a job is settled only when it is Ready with its published `analysis.ready-work` row, or it waits on an upstream node result of the same run (a dependency on another node's accepted result); a job that waits on segment materialization (or whose segment association is not yet pinned) is never settled, and the wait does not exit while the run's segment-scoped node has no Ready job; the streams are read only then |
| `SOCALYTICS_ANALYSIS_JOBS`, `SOCALYTICS_EVENTS` | `Nats-Msg-Id`, subject, payload | Messages whose id is in the run's outbox id set, read after the wait condition holds |

## State transitions relied on

An outbox row moves `pending` → `published` (or `failed` / `discarded`) and never back to `pending` for the run's initial publication, so "no row `pending`" is stable once it holds. A message published more than once appears under the same `Nats-Msg-Id` family (`.r<round>` suffix for later rounds) and is accepted.
