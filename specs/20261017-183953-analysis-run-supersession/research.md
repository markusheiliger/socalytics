# Research: Analysis Run Supersession

## R1. Version order of recording-set versions

- **Decision**: Copy the recording-set version's finalization time into the
  run at creation (`analysis_runs.recording_set_finalized_at`, from
  `RecordingSetLineage.FinalizedAt` that `HandleRecordingsFinalizedNotification`
  already loads) and order versions by
  `(recording_set_finalized_at, recording_set_version_id)`. The migration
  backfills the column for existing runs from `recording_set_versions`.
- **Rationale**: FR-001 needs one deterministic order. Finalization time is
  the only per-match order Recordings has (`ix_recording_set_versions_match`
  on `(match_id, finalized_at)`); `FinalizeRecordingSetHandler` truncates it to
  microseconds and assigns the set id with `Guid.CreateVersion7()`, so the id
  is a time-ordered tie-break. Copying the time keeps Analysis reads off the
  Recordings tables, as the Recording-Lineage Foundation requires ("downstream
  validation uses a typed Recordings lineage query rather than reading
  Recordings tables directly").
- **Alternatives considered**: a per-match version number in Recordings (new
  member-visible concept, changes another area); joining
  `recording_set_versions` in Analysis queries (crosses the area boundary);
  ordering by run creation time (wrong under out-of-order events, FR-012).

## R2. Supersession record

- **Decision**: A new insert-only table `analysis_run_supersessions` (one row
  per superseded run: superseding run, match, supersession time, the run's
  state at supersession), classified `Immutable` in
  `PersistedTableClassifications`. Composite foreign keys on
  `(run_id, match_id)` and `(superseding_run_id, match_id)` against a new
  `uq_analysis_runs_id_match` enforce that both runs belong to the same match.
- **Rationale**: FR-005 and FR-010 require an immutable record that never
  changes the superseded run's `version`. A column on `analysis_runs` would
  fire the version trigger and break the cancellation feature's idempotency
  rule ("the current version or current − 1 for a `Cancelled` run") for
  member-cancelled runs superseded later.
- **Alternatives considered**: a `superseded_by_run_id` column on
  `analysis_runs` (advances the version); deriving supersession at read time
  only (no immutable record, FR-005 and FR-014 could not be audited or made
  idempotent).

## R3. One rule for creation, late arrival, and reconciliation

- **Decision**: `RunSupersession.ApplyAsync(matchId)` determines the match's
  current run C (the newest run of the latest version, ordered by R1 and then
  `created_at`, `id`) and supersedes, by C, every run of the match that has no
  supersession row and whose version is older than C's version. Run creation
  calls it after inserting the new run; the analysis reconciliation calls it
  for every match that needs it.
- **Rationale**: When the new run is the newest, C is the new run (FR-002).
  When an older version's run arrives late, C is the existing newer run and
  the new run is superseded by it at once (FR-006). Reconciliation of runs
  that predate the feature uses the same current run (clarification). Runs of
  C's own version are never superseded (FR-004), and a run already superseded
  keeps its first record (FR-005).
- **Alternatives considered**: superseding only by the run of the next newer
  version (a chain; harder to explain, and the clarification chose the current
  run).

## R4. Serialization per match

- **Decision**: Run creation and reconciliation take a transaction-scoped
  advisory lock per match,
  `pg_advisory_xact_lock(hashtextextended('analysis-match-runs:' || @matchId, 0))`,
  through a new `IAnalysisRunStore.LockMatchRunsAsync`, before looking up or
  inserting runs; then they lock affected runs with the existing
  `LockRunAsync` in id order.
- **Rationale**: FR-013. Row locks on existing runs cannot serialize the first
  two concurrent creations of a match (nothing to lock yet, so both would miss
  each other under read committed). The pattern already exists in the platform
  (`ClubHierarchyStore`, `MemberAccountStore`, `EmailStores`). The lock order
  is always match lock, then run locks; member cancellation, readiness,
  completion, and stale handling take run locks only and never the match lock,
  so no cycle can form.
- **Alternatives considered**: locking the Club area's `match` row (crosses
  the area boundary); a mutable per-match head table (a new versioned aggregate
  for a lock only); serializable isolation (retries across the whole creation).

## R5. Reusing the cancellation effects

- **Decision**: Factor the in-transaction effects of the Analysis Run
  Cancellation feature's `CancelAnalysisRunHandler` (run, node, job, and
  attempt transitions, discarded ready-work, the `analysis.run-state-changed`
  outbox message, fenced-attempt metrics) into an internal application service
  `RunCancellationEffects` that takes the locked run, the cause, the optional
  member and reason, and the time. The member handler and `RunSupersession`
  both call it; the member handler's behavior stays unchanged and its tests
  keep guarding it.
- **Rationale**: FR-007 requires exactly the member cancellation's effects;
  one implementation keeps fencing, outbox, and worker guards identical.
- **Alternatives considered**: calling the member handler from the system
  (needs a member, authorization, and `If-Match`); duplicating the effects
  (drift).

## R6. Cancellation cause

- **Decision**: Add `analysis_runs.cancellation_cause` (`member` or
  `superseded`), required exactly when the run is `Cancelled`; replace the
  cancellation feature's check
  "`(cancelled_by_account_id IS NOT NULL) = (state = 'Cancelled')`" by
  "a member and an optional reason exist exactly for cause `member`". The
  migration sets `member` for every run that is already `Cancelled`.
- **Rationale**: FR-008 and FR-009: a superseded cancellation has no member
  and no reason, and reads must tell both causes apart.
- **Alternatives considered**: a sentinel system member (fake identity in
  member reads); deriving the cause from the supersession row (a member
  cancellation that is later superseded would be misreported, US4 scenario 3).

## R7. Audit, notification, and metrics

- **Decision**: For an active run, one audit event `analysis.run-superseded`
  (action `supersede-analysis-run`, outcome succeeded, actor override
  `AuditActorKind.System`, details `runId`, `matchId`, `supersedingRunId`,
  `previousState`) through `IAuditTrail.RecordAsync` in the same unit of work,
  and the one `analysis.run-state-changed` message from `RunCancellationEffects`
  with the unchanged v1 contract. For an already ended run, neither
  (clarification). A new counter `socalytics.analysis.runs_superseded` in
  `AnalysisMetrics` with tag `previous` = `active` or `ended`.
- **Rationale**: FR-008, FR-010, FR-014a; the existing audit pattern for
  system-attributed analysis events (`ActorOverride`) and the existing meter.

## R8. Concurrency with member cancellation and readiness

- **Decision**: Both paths lock the run before deciding. Supersession reads
  the locked run's state: `Requested` or `Running` → cancel with cause
  `superseded`; any terminal state, including a member's `Cancelled` →
  supersession row only. A member request that arrives after a supersession
  sees a `Cancelled` run and gets the cancellation feature's idempotent answer
  (the current version or the version before) or `412`; the run keeps cause
  `superseded`.
- **Rationale**: FR-011 and US3 scenario 4; whichever transaction commits
  first decides, and neither writes a second transition.

## R9. Late-arriving older run

- **Decision**: `HandleRecordingsFinalizedNotification` inserts the run and its
  waiting jobs as today, calls `RunSupersession.ApplyAsync`, and calls
  `RunReadinessCoordinator.ApplyAsync` only when the new run is still
  `Requested`. A late run is therefore cancelled (`Requested → Cancelled`)
  before any readiness, so no ready-work outbox row is ever written for it.
  The notification receipt records `run-created` as today.
- **Rationale**: FR-006; one notification for the transition, no published
  work, and duplicate deliveries still answer from the receipt (FR-012).

## R10. Reconciliation

- **Decision**: `ReconcileAnalysisPublicationHandler` first lists, in batches,
  matches with at least one run that has no supersession row and an older
  version than the match's newest run
  (`IRunSupersessionStore.ListMatchesNeedingSupersessionAsync`), and applies
  `RunSupersession` per match in its own unit of work with the match lock;
  only then it resets ready work. `ReconciliationResult` and the
  `analysis.publication-reconciled` audit details gain `runsSuperseded`.
- **Rationale**: FR-014 covers runs created before this feature and any missed
  case; supersession first prevents republishing work of runs about to be
  cancelled. The pass runs on startup, on transport reconnect, and through the
  existing Club Admin reconciliation operation.

## R11. Reads

- **Decision**: `AnalysisRunSummaryView` (and so the status view) gains
  `IsCurrent`, `SupersededBy` (`RunId`, `RecordingSetVersionId`,
  `RecordingSetFinalizedAt`), `SupersededAt`, and `CancellationCause`.
  `AnalysisRunQueries` joins the supersession row and the superseding run and
  computes `IsCurrent` as "this run is the first run of its match in the order
  of R3", supported by a new index on `analysis_runs (match_id,
  recording_set_finalized_at DESC, recording_set_version_id DESC, created_at DESC, id DESC)`.
  DTOs gain `isCurrent`, `supersededBy`, `supersededAt`, and `cancellationCause`
  additively; the existing team-scoped authorization of both reads is
  unchanged.
- **Rationale**: FR-015, FR-016, FR-018; current-run membership is derived,
  so it can never disagree with the supersession rule.

## R12. Web UI

- **Decision**: `AnalysisRunSummary` gains `isCurrent`, `supersededBy?`,
  `supersededAt?`, and `cancellationCause?`, mapped in `analysisRunAdapter`.
  `AnalysisRunStates` adds to each run line a "Current" marker or "Superseded
  by the run of the recording set finalized {time} · {supersession time}",
  shows a superseded cancellation as superseded without a member, and keeps a
  member's cancellation details when a member-cancelled run is superseded. The
  markers are text, not color alone, inside the line's existing status
  semantics. The existing refresh for unfinished runs updates them.
- **Rationale**: FR-019–FR-021; no new screen, adapter method, or storage.

## R13. Tooling

- **Decision**: No new framework, SDK, or tool. Platform tests use the .NET
  SDK, Docker, and SoftHSM2 for end-to-end tests on Linux; the Web UI uses
  Node.js 24, Kiota 1.36.0, and Playwright 1.63.0 Chromium, all provided by
  `.github/actions/environment-setup` and `environment-verify`.
