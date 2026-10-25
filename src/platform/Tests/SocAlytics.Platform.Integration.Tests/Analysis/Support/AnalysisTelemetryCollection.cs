using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AnalysisTelemetryCollection
{
	public const string Name = "AnalysisTelemetry";
}

/*
Evidence record

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
*/
