# Research: Explicit Analysis Run Request

All Technical Context items are resolved. Each decision names what was chosen, why, and what was rejected.

## R1. Route and verb

- **Decision**: `POST /api/v1/matches/{matchId}/analysis-runs`, operation id `requestAnalysisRun`, tag
  `Analysis runs`, no request body, header `Idempotency-Key`, success `202 Accepted`.
- **Rationale**: the collection `GET /matches/{matchId}/analysis-runs` (`listMatchAnalysisRuns`) already exists; a
  `POST` to the same collection is the natural "ask for a run" and returns the conditional-read link
  `/api/v1/analysis/runs/{runId}` of `getAnalysisRun`.
- **Alternatives**: `POST /analysis/runs` with a match in the body (a body invites per-request workflow choice, which
  FR-001 forbids); `POST /matches/{id}/analysis-requests` (a second noun for the same thing).

## R2. Share run creation instead of duplicating it

- **Decision**: extract the code of `HandleRecordingsFinalizedNotificationHandler.ExecuteAsync` after the lineage
  checks (lock the match's runs, find an existing run by recording-set version, workflow, and workflow version, map
  the snapshot, validate the graph, insert the run or a `Failed` run, plan jobs, request segments, insert jobs, apply
  supersession, start readiness) into `AnalysisRunCreator.CreateOrFindAsync(lineage, definition, ct)` returning
  `(Guid RunId, bool Created)`. The finalization handler keeps its receipt, lineage-mismatch, and workflow-resolution
  handling and calls the creator; the new handler does the same with its own envelope.
- **Rationale**: FR-007 and SC-004 require identical behavior; one code path guarantees it and the existing run
  creation tests keep proving it. The unique constraint on the run identity and the per-match run lock
  (`LockMatchRunsAsync`) already serialize the two paths (FR-009).
- **Alternatives**: publish a synthetic `recordings-finalized` event (rejected: it would re-publish a finalized event
  record that is immutable, one per recording-set version, and would feed every consumer); call the finalization
  handler with a fabricated notification (rejected: its receipt identity is the event id and its lineage checks are
  meaningless for a database-read lineage).

## R3. Current finalized recording-set version

- **Decision**: add `IRecordingSetLookup.GetCurrentForMatchAsync(matchId)` returning the `RecordingSetLineage` of the
  match's latest finalized set (`ORDER BY finalized_at DESC, id DESC LIMIT 1`, then the same member join as
  `GetAsync`); `null` when none.
- **Rationale**: the lineage type already carries members and spans, so the shared creator needs no change and the
  assumption "same members as the notification would" holds by construction. Recording sets are immutable once
  finalized, so reading the lineage before taking the match run lock is safe; a newer set finalized in between is
  handled by the existing supersession ordering by recording-set version.
- **Alternatives**: `IRecordingLineageQueries.GetMatchLineageAsync` (rejected: loads every version and mapping).

## R4. Idempotency and the receipt identity

- **Decision**: a new table `socalytics.analysis_run_requests` holds the idempotency record of each decided request:
  `idempotency_key` (primary key), `match_id`, `requested_by`, `request_id` (the receipt's `message_id`, a fresh
  version 7 UUID), `outcome`, `rejection_category`, `recording_set_version_id`, `run_id`, `current_run_id`, `created`
  flag, `status_code`, `created_at`. A repeat of a key by the same member for the same match returns the recorded
  answer without evaluating anything; the same key from another member or for another match answers the existing
  `idempotency-key-reused` conflict. The receipt row is inserted with `message_id = request_id` in the same unit of
  work. A concurrent duplicate loses on the primary key, rolls back, and replays the winner's record.
- **Rationale**: the receipt table's primary key is a `uuid` message id, which cannot express "same key, other member
  is a conflict"; the existing recording retry table is scoped per match and operation and cannot return the rejected
  refusal without a body. A dedicated table keeps one answer per request identity (FR-011, FR-012, FR-015) and the
  receipts stay immutable and unchanged in shape.
- **Alternatives**: derive the receipt id from a hash of match, member, and key (rejected: cannot detect cross-member or
  cross-match key reuse); reuse `recording_retry_outcomes` (rejected: operation check constraint, per-match key, and
  recording-domain ownership).

## R5. Receipt message type and rejection categories

- **Decision**: message type `analysis-requested`. New rejection categories for this type: `no-finalized-recording-set`,
  `not-configured`, and `invalid-definition` (the workflow resolution categories already defined in
  `WorkflowResolution`). `registry-unavailable` and recording-set read failures are dependency failures and record
  nothing (FR-013). The migration drops and re-adds `ck_analysis_notification_receipts_type` and
  `ck_analysis_notification_receipts_category`; the receipt immutability trigger and revoked grants stay.
- **Rationale**: the operations summary counts `outcome = 'rejected'` and the receipt read does not filter on message
  type, so explicit-request receipts appear in both without query changes (FR-014); only the DTO field values grow.
- **Alternatives**: reuse `recordings-finalized` as the type (rejected: FR-012 and User Story 4 need the paths
  distinguishable).

## R6. Problem results

- **Decision** (codes are stable kebab-case problem codes through `OperationFailure`/`ProblemResults`):
  - unauthenticated `401`; Viewer `403`; no role on the owning team or unknown match `404`;
  - archived season `409 season-archived` (as other match-scoped changes);
  - missing or malformed key `400` validation on `Idempotency-Key`, no receipt;
  - no finalized set `409 no-finalized-recording-set`, receipt `rejected`;
  - unresolvable default workflow `409 workflow-unresolvable` with the resolution category in the problem detail
    extension, receipt `rejected` with the category as `rejection_category`;
  - key reuse across match or member `409 idempotency-key-reused`;
  - Registry or lineage read failure `503 analysis-dependency-unavailable` (retryable), no receipt.
- **Rationale**: reuses `OperationFailure.Conflict`, `Validation`, `Unavailable`, and the existing codes; a replayed
  `rejected` request reconstructs the same problem from the stored category.
- **Alternatives**: `422` for the workflow case (rejected: the existing conventions use `409` for state-dependent
  refusals).

## R7. Existing run, current run, and cancelled or failed runs

- **Decision**: when `FindRunIdAsync` finds the run, the handler returns it with `created: false` and never calls
  supersession or the segment source, regardless of its state. The answer's `currentRunId` is computed with
  `IRunSupersessionStore.ListMatchRunsAsync` and `RecordingSetVersionOrder.SelectCurrent` and is set only when it
  differs from the returned run. The answer's `state` is read from the run record.
- **Rationale**: FR-006 and User Story 2 scenarios 7 and 8; reuses the code behind `isCurrent` in the run reads, so
  "current" has one definition.
- **Alternatives**: a new `is_current` query (rejected: a second definition of current).

## R8. Audit

- **Decision**: one `AuditEvent` per request identity in the unit of work that records the run or existing-run
  decision: event type `analysis.run.request`, action `request-analysis-run`, resource `analysis-run` (the returned
  run), details `matchId`, `recordingSetVersionId`, `outcome` (`run-created` or `run-exists`). No event for refusals
  and none for replays.
- **Rationale**: FR-015; mirrors `RecordingAuditActions`. The event needs no new audit table.

## R9. Reconciliation and operations

- **Decision**: no change to `RunAnalysisReconciliation`, the publication reconciliation, or the consumer. The run is
  created synchronously and publishes through the existing outbox, so the existing reconciliation already covers it
  (FR-016). Operations reads change no query; tests prove the new receipts appear in the summary and list.

## R10. Contract artifacts and clients

- **Decision**: the machine-readable contract is the committed OpenAPI snapshot, refreshed with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
  in three steps so each phase verification passes: T023 after the endpoint, T024 after the problem mappings (a
  no-op when the snapshot test already passes), and T018 as a final confirmation that refreshes only on a difference.
  No new JSON Schema under `contracts/` is added (no new event, message, or Manager/Analyst interface), so the
  Contracts.Tests project needs no change. The Web UI gets no control; its generated Kiota client (gitignored, a build-time output) gains the
  operation when its build regenerates from the snapshot, which the Web UI build and unit tests must keep green.
- **Alternatives**: a `contracts/analysis/run-request` schema (rejected: HTTP request has no body, and the response is
  OpenAPI-described).

## R11. Environment

- **Decision**: no environment spec is needed; no new tool, SDK, or framework. Docker is already required by the
  Integration tests.
