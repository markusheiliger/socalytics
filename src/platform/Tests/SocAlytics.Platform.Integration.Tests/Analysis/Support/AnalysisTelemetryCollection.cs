using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Support;

// Build baseline (T001): `dotnet restore src/platform/SocAlytics.Platform.slnx` succeeded and
// `dotnet build src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-restore` succeeded
// with 0 warnings and 0 errors; Docker server version 28.0.4.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AnalysisTelemetryCollection
{
	public const string Name = "AnalysisTelemetry";
}
