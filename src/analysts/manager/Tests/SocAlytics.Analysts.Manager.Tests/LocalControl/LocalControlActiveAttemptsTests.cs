using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.LocalControl;

public sealed class LocalControlActiveAttemptsTests
{
    private static JsonObject Report(FakeManagerControl control) => LocalControlServer.StatusReport(control);

    private static bool Valid(JsonObject report) => LocalControlSchema.Value.Evaluate(JsonSerializer.SerializeToElement(
        new { protocolVersion = 1, ok = true, result = report })).IsValid;

    [Fact]
    public void Status_serializes_concurrency_and_attempts_and_validates()
    {
        var control = new FakeManagerControl { State = "active", Concurrency = new ConcurrencySnapshot(2, 4, 3, 2) };
        var id = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 10, 12, 14, 20, 1, TimeSpan.Zero);
        control.ActiveAttempts.Add(new ActiveAttemptStatus(id, 1, "player-detection", "running", started, started.AddSeconds(40)));
        control.ActiveAttempts.Add(new ActiveAttemptStatus(Guid.NewGuid(), 2, "ball-tracking", "preparing", started, null));

        var report = Report(control);

        report["concurrency"]!["effective"]!.GetValue<int>().ShouldBe(2);
        report["concurrency"]!["stampMaximum"]!.GetValue<int>().ShouldBe(4);
        report["concurrency"]!["runtimeMaximum"]!.GetValue<int>().ShouldBe(3);
        var attempts = report["activeAttempts"]!.AsArray();
        attempts.Count.ShouldBe(2);
        attempts[0]!["logicalJobId"]!.GetValue<string>().ShouldBe(id.ToString());
        attempts[0]!["startedAt"]!.GetValue<string>().ShouldBe("2026-10-12T14:20:01Z");
        attempts[0]!["lastConfirmedHeartbeatAt"]!.GetValue<string>().ShouldBe("2026-10-12T14:20:41Z");
        attempts[1]!["lastConfirmedHeartbeatAt"].ShouldBeNull();
        Valid(report).ShouldBeTrue();
    }

    [Fact]
    public void Unknown_bounds_are_null_and_no_secret_fields_appear()
    {
        var control = new FakeManagerControl { State = "active" };
        control.ActiveAttempts.Add(new ActiveAttemptStatus(Guid.NewGuid(), 1, "c", "finalizing", DateTimeOffset.UtcNow, null));

        var report = Report(control);

        report["concurrency"]!["stampMaximum"].ShouldBeNull();
        report["concurrency"]!["runtimeMaximum"].ShouldBeNull();
        Valid(report).ShouldBeTrue();
        var json = report.ToJsonString().ToLowerInvariant();
        json.ShouldNotContain("fencing");
        json.ShouldNotContain("checksum");
        json.ShouldNotContain("credential");
        report["activeAttempts"]![0]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ShouldBe(
            ["attemptNumber", "capability", "executionState", "lastConfirmedHeartbeatAt", "logicalJobId", "startedAt"]);
    }
}
