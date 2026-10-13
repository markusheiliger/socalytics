using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Registration;

public sealed class StatusReportingTests
{
    private static async Task<RegistrationStatusMonitor> Activate(ManagerTestHost host)
    {
        var key = await host.Keys.CreateKeyAsync(false, TestContext.Current.CancellationToken);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                OperatingIntent.Running, false, host.Time.GetUtcNow()),
            key);
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(TestContext.Current.CancellationToken);
        return host.Services.GetRequiredService<RegistrationStatusMonitor>();
    }

    private static OperatingStateMachine Machine(ManagerTestHost host) =>
        host.Services.GetRequiredService<RegistrationCoordinator>().Machine!;

    private static async Task<System.Text.Json.Nodes.JsonNode> ReportAsync(ManagerTestHost host, RegistrationStatusMonitor monitor)
    {
        var before = host.Platform.StatusReports.Count();
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        var reports = host.Platform.StatusReports.ToList();
        reports.Count.ShouldBe(before + 1);
        return reports[^1];
    }

    [Fact]
    public async Task ReportsRunning()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        Machine(host).PreflightPassed();
        var report = await ReportAsync(host, monitor);
        ((string)report["operatingState"]!).ShouldBe("running");
        report["pausedUntil"].ShouldBeNull();
        report["runtimeUnavailableReason"].ShouldBeNull();
        ((int)report["activeAnalysts"]!).ShouldBe(0);
        ((int)report["maxAnalysts"]!).ShouldBeGreaterThanOrEqualTo(0);
        ((string)report["managerVersion"]!).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ReportsPausedWithEnd()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        var machine = Machine(host);
        machine.PreflightPassed();
        var end = host.Time.GetUtcNow().AddHours(2);
        machine.PauseUntil(end).ShouldBeTrue();
        var report = await ReportAsync(host, monitor);
        ((string)report["operatingState"]!).ShouldBe("paused");
        DateTimeOffset.Parse((string)report["pausedUntil"]!).ShouldBe(end);
    }

    [Fact]
    public async Task ReportsRuntimeUnavailableWithFirstFailedCheck()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);
        var report = await ReportAsync(host, monitor);
        ((string)report["operatingState"]!).ShouldBe("runtime-unavailable");
        ((string)report["runtimeUnavailableReason"]!).ShouldBe("runtime-integration-configured");
        report["pausedUntil"].ShouldBeNull();
    }

    [Fact]
    public async Task ReportsDraining()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        var machine = Machine(host);
        machine.PreflightPassed();
        machine.BeginDrain(DrainPurpose.SafeExit, host.Time.GetUtcNow().AddMinutes(5)).ShouldBeTrue();
        var report = await ReportAsync(host, monitor);
        ((string)report["operatingState"]!).ShouldBe("draining");
    }

    [Fact]
    public async Task UsesThePlatformIntervalFromTheResponse()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        host.Platform.IntervalSeconds = 17;
        var before = host.Platform.Requests.Count;
        var delay = await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        delay.ShouldBe(TimeSpan.FromSeconds(17));
        host.Platform.Requests.Skip(before).ShouldNotContain(r => r.Uri.AbsolutePath.EndsWith("/self/registration", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusedReportsAreLoggedWithoutChangingBehavior()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        host.Platform.ReportViolations = [new ReportViolation("maxAnalysts", "out-of-range")];
        var delay = await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        delay.ShouldBe(TimeSpan.FromSeconds(60));
        host.Control.GetStatus().RegistrationState.ShouldBe("active");
        host.Logs.GetSnapshot().ShouldContain(l => l.Message.Contains("maxAnalysts:out-of-range"));
    }

    [Fact]
    public async Task RevocationIsStillObserved()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        host.Platform.Inactive = true;
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        host.Control.GetStatus().RegistrationState.ShouldBe("unregistered");
    }
}
