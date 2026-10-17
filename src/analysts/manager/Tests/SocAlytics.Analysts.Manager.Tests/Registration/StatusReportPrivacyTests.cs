using System.Text.Json;
using Shouldly;
using SocAlytics.Analysts.Manager.Platform;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Registration;

public sealed class StatusReportPrivacyTests
{
    [Fact]
    public void Status_report_carries_no_queue_depth()
    {
        var json = JsonSerializer.Serialize(new StatusReport("running", null, null, 0, 1, "1.0.0"), PlatformJson.Options).ToLowerInvariant();
        json.ShouldNotContain("queue");
        json.ShouldNotContain("waiting");
        typeof(StatusReport).GetProperties().Select(p => p.Name.ToLowerInvariant())
            .ShouldAllBe(n => !n.Contains("queue") && !n.Contains("waiting"));
    }

    [Fact]
    public void Status_report_carries_no_host_utilization()
    {
        var json = JsonSerializer.Serialize(new StatusReport("running", null, null, 0, 1, "1.0.0"), PlatformJson.Options).ToLowerInvariant();
        json.ShouldNotContain("cpu");
        json.ShouldNotContain("memory");
        json.ShouldNotContain("utilization");
        typeof(StatusReport).GetProperties().Select(p => p.Name.ToLowerInvariant())
            .ShouldAllBe(n => !n.Contains("cpu") && !n.Contains("memory") && !n.Contains("utilization"));
    }
}
