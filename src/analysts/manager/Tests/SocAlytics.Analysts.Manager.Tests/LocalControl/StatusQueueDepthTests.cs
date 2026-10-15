using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.LocalControl;

public sealed class StatusQueueDepthTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 15, 19, 40, 0, TimeSpan.Zero);

    private static QueueDepthView View(QueueDepthStatus state, double? age = null, string? error = null, bool observation = false) =>
        new(new QueueDepthSnapshot(
            state, observation ? Observed : null, observation ? Observed.AddSeconds(1) : null,
            observation ? [new QueueDepthCapability("person-detection", 5)] : [], observation ? 5 : 0, error), age);

    private static JsonObject Report(QueueDepthView view) =>
        LocalControlServer.StatusReport(new FakeManagerControl { State = "active", QueueDepth = view });

    private static bool Valid(JsonObject report) => LocalControlSchema.Value.Evaluate(JsonSerializer.SerializeToElement(
        new { protocolVersion = 1, ok = true, result = report })).IsValid;

    [Fact]
    public void Available_observation_is_serialized_and_valid()
    {
        var report = Report(View(QueueDepthStatus.Available, 12.7, observation: true));
        var q = report["queueDepth"]!;
        q["state"]!.GetValue<string>().ShouldBe("available");
        q["observedAt"]!.GetValue<string>().ShouldBe("2026-10-15T19:40:00Z");
        q["ageSeconds"]!.GetValue<int>().ShouldBe(12);
        q["capabilities"]![0]!["id"]!.GetValue<string>().ShouldBe("person-detection");
        q["capabilities"]![0]!["waiting"]!.GetValue<int>().ShouldBe(5);
        q["total"]!.GetValue<int>().ShouldBe(5);
        Valid(report).ShouldBeTrue();
    }

    [Theory]
    [InlineData(QueueDepthStatus.NotYetAvailable, "not-yet-available")]
    [InlineData(QueueDepthStatus.NoCapability, "no-capability")]
    [InlineData(QueueDepthStatus.Unavailable, "unavailable")]
    public void States_without_an_observation_carry_no_numbers(QueueDepthStatus state, string wire)
    {
        var report = Report(View(state, error: state == QueueDepthStatus.Unavailable ? "platform-unavailable" : null));
        var q = report["queueDepth"]!.AsObject();
        q["state"]!.GetValue<string>().ShouldBe(wire);
        q.ContainsKey("observedAt").ShouldBeFalse();
        q.ContainsKey("ageSeconds").ShouldBeFalse();
        q.ContainsKey("capabilities").ShouldBeFalse();
        q.ContainsKey("total").ShouldBeFalse();
        Valid(report).ShouldBeTrue();
    }

    [Fact]
    public void Stale_observation_keeps_its_numbers()
    {
        var report = Report(View(QueueDepthStatus.Stale, 180, "platform-unavailable", true));
        report["queueDepth"]!["state"]!.GetValue<string>().ShouldBe("stale");
        report["queueDepth"]!["total"]!.GetValue<int>().ShouldBe(5);
        Valid(report).ShouldBeTrue();
    }

    [Fact]
    public void Text_lines_cover_every_state()
    {
        View(QueueDepthStatus.Available, 12, observation: true).Lines().ShouldBe(
            ["Queue (approximate guidance, observed 12 s ago):", "  person-detection: 5", "  Total: 5"]);
        View(QueueDepthStatus.Stale, 180, "x", true).Lines()[0].ShouldBe("Queue (approximate guidance, observed 3 min ago, stale):");
        View(QueueDepthStatus.NotYetAvailable).Lines().ShouldBe(["Queue: no information yet"]);
        View(QueueDepthStatus.NoCapability).Lines().ShouldBe(["Queue: no capability assigned"]);
        View(QueueDepthStatus.Unavailable, error: "refused").Lines().ShouldBe(["Queue: unavailable (refused)"]);
    }
}
