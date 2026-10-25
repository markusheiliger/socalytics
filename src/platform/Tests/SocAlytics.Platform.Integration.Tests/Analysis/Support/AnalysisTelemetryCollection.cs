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
*/
