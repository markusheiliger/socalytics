# Research: Deterministic Fact Payload Telemetry Test

## Decisions

### Decision: Isolate the analysis-meter listening tests in one non-parallel xUnit collection

- **Rationale**: The meter is process-wide and its tags may not carry identifiers (FR-004), so a measurement cannot be attributed to a host. xUnit runs a `DisableParallelization` collection only while no other collection runs; no foreign host can then raise a measurement in a listener window, and the assertions stay unchanged. The repository already uses this pattern (`TimingCollection`, `MigratorDiagnosticsRedactionCollection`).
- **Alternatives considered**: a per-host tag (rejected: breaks the documented tag set and cardinality); `AsyncLocal` or `Activity` baggage filtering in the listener (rejected: flow into the in-process host and its workers is not guaranteed, so own measurements could be hidden); tolerant assertions, retries, or sleeps (rejected by the spec); serializing the whole project (rejected by FR-006); a production change (rejected: the cause is not there).

### Decision: The metric-after-response cause is ruled out

- **Rationale**: `CompleteAttempt.cs` calls `AnalysisMetrics.CompletionRejected(...)` before `CloseAsync`, the audit record, and the commit, all before the HTTP response, so the measurement exists when the test asserts. No wait is needed.
- **Alternatives considered**: waiting for the measurement (rejected: not needed and forbidden as a fix).

### Decision: Move other classes only where the defect is confirmed

- **Rationale**: FR-005 fixes only tests with count or absence assertions that a foreign measurement can break. The implementation checks each listener's assertions and records the result in the evidence below.

## Evidence

Filled in by the implementation (FR-001): the build baseline of T001, the code path above, the deliberate concurrent-rejection reproduction with the old and new test results, the per-class defect check, the consecutive run counts of SC-001 and SC-002, and the probe results of SC-003.
