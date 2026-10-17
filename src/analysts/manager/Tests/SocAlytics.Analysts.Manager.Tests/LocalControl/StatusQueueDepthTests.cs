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
        q["capabilities"]![0]!["waiting"]!.GetValue<long>().ShouldBe(5);
        q["total"]!.GetValue<long>().ShouldBe(5);
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
        report["queueDepth"]!["total"]!.GetValue<long>().ShouldBe(5);
        Valid(report).ShouldBeTrue();
    }

    [Fact]
    public void Counts_above_int_max_are_reported_as_is()
    {
        const long large = 3_000_000_000;
        var view = new QueueDepthView(new QueueDepthSnapshot(
            QueueDepthStatus.Available, Observed, Observed.AddSeconds(1),
            [new QueueDepthCapability("person-detection", large)], large, null), 1);
        var report = Report(view);
        var q = report["queueDepth"]!;
        q["capabilities"]![0]!["waiting"]!.GetValue<long>().ShouldBe(large);
        q["total"]!.GetValue<long>().ShouldBe(large);
        Valid(report).ShouldBeTrue();

        var parsed = JsonSerializer.Deserialize<QueueDepthResponse>(
            """{"observedAt":"2026-10-15T19:40:00Z","capabilities":[{"id":"person-detection","waiting":3000000000}],"total":3000000000}""",
            PlatformJson.Options);
        parsed!.Total.ShouldBe(large);
        parsed.Capabilities.Single().Waiting.ShouldBe(large);
    }

    [Fact]
    public void Host_utilization_is_serialized_and_valid()
    {
        var sampled = new DateTimeOffset(2026, 10, 15, 19, 40, 10, TimeSpan.Zero);
        var report = LocalControlServer.StatusReport(new FakeManagerControl
        {
            State = "active",
            HostUtilization = new HostUtilizationSample(sampled, 37.5m, 8589934592, 34359738368),
        });
        var h = report["hostUtilization"]!;
        h["sampledAt"]!.GetValue<string>().ShouldBe("2026-10-15T19:40:10Z");
        h["cpuPercent"]!.GetValue<double>().ShouldBe(37.5);
        h["memoryUsedBytes"]!.GetValue<long>().ShouldBe(8589934592);
        h["memoryTotalBytes"]!.GetValue<long>().ShouldBe(34359738368);
        Valid(report).ShouldBeTrue();
    }

    [Fact]
    public void Unreadable_host_utilization_is_null_and_valid()
    {
        var report = Report(View(QueueDepthStatus.NotYetAvailable));
        var h = report["hostUtilization"]!.AsObject();
        h["sampledAt"].ShouldBeNull();
        h["cpuPercent"].ShouldBeNull();
        h["memoryUsedBytes"].ShouldBeNull();
        h["memoryTotalBytes"].ShouldBeNull();
        Valid(report).ShouldBeTrue();
    }

    [Fact]
    public void Host_utilization_text_lines()
    {
        var sample = new HostUtilizationSample(Observed, 37.5m, 8589934592, 34359738368);
        HostUtilizationText.Lines(sample).ShouldBe(["CPU: 37.5 %", "Memory: 8.0 GiB of 32.0 GiB"]);
        HostUtilizationText.Lines(null).ShouldBe(["CPU: not available", "Memory: not available"]);
        HostUtilizationText.Lines(new HostUtilizationSample(Observed, null, 1024, null))
            .ShouldBe(["CPU: not available", "Memory: not available"]);
    }

    [Fact]
    public async Task Cli_status_prints_cpu_and_memory_lines()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix sockets");
        var dir = Path.Combine(Path.GetTempPath(), "sa-hu-" + Guid.NewGuid().ToString("N")[..8]);
        var path = Path.Combine(dir, "run", "manager.sock");
        try
        {
            using var server = LocalControlServer.Bind(path);
            var control = new FakeManagerControl
            {
                State = "active",
                HostUtilization = new HostUtilizationSample(Observed, 37.5m, 8589934592, 34359738368),
            };
            server.Start(control);
            var o = new StringWriter();
            var code = await SocAlytics.Analysts.Manager.Cli.ManagerCli.RunAsync(["status"], path, o, new StringWriter());
            code.ShouldBe(0);
            o.ToString().ShouldContain("CPU: 37.5 %");
            o.ToString().ShouldContain("Memory: 8.0 GiB of 32.0 GiB");

            control.HostUtilization = null;
            o = new StringWriter();
            await SocAlytics.Analysts.Manager.Cli.ManagerCli.RunAsync(["status"], path, o, new StringWriter());
            o.ToString().ShouldContain("CPU: not available");
            o.ToString().ShouldContain("Memory: not available");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
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
