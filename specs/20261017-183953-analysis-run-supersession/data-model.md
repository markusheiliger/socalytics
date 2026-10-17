# Data Model: Analysis Run Supersession

All changes arrive in one forward-only migration
`NNNN_analysis_run_supersession.sql` under
`src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/`,
numbered at implementation time as the next free number on an up-to-date
default branch, after the Analysis Run Cancellation feature's
`NNNN_analysis_run_cancellation.sql`. Never edit an applied migration.

## Analysis Run (`socalytics.analysis_runs`)

| Field | Type | Rule |
| --- | --- | --- |
| `recording_set_finalized_at` | `timestamptz NOT NULL` | Finalization time of the run's recording-set version, copied at creation from `RecordingSetLineage.FinalizedAt`; backfilled for existing rows from `recording_set_versions.finalized_at` before `NOT NULL` is set; never changes |
| `cancellation_cause` | `text NULL` | `member` or `superseded`; backfilled to `member` for existing `Cancelled` runs |

Constraints (replacing the cancellation feature's
"`(cancelled_by_account_id IS NOT NULL) = (state = 'Cancelled')`" and
"`cancellation_reason IS NULL OR state = 'Cancelled'`"):

- `ck_analysis_runs_cancellation_cause`: `cancellation_cause IN ('member', 'superseded')` and
  `(cancellation_cause IS NOT NULL) = (state = 'Cancelled')`.
- `ck_analysis_runs_cancelled_by`:
  `(cancelled_by_account_id IS NOT NULL) = coalesce(cancellation_cause = 'member', false)`.
- `ck_analysis_runs_cancellation_reason`:
  `cancellation_reason IS NULL OR cancellation_cause = 'member'` (the
  500-character length check of the cancellation feature stays).
- `uq_analysis_runs_id_match UNIQUE (id, match_id)` (target of the
  supersession foreign keys).

New index `ix_analysis_runs_match_order` on `(match_id,
recording_set_finalized_at DESC, recording_set_version_id DESC, created_at
DESC, id DESC)`.

The version trigger is unchanged; a cancellation by supersession increments
`version` once, exactly like a member cancellation.

### Version order and current run

```text
version key(run) = (recording_set_finalized_at, recording_set_version_id)
run order        = (version key, created_at, id)          -- descending: newest first
current run(m)   = first run of match m in run order
older(a, b)      = version key(a) < version key(b)        -- same version is never older
```

## Run Supersession (`socalytics.analysis_run_supersessions`, new)

| Field | Type | Rule |
| --- | --- | --- |
| `run_id` | `uuid PRIMARY KEY` | The superseded run; at most one row per run (FR-005) |
| `match_id` | `uuid NOT NULL` | Match of both runs |
| `superseding_run_id` | `uuid NOT NULL` | The match's current run at supersession time |
| `superseded_at` | `timestamptz NOT NULL` | Supersession time; the end time of a run cancelled by supersession |
| `previous_state` | `text NOT NULL` | Run state at supersession: `Requested`, `Running`, `Completed`, `PartiallyCompleted`, `Failed`, or `Cancelled` |

Constraints: `FOREIGN KEY (run_id, match_id)` and
`FOREIGN KEY (superseding_run_id, match_id)` reference
`analysis_runs (id, match_id)`; `CHECK (run_id <> superseding_run_id)`;
`previous_state` check over the run states. Index on `superseding_run_id`.
Insert-only; classified `Immutable` in
`src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`.

## Supersession rule (`RunSupersession.ApplyAsync(matchId)`)

Within one unit of work, after `LockMatchRunsAsync(matchId)`:

1. Load the match's runs with their version keys and supersession rows.
2. `C` = current run. If none, stop.
3. For each run `R` without a supersession row with `older(R, C)`, in id order:
   lock `R`; if `R.state` is `Requested` or `Running`, apply
   `RunCancellationEffects` with cause `superseded`, no member, no reason, and
   time `now`, write the audit event `analysis.run-superseded`, and count
   `previous = active`; otherwise count `previous = ended`. Insert the
   supersession row (`superseding_run_id = C`, `superseded_at = now`,
   `previous_state = R.state` before the change).

State transitions added by this feature (all others come from the
cancellation feature):

```text
Requested | Running ──(superseded)──► Cancelled   cause = superseded
any terminal state  ──(superseded)──► unchanged   supersession row only
```

## Outbox, audit, and metrics

- One `analysis.run-state-changed` message per run cancelled by supersession
  (unchanged v1 contract, `previousState` = `Requested` or `Running`, `state`
  = `Cancelled`); none for runs that had ended.
- Pending or failed `analysis.ready-work` rows of a run cancelled by
  supersession become `discarded` (cancellation effects).
- Audit event `analysis.run-superseded`, action `supersede-analysis-run`,
  outcome `Succeeded`, actor `System`, resource `analysis-run`, team id, details
  `runId`, `matchId`, `supersedingRunId`, `previousState`; only for active
  runs.
- `socalytics.analysis.runs_superseded` counter, tag `previous` = `active` or
  `ended`.

## Read Models

- `AnalysisRunSummaryView` / `AnalysisRunSummaryDto` (and through them
  `AnalysisRunStatusView` / `AnalysisRunStatusDto`) gain:
  - `IsCurrent` (`bool`);
  - `SupersededBy` (`RunId`, `RecordingSetVersionId`,
    `RecordingSetFinalizedAt` of the superseding run) or null;
  - `SupersededAt` or null;
  - `CancellationCause` (`member`, `superseded`) or null unless `Cancelled`.
- `CancelledBy` and `CancellationReason` (cancellation feature) stay null for
  cause `superseded`.
- Web UI `AnalysisRunSummary`
  (`src/clients/web/src/platform/models/recordings.ts`): additive `isCurrent`,
  `supersededBy?: { runId, recordingSetVersionId, recordingSetFinalizedAt }`,
  `supersededAt?`, `cancellationCause?`.

## Validation Rules

- Supersession never targets the current run, a run of the current run's
  version, a run of another match, or a run that already has a supersession
  row.
- A run is created already superseded when an existing run of its match has a
  newer version; it never reaches readiness.
- Reconciliation applies the same rule to every match returned by
  `ListMatchesNeedingSupersessionAsync`: matches with a run without a
  supersession row whose version is older than the match's current run.
