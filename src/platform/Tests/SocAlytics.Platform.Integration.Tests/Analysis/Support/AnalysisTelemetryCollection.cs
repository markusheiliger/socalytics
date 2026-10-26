using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AnalysisTelemetryCollection
{
	public const string Name = "AnalysisTelemetry";
}

/*
Evidence record

Build baseline (T001, copied from the outcome T001 recorded when the code-side record did not exist yet): dotnet restore
src/platform/SocAlytics.Platform.slnx succeeded and dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-restore
succeeded with 0 warnings and 0 errors; Docker server version 28.0.4.

Rejection path (T003): CompleteAttempt counts the rejection with AnalysisMetrics.CompletionRejected(ResultRejectionReasons.Code(reason))
before it closes the attempt, records the audit event, and returns the response, so the metric is never late. The meter
SocAlytics.Platform.Analysis is process-wide, and FactPayloadTelemetryTests listens to it without scope while test collections run in
parallel in one process.

Deliberate reproduction with the unchanged test (T003): ConcurrentRejectionProbe (a different collection) raised
CompletionRejected("result-payload-invalid") in a loop with SOCALYTICS_PROBE_CONCURRENT_REJECTION=1 and
--filter "FullyQualifiedName~FactPayloadTelemetryTests|FullyQualifiedName~ConcurrentRejectionProbe". Both theory rows failed:
- invalid: measurements.Count(x => x.Outcome == code) should be 1 but was 212
- undeclared: measurements.Count(x => x.Outcome is not null && x.Outcome != code) should be 0 but was 384

Deliberate reproduction with the changed test (T005): same probe, same environment variable, same filter. The test joined the
non-parallel AnalysisTelemetry collection, so xUnit ran it only after the probe collection had finished (the probe ran its full
60 s first). Result: Passed 3, Failed 0 (both theory rows and the probe). The probe file was deleted afterwards.

Per-class defect check (T006, FR-005): a class is defective when it asserts a count or an absence that a foreign measurement of its
own instrument, raised by a test collection running in parallel, can break. Presence-only assertions are not defective.
- Analysis/Results/ValidatedAcceptanceTests: defective. It listens to socalytics.analysis.completions_rejected and asserts exactly one
  measurement with outcome result-coverage-mismatch, which a parallel rejection with that outcome breaks.
- Analysis/AnalysisTelemetryTests: defective. It listens to the analysis and the messaging meter and asserts that every measurement
  carries only the tag keys capability, message_type, outcome, and failure_category (an absence), which a foreign measurement of
  another analysis instrument (tags such as previous, coverage, or operation) or another test's messaging measurement breaks. Its
  presence assertions alone would not be defective.
- Analysis/RunSupersessionTests: defective. One test asserts exactly four socalytics.analysis.runs_superseded measurements with
  previous=ended and none with previous=active, which a foreign supersession breaks. Its other assertions only check presence.
- Analysis/PartialCoverageReadinessTests: not defective. It listens only to the partial_barriers instruments and asserts only that a
  matching measurement exists (ShouldContain), which a foreign measurement cannot break.
- Registry/MinimumVersionEnforcementTests: defective. It listens to socalytics.analysis.manager_version_refusals and asserts the exact
  list ["claim"], ["queue-credentials"], and an empty list, which a foreign refusal breaks.

Probes (T008, SC-003): each probe changed CompleteAttempt.cs, was run with --filter "FullyQualifiedName~FactPayloadTelemetryTests",
and was restored afterwards (git diff shows no change under SocAlytics.Platform.Application). Both theory rows failed each time.
- Counting the rejection a second time: measurements.Count(x => x.Outcome == code) should be 1 failed (the rejection was counted twice).
- Counting it with a different outcome (instead of the documented code): measurements.Count(x => x.Outcome == code) should be 1 but was 0.
- Adding a payload value as a metric tag (socalytics.analysis.runs_blocked:capability=PAYLOAD-VALUE-MUST-NOT-LEAK):
  measurements.ShouldAllBe(x => !x.Tags.Contains(Sentinel, StringComparison.Ordinal)) failed.

Stability of the named test alone (T009, SC-001): 30 consecutive runs of
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~FactPayloadTelemetryTests",
one after another in a single shell loop that stopped at the first failure. Result: 30 of 30 runs passed (each run: Passed 2,
Failed 0, both theory rows).

Stability together with the other analysis tests (T013, SC-001 and SC-002, runs 1 to 5 of 10): 5 consecutive runs of
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~SocAlytics.Platform.Integration.Tests.Analysis|FullyQualifiedName~MinimumVersionEnforcementTests",
one after another in a single shell loop that stopped at the first failure. The filter covers every class changed by T007 (the four
classes under Analysis/ and Registry/MinimumVersionEnforcementTests). Result: 5 of 5 runs passed (each run: Passed 658,
Failed 0, Skipped 0, Total 658).

Stability together with the other analysis tests (T014, SC-001 and SC-002, runs 6 to 10 of 10): the same command, filter, and loop as
T013. Run 6 passed (Passed 658, Failed 0). The first attempt at run 7 stopped the loop with one failure, not in a telemetry test:
Analysis/Results/FactPayloadPerformanceTests.Validation_at_the_maximum_fact_count_adds_at_most_ten_percent_or_reaches_the_named_bound,
a wall-clock budget test (median with schemas 1978 ms, without 1333 ms, budget 1966 ms; load-sensitive and unrelated to the analysis
telemetry; it was neither changed nor retried inside a run). The loop was then started again at run 7, and runs 7, 8, 9, and 10 passed
(each run: Passed 658, Failed 0, Skipped 0, Total 658). Result: 5 runs (6 to 10) passed with no telemetry assertion failing, one
unrelated timing-budget failure observed once and not reproduced; together with T013 that is 10 runs in total.

Rest of the project (T015): the whole project in one run with
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build. The first run had 2 failures of 1946 tests, neither
a telemetry test: Analysis/SegmentFailureReadinessTests.FailedSegmentMakesTheOptionalJobReadyWithoutMedia ("Sequence contains no
elements" while preparing the failed segment) and Analysis/Evidence/EvidencePartStateTests.Purged_segment_is_rematerializing_beside_available_parts_until_the_worker_ran.
Both passed when run again in isolation (10 of 10 passed with their classes), so they are load-sensitive and unrelated to the analysis
telemetry; they were neither changed nor retried inside a run. The whole project was then run once more in one run. Result: Passed 1945,
Failed 0, Skipped 1 (LargeRecordingUploadTests, skipped by design), Total 1946; every test passes.

Architecture confirmation (T010): the plan's Architecture Updates section says None. docs/architecture/ was searched for statements
about the scheduling of the analysis telemetry tests (xUnit, test collection, AnalysisTelemetry, parallelization, CollectionBehavior,
FactPayloadTelemetry, non-parallel, DisableParallelization). The only test statements are the xUnit v3 tool lists in
platform-implementation.md ("Test And Observability Baseline") and tenancy-and-technology.md; neither describes how integration
tests are scheduled, so no statement became false and no architecture file was changed.

Consolidation (T011, FR-001, SC-004): this record holds the build baseline (T001), the code path of the rejection counting and the
parallel execution of the test project (T003), the deliberate reproduction with the old and the new result (T003 and T005), the
per-class defect check (T006), the probe results (T008), the run counts (T009: 30 runs alone; T013 and T014: 10 grouped runs; T015: one
full-project run), and the architecture confirmation (T010).

SC-005 (the verification of #294 reports no flaky MetricsAndAuditRecordsCarryThePayloadReasonCodeAndNeverAPayloadValue test) is
post-merge evidence: it is observed after merge when #294 retries and is not verified here.
*/
