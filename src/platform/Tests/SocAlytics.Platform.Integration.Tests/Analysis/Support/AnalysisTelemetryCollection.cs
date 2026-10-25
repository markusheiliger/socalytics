using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AnalysisTelemetryCollection
{
	public const string Name = "AnalysisTelemetry";
}
