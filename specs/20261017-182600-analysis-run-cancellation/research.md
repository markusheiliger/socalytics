# Research: Analysis Run Cancellation

## R1. Terminal states for unfinished nodes, jobs, and attempts

- **Decision**: Add `Cancelled` to `WorkflowNodeState`, `LogicalJobState`, and
  `ExecutionAttemptState`, with transitions only from unfinished states
  (`Waiting`/`Active` nodes; `Waiting`/`Ready`/`Claimed` jobs; `Active`
  attempts). Attempts close with `close_reason = 'run-cancelled'`.
- **Rationale**: The clarification requires a terminal state distinct from
  `Failed` and `Blocked` (FR-011). Reusing `Stale` for attempts would run the
  stale path's budget consumption and requeue (`RunReadinessCoordinator.RequeueOrFailAsync`)
  and report the attempt as a lease expiry in `AnalysisMetrics`, which FR-012
  forbids. Every existing gate already refuses anything that is not `Ready`
  (claims) or `Active` (heartbeats, grants, upstream reads, completions), so a
  new terminal state is fenced by construction.
- **Alternatives considered**: `Stale`/`Rejected` attempts (wrong metrics and
  budget semantics); leaving jobs in their state and checking the run in every
  gate (scattered checks, misleading job counts); `Blocked` for nodes (would
  read as an upstream failure).

## R2. Transaction and lock order

- **Decision**: `CancelAnalysisRunHandler` runs in one `IUnitOfWork`:
  authorize, lock the run (`IAnalysisRunStore.LockRunAsync`), evaluate
  idempotency and the version, then lock and transition the run's unfinished
  jobs and active attempts in id order, nodes, outbox rows, audit, and the
  run-state notification, then commit.
- **Rationale**: Readiness, completion, and stale handling already lock the
  run first (`CloseAttemptAsStale`, `RunReadinessCoordinator`), so the run lock
  serializes cancellation against completion and against the run's own
  terminal transition. Whichever transaction commits first wins; the other
  sees a non-`Running` run or a non-`Active` attempt and ends as conflict or
  `attempt-obsolete` (spec edge cases).
- **Alternatives considered**: per-job transactions (partial cancellation
  visible), advisory locks (a second lock order).

## R3. Stopping publication and queued work

- **Decision**: In the cancellation transaction, set every `pending` or
  `failed` `analysis.ready-work` outbox row with the run's `run_id` to
  `discarded`. The publisher only publishes `pending` rows, and
  `ReconcileAnalysisPublication` must skip runs in `Cancelled` and jobs not in
  `Ready`. Messages already in JetStream are not purged individually: the
  Manager's claim is refused as `claim-obsolete` because the job is no longer
  `Ready`, and the Manager acknowledges the delivery (existing
  `AttemptRunEnd.ClaimObsolete` path).
- **Rationale**: Matches "stops publication, revokes queued work" in
  [Run State And Recovery](../../docs/architecture/job-processing.md#run-state-and-recovery)
  while keeping PostgreSQL the workflow truth and delivery at least once;
  the outbox already has the `discarded` state.
- **Alternatives considered**: purging JetStream messages by subject or
  sequence (needs message-to-sequence bookkeeping and broker availability in
  the transaction; correctness still needs the claim check).

## R4. Idempotency and the version precondition

- **Decision**: For a `Cancelled` run, a request whose `If-Match` equals the
  current version or the current version − 1 returns `200` with the run and
  changes nothing; other versions get `412`. For other terminal states,
  `409 analysis-run-terminal` (with the state in the problem details) is
  returned before the version check, so a member learns why.
- **Rationale**: A cancelled run never changes again (FR-007) and the version
  trigger increments by one per update, so current − 1 is exactly the version
  the cancelling request named; no extra column is needed. Two concurrent
  requests with the same version both succeed once, with one audit event.
- **Alternatives considered**: storing the pre-cancellation version (redundant),
  `409` for repeats (not idempotent).

## R5. Authorization and audit

- **Decision**: `AuthorizeTeamResourceAsync(new("analysis-run", runId), TeamPermission.Write)`;
  `Forbidden` → 403, otherwise not granted → 404 (pattern of
  `UpdateMatchHandler`). Record `analysis.run-cancelled` (action
  `cancel-analysis-run`) through `IAuditTrail.RecordAsync` in the same unit of
  work, with member, team, match, run, and outcome; the reason is not copied
  into the audit details. Denials are audited by the authorizer as today.
- **Rationale**: Reuses the team-scope model and the security audit trail of
  the identity foundation; keeps free text out of audit details as the existing
  analysis audit events do.

## R6. Cancelling member in reads

- **Decision**: Store `cancelled_by_account_id` (FK to `member_account`), and
  join `member_account.account_name` when reading the run summary and status;
  DTOs gain `cancelledBy { memberId, accountName }` and `cancellationReason`.
  The member's name shown is the account name, the only member name the
  platform has (spec FR-016).
- **Rationale**: The clarification forbids copying the name into the run.
- **Alternatives considered**: member id only (Viewers cannot resolve names).

## R7. Reason validation

- **Decision**: Optional; trimmed; at most 500 characters after trimming;
  control characters other than line breaks refused as `400` with a field
  violation `reason`; empty after trimming is treated as no reason. Stored as
  `text` and returned verbatim; the Web UI renders it as text.
- **Rationale**: Bounded plain text per FR-006; the 500-character bound is the
  spec's assumption.

## R8. Workers

- **Decision**: `RunReadinessCoordinator` returns early for runs not in
  `Requested`/`Running`; `ExpireStaleAttempts` already scans only `Active`
  attempts; `ReconcileAnalysisPublication` filters to `Ready` jobs of
  `Requested`/`Running` runs; the run-terminal evaluation ignores `Cancelled`
  runs. Tests run each worker after a cancellation and assert no change.
- **Rationale**: FR-013; one guard at each entry point.

## R9. Manager behavior

- **Decision**: No Manager production change. The Manager stops the Analyst
  container (`IAttemptExecution.StopAsync`), cleans up, and deletes the attempt
  directory when the heartbeat ends as `Fenced` or a grant, upstream read, or
  completion is refused as obsolete (`AttemptRunner`, `HeartbeatLoop`,
  `UpstreamStager`, `ResultPublisher`). The platform's existing
  `attempt-obsolete` code is returned for cancelled attempts. Evidence comes
  from `RunCancellationEndToEndTests` with the simulated runtime.
- **Rationale**: FR-014 is satisfied by existing behavior; correctness does not
  depend on it.

## R10. Contracts and API documentation

- **Decision**: The `run-state-changed` v1 contract already allows
  `Cancelled`; add a valid example `cancelled.json` without `failureReason`.
  The OpenAPI document gains `cancelAnalysisRun` and additive fields; refresh
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json` and regenerate the
  Web UI's Kiota client through `npm run build`.

## R11. Web UI

- **Decision**: `AnalysisRunStates` shows a cancel button per
  `Requested`/`Running` run when `canEditTeamMatches(member, teamId)`;
  `CancelRunDialog` (MUI `Dialog`) states consequences, offers an optional
  reason (max 500, counter), and confirms; on success the adapter result
  replaces the run in the `RUNS_KEY` cache and the list refetches; on
  `412`/`409`/`403`/`404` the existing `FailureAlert` shows the mapped message
  and the list refetches. The cancelled line shows the account name, the end
  time, and the reason as text.
- **Rationale**: Follows the match workflow's existing role helper, failure
  mapping, and refresh; the analysis review of the batch sibling stays free of
  run controls.
