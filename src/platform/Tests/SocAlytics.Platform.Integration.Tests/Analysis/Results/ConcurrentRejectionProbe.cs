using SocAlytics.Platform.Application.Telemetry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class ConcurrentRejectionProbe
{
    [Fact]
    public async Task RaisesAForeignRejectionWhileTheTelemetryTestRuns()
    {
        if (Environment.GetEnvironmentVariable("SOCALYTICS_PROBE_CONCURRENT_REJECTION") != "1")
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            AnalysisMetrics.CompletionRejected("result-payload-invalid");
            await Task.Delay(5, CancellationToken.None);
        }
    }
}
