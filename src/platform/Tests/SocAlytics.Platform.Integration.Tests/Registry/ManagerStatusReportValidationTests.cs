using Shouldly;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.Domain.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class ManagerStatusReportValidationTests
{
    private static readonly DateTimeOffset Pause = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    private static (bool Ok, string[] Fields) Validate(
        string? state = "running", DateTimeOffset? pausedUntil = null, string? reason = null,
        int? active = 1, int? max = 2, string? version = "0.3.0")
    {
        var ok = ManagerStatusReport.TryCreate(state, pausedUntil, reason, active, max, version, out _, out var violations);
        return (ok, violations.Select(v => v.Field).ToArray());
    }

    [Theory]
    [InlineData("running")]
    [InlineData("draining")]
    public void AcceptsPlainStates(string state) => Validate(state).Ok.ShouldBeTrue();

    [Fact]
    public void AcceptsPausedWithEnd() => Validate("paused", Pause).Ok.ShouldBeTrue();

    [Fact]
    public void AcceptsPausedWithoutEnd() => Validate("paused").Ok.ShouldBeTrue();

    [Fact]
    public void AcceptsRuntimeUnavailableWithReason() =>
        Validate("runtime-unavailable", reason: "runtime-missing").Ok.ShouldBeTrue();

    [Fact]
    public void RefusesUnknownState() => Validate("sleeping").Fields.ShouldBe(["operatingState"]);

    [Fact]
    public void RefusesMissingState() => Validate(null).Fields.ShouldBe(["operatingState"]);

    [Fact]
    public void RefusesPausedUntilOutsidePaused() => Validate("running", Pause).Fields.ShouldBe(["pausedUntil"]);

    [Fact]
    public void RefusesReasonOutsideRuntimeUnavailable() =>
        Validate("running", reason: "x").Fields.ShouldBe(["runtimeUnavailableReason"]);

    [Fact]
    public void RefusesLongOrEmptyReason()
    {
        Validate("runtime-unavailable", reason: new string('a', 129)).Fields.ShouldBe(["runtimeUnavailableReason"]);
        Validate("runtime-unavailable", reason: "").Fields.ShouldBe(["runtimeUnavailableReason"]);
        Validate("runtime-unavailable", reason: new string('a', 128)).Ok.ShouldBeTrue();
    }

    [Fact]
    public void RefusesActiveAboveMaximum() => Validate(active: 3, max: 2).Fields.ShouldBe(["activeAnalysts"]);

    [Fact]
    public void ChecksCountBounds()
    {
        Validate(active: -1).Fields.ShouldBe(["activeAnalysts"]);
        Validate(active: 0, max: 1025).Fields.ShouldBe(["maxAnalysts"]);
        Validate(active: 1024, max: 1024).Ok.ShouldBeTrue();
        Validate(active: null, max: null).Fields.ShouldBe(["activeAnalysts", "maxAnalysts"]);
    }

    [Fact]
    public void ChecksVersionLength()
    {
        Validate(version: new string('1', 65)).Fields.ShouldBe(["managerVersion"]);
        Validate(version: "").Fields.ShouldBe(["managerVersion"]);
        Validate(version: new string('1', 64)).Ok.ShouldBeTrue();
    }

    [Fact]
    public void ReportsEveryViolation() =>
        Validate("nope", Pause, "r", 5, 2, null).Fields.ShouldBe(["operatingState", "pausedUntil", "runtimeUnavailableReason", "activeAnalysts", "managerVersion"]);

    private static StoredManagerStatus Stored(DateTimeOffset receivedAt, int interval) =>
        new(Guid.NewGuid(), ManagerStatusReport.Restore(ManagerOperatingState.Running, null, null, 0, 1, "1"), receivedAt, interval);

    [Fact]
    public void ConnectivityOnlineWithinMissedIntervals()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero);
        Connectivity.Derive(RegistrationState.Active, Stored(now.AddSeconds(-180), 60), now, 3).ShouldBe(ManagerConnectivity.Online);
        Connectivity.Derive(RegistrationState.Unregistering, Stored(now.AddSeconds(-10), 60), now, 3).ShouldBe(ManagerConnectivity.Online);
        Connectivity.Derive(RegistrationState.Active, Stored(now.AddSeconds(-181), 60), now, 3).ShouldBe(ManagerConnectivity.Offline);
    }

    [Fact]
    public void ConnectivityUsesStoredInterval()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero);
        Connectivity.Derive(RegistrationState.Active, Stored(now.AddSeconds(-200), 120), now, 3).ShouldBe(ManagerConnectivity.Online);
    }

    [Fact]
    public void ConnectivityOfflineWhenNeverReportedAndNotApplicableWhenEnded()
    {
        var now = DateTimeOffset.UnixEpoch;
        Connectivity.Derive(RegistrationState.Active, null, now, 3).ShouldBe(ManagerConnectivity.Offline);
        foreach (var state in new[] { RegistrationState.Pairing, RegistrationState.PendingApproval, RegistrationState.Approved,
            RegistrationState.Revoked, RegistrationState.Unregistered, RegistrationState.Rejected, RegistrationState.Expired })
        {
            Connectivity.Derive(state, Stored(now, 60), now, 3).ShouldBe(ManagerConnectivity.NotApplicable);
        }
    }
}
