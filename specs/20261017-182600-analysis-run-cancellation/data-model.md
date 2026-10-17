# Data Model: Analysis Run Cancellation

All changes are additive and arrive in one forward-only migration
`NNNN_analysis_run_cancellation.sql` under
`src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/`
(number assigned at implementation time). No new table.

## Analysis Run (`socalytics.analysis_runs`)

| Field | Type | Rule |
| --- | --- | --- |
| `cancelled_by_account_id` | `uuid NULL` | References `member_account (id)`; set only when `state = 'Cancelled'` |
| `cancellation_reason` | `text NULL` | Plain text, 1 to 500 characters after trimming, or null; only when `state = 'Cancelled'` |

Constraint: `(cancelled_by_account_id IS NOT NULL) = (state = 'Cancelled')`,
and `cancellation_reason IS NULL OR state = 'Cancelled'`. `terminated_at` is
the cancellation time (existing `ck_analysis_runs_terminated` already requires
it). `version` increments through the existing trigger.

State transitions (`AnalysisStateTransitions`):

```text
Requested ──► Running ──► Completed | PartiallyCompleted | Failed
    │            │
    └────────────┴──► Cancelled   (new; terminal, no outgoing transition)
```

## Workflow Node (`socalytics.analysis_workflow_nodes`)

`state` check gains `Cancelled`. New transitions: `Waiting → Cancelled`,
`Active → Cancelled`. `Completed`, `Failed`, and `Blocked` nodes keep their
state.

## Logical Job (`socalytics.analysis_logical_jobs`)

`state` check gains `Cancelled`. New transitions: `Waiting → Cancelled`,
`Ready → Cancelled`, `Claimed → Cancelled`. `attempts_used` is unchanged;
`failure_reason` stays null. The current readiness occurrence stays as it is;
claims for it are refused because the job is not `Ready`.

## Execution Attempt (`socalytics.analysis_execution_attempts`)

`state` check gains `Cancelled`. New transition: `Active → Cancelled` with
`close_reason = 'run-cancelled'`. The partial unique index on active attempts
is unaffected. A cancelled attempt never becomes `Accepted`.

## Outbox Message (`socalytics.outbox_messages`)

No schema change. In the cancellation transaction, rows with
`message_type = 'analysis.ready-work'`, the run's `run_id`, and
`state IN ('pending', 'failed')` become `discarded`. One new
`analysis.run-state-changed` row is enqueued (`previousState` =
`Requested` or `Running`, `state` = `Cancelled`, no `failureReason`).

## Audit Event

`analysis.run-cancelled`, action `cancel-analysis-run`, through `IAuditTrail`:
actor member, team, match, run, previous state, outcome `cancelled`. The reason
is not part of the audit details.

## Read Models

- `AnalysisRunSummaryView` / `AnalysisRunSummaryDto` and
  `AnalysisRunStatusView` / `AnalysisRunStatusDto`: additive
  `CancelledBy` (`MemberId`, `AccountName` joined from `member_account`) and
  `CancellationReason`, null unless the run is `Cancelled`.
- `AnalysisJobCountsDto`: additive `Cancelled` count.
- Web UI `AnalysisRunSummary` (`src/clients/web/src/platform/models/recordings.ts`):
  additive `version`, `cancelledBy?: { memberId, accountName }`,
  `cancellationReason?`.

## Validation Rules

- Reason: optional; trimmed; empty → null; length ≤ 500; no control characters
  except `\n` and `\r\n`; violations → `400` field `reason`.
- `If-Match`: required (`428`); must equal the current version, or for a
  `Cancelled` run the current version or current − 1 (`412` otherwise).
- State: `Requested` or `Running` to cancel; `Cancelled` → idempotent
  success (version rule above); other terminal states → `409`
  `analysis-run-terminal`.
