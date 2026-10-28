# Research: Deterministic Evidence Part State Test

All Technical Context items are resolved. The cause is not adopted as fact here: FR-001 requires it to be established with evidence during implementation and recorded in the Evidence section below.

## Code facts

- `EvidenceScenario.Settings` copies the `Analysis` section of `appsettings.Development.json` and sets the video policy, the segmentation policy, and the `Segments:*` toolchain and working-directory values. It never sets `Analysis:Workers:Enabled`, whose default is `true` (`AnalysisWorkerOptions`).
- The API host registers `SegmentMaterializationWorker` (and the outbox, lease expiry, recordings-finalized, agent monitor, and reconciliation workers). The segment worker runs `RunOnceAsync` every `Segments:Materialization:PollInterval` (default 5 s), and each iteration drains due rows with `MaterializeSegmentHandler.ProcessDueAsync`.
- Run creation (`CreateRunAsync` in the scenario) demands the required segments directly through a handler, so demanded rows exist before the test starts. The test then deletes all rows, resolves evidence (which creates and demands one row in `materializing`), asserts the state, the retry hint, and the demanded count of 1, calls `MaterializeSegmentsAsync` (the same handler, `ProcessDueAsync`), and resolves again.
- The other tests using the scenario (`EvidenceResolutionTests`, `EvidenceAuthorizationTests`, `EvidenceRateLimitTests`, `EvidenceAuditAndLoggingTests`, and the rest of `EvidencePartStateTests`) call `MaterializeAllAsync` or `MaterializeSegmentsAsync` themselves; none starts a worker explicitly.
- Sibling Integration tests that need manual control already set `Analysis:Workers:Enabled` to `false` (for example `QueueDepthTests`, the Agent Orchestration `Manual` helpers, and `AnalysisTestHost` by default).

## Candidate causes and how the evidence separates them

| # | Candidate | Evidence that confirms it | Fix where it lives |
| --- | --------- | ------------------------- | ------------------ |
| 1 | The default-on `SegmentMaterializationWorker` claims the row between the first resolution and the assertions or before the explicit materialization, so the observed state or demanded count differs, or the explicit call finds nothing due or a leased row | Failure shows `available`, a claimed or leased row, or a demanded count other than 1; a temporary probe that runs the worker handler right after `DeleteAllSegmentRowsAsync` reproduces it; with workers off, no failure in many runs | `EvidenceScenario.Settings`: the scenario owns the workers (off by default, overridable by `extraSettings`) |
| 2 | Another host worker (outbox, lease expiry, agent monitors) touches segment rows | The captured `Factory.Logs` show it acting on `materialized_segments` | Where the logs place it; turning off the workers covers it too |
| 3 | Ordering inside the test: the second resolution reads before the explicit materialization has committed | The failure reads `materializing` after `MaterializeSegmentsAsync` returned | The test or helper; the call is awaited, so this is unlikely; confirm it is not the case |
| 4 | State shared with other tests (database, object key prefix, working directory) | Failures only when another test runs in parallel | Scenario isolation (each scenario already has its own database, key prefix, and working directory) |
| 5 | An external dependency: RustFS availability or the `ffmpeg` toolchain under load (`ExecutionTimeout` 1 minute) | The explicit materialization outcome is `failed` or times out | Where the logs place it |

The established cause is recorded in the Evidence section of this file and in the code comment next to the fix (FR-001).

## Decision

- **Decision**: Establish the cause with repeated runs and a temporary probe, then fix it at its home. Expected: `EvidenceScenario.Settings` sets `Analysis:Workers:Enabled` to `false` before the caller's `extraSettings` are applied, so the explicit `MaterializeSegmentsAsync` call is the only materialization and any test that needs the worker overrides the value.
- **Rationale**: The test models the worker's run explicitly; a second, real worker on the same rows is an uncontrolled input. The spec forbids retries, sleeps, longer limits, and weakened assertions, and allows controlling the racing input. Sibling fixes (`deterministic-fail-retryable-test`) show the guess must be confirmed first.
- **Alternatives considered**: Raising `PollInterval` (rejected: a longer limit that only hides the race); asserting with tolerance for other states or counts (rejected: weakens assertions); waiting until a state matches (rejected: the first state must be observed exactly as `materializing`); a retry or rerun policy (rejected: the symptom the spec removes); changing platform worker code (rejected unless the evidence shows a platform defect).

## Evidence procedure

1. Run the test 20 times alone and the evidence classes 5 times with CPU load, capturing on failure the segment rows (state, lease owner, demanded), the outcome list of `MaterializeSegmentsAsync`, and `Factory.Logs`.
2. If no run fails, add a temporary probe that runs a worker iteration between the first resolution and the demanded-count query (and, separately, between the count and `MaterializeSegmentsAsync`) to show that the worker's action reproduces the observed failure; the expected failure signature is a leased or claimed row, an explicit call that finds nothing due, and a second resolution that reads `materializing`; record the rate.
3. After the fix, repeat step 1 and run the probes of SC-002: the worker probe no longer changes the outcome, and probes that break demand recording or make the resolution report `available` early make the test fail on its unchanged assertions. Remove every probe.

## Evidence

To be recorded by the implementation (FR-001): the runs, the failure captures or probe results, and the confirmed cause.
