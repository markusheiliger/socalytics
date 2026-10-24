# Data Model: Explicit Analysis Run Request

Existing entities (`analysis_runs`, `analysis_notification_receipts`, `analysis_run_supersessions`,
`recording_set_versions`, the security audit trail) are reused. This feature changes one existing table's constraints
and adds one table.

## New: `socalytics.analysis_run_requests` (migration `0032_analysis_explicit_requests.sql`)

The immutable idempotency record of one decided explicit request; one row per request identity.

| Column | Type | Rules |
| ------ | ---- | ----- |
| `idempotency_key` | `text` | Primary key; 1 to 255 visible ASCII characters (same rule as the recording operations) |
| `match_id` | `uuid` | Not null; references `socalytics.match (id)` |
| `requested_by` | `uuid` | Not null; references `socalytics.member_account (id)` |
| `request_id` | `uuid` | Not null, unique; equals the receipt's `message_id`; references `analysis_notification_receipts (message_id)` |
| `outcome` | `text` | Not null; `run-created`, `run-exists`, or `rejected` (same values as the receipt) |
| `rejection_category` | `text` | Null unless `outcome = 'rejected'`; then one of `no-finalized-recording-set`, `not-configured`, `invalid-definition` |
| `recording_set_version_id` | `uuid` | Null only for `no-finalized-recording-set` |
| `run_id` | `uuid` | Not null for `run-created` and `run-exists`, null for `rejected`; references `analysis_runs (id)` |
| `current_run_id` | `uuid` | Null unless the returned run is not the match's current run; references `analysis_runs (id)` |
| `created_at` | `timestamptz` | Not null |

Constraints: check on `outcome`; check that `run_id` is null exactly when `outcome = 'rejected'`; check that
`rejection_category` is not null exactly when `outcome = 'rejected'`. The table is immutable like the receipts:
`reject_immutable_change` trigger on update and delete, and `REVOKE UPDATE, DELETE` from `socalytics_app`. It has no
`version` column, so `PersistedTableClassifications.cs` gets an entry classifying it as an immutable record.

The replayed answer is rebuilt from this row (the run's current state is read live, because the answer's `state`
describes the run at read time, never frozen): replay returns the originally recorded `created` flag, run, and
`currentRunId`, and, for `rejected`, the problem built from `rejection_category`.

## Changed: `socalytics.analysis_notification_receipts`

- `ck_analysis_notification_receipts_type` allows `recordings-finalized` and `analysis-requested`.
- `ck_analysis_notification_receipts_category` allows the existing `schema-invalid`, `lineage-not-found`,
  `lineage-mismatch`, `scope-mismatch`, and additionally `no-finalized-recording-set`, `not-configured`,
  `invalid-definition`.
- No column, index, trigger, or grant change. Rows of explicit requests use `message_id = request_id`,
  `recording_set_version_id` (null only when no finalized set exists), `outcome`, `rejection_category`, `run_id`.

## Request resolution (state transitions)

```text
request(key, member, match)
  authorize (401 / 403 / 404 / 409 season-archived)        -> refuse, nothing written
  validate key                                              -> refuse (400), nothing written
  key recorded?  same member+match -> replay recorded answer, no audit
                 other member or match -> 409 idempotency-key-reused
  current finalized set?  none -> record rejected(no-finalized-recording-set), 409
  resolve default workflow  registry-unavailable -> 503, nothing written
                            not-configured | invalid-definition -> record rejected(category), 409
  lock match runs; run exists for (set, workflow, workflow version)?
      yes -> record run-exists (+ currentRunId when different), 202 created:false
      no  -> shared run creation (run or Failed run, segments, supersession, readiness)
             record run-created, 202 created:true
  one unit of work: run, receipt, request record, audit event commit together
```

## Response shape (`AnalysisRunRequestAcceptedDto`)

`runId`, `created` (bool), `state` (run state), `currentRunId` (nullable; set only when it differs from `runId`),
`recordingSetVersionId`, `workflowId`, `workflowVersion`, and `statusLink` (`/api/v1/analysis/runs/{runId}`).
