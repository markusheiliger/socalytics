using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Revocation;

public sealed class RevocationTests
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
        var monitor = host.Services.GetRequiredService<RegistrationStatusMonitor>();
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(TestContext.Current.CancellationToken);
        host.Control.GetStatus().RegistrationState.ShouldBe("active");
        return monitor;
    }

    private static string State(ManagerTestHost h) => h.Control.GetStatus().RegistrationState;

    private static bool StateFile(ManagerTestHost h) => File.Exists(h.Services.GetRequiredService<SignedStateStore>().Path);

    [Fact]
    public async Task RevocationClearsEverythingWithinOneInterval()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        var delay = await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
        host.Platform.Inactive = true;
        host.Time.Advance(delay);
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        State(host).ShouldBe("unregistered");
        StateFile(host).ShouldBeFalse();
        host.Keys.LiveCount.ShouldBe(0);
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationAppliesFromPausedAndRuntimeUnavailable(bool paused)
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        var machine = host.Services.GetRequiredService<RegistrationCoordinator>().Machine!;
        if (paused)
        {
            machine.PreflightPassed();
            machine.Pause().ShouldBeTrue();
            machine.State.Kind.ShouldBe(OperatingStateKind.Paused);
        }
        else
        {
            machine.State.Kind.ShouldBe(OperatingStateKind.RuntimeUnavailable);
        }

        host.Platform.Inactive = true;
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        State(host).ShouldBe("unregistered");
        machine.IsRevoked.ShouldBeTrue();
        StateFile(host).ShouldBeFalse();
        host.Keys.LiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task TransientAndNonInactiveFailuresKeepTheRegistration()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        var ct = TestContext.Current.CancellationToken;

        host.Platform.SelfStatus = HttpStatusCode.InternalServerError;
        await monitor.CheckOnceAsync(ct);
        host.Platform.SelfStatus = HttpStatusCode.RequestTimeout;
        await monitor.CheckOnceAsync(ct);
        host.Platform.SelfStatus = HttpStatusCode.Unauthorized;
        await monitor.CheckOnceAsync(ct);
        host.Platform.SelfStatus = null;
        host.Platform.UnreachableCount = 1;
        await monitor.CheckOnceAsync(ct);

        State(host).ShouldBe("active");
        StateFile(host).ShouldBeTrue();
        host.Keys.LiveCount.ShouldBe(1);
    }

    [Fact]
    public async Task NewRegistrationAfterRevocationNeedsNewPairing()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        host.Platform.Inactive = true;
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        host.Platform.Inactive = false;
        host.Keys.CreatedCount.ShouldBe(1);

        var result = await host.Control.RegisterAsync(
            new RegisterRequest("https://platform.test"), TestContext.Current.CancellationToken);
        result.Succeeded.ShouldBeTrue();
        result.PairingCode.ShouldNotBeNull();
        host.Keys.CreatedCount.ShouldBe(2);
        await host.Control.CancelRegistrationAsync();
    }

    [Fact]
    public async Task InaccessibleKeyClosesTheGateAndFailsClosed()
    {
        await using var host = new ManagerTestHost();
        var monitor = await Activate(host);
        var ct = TestContext.Current.CancellationToken;
        host.Services.GetRequiredService<SocAlytics.Analysts.Manager.Platform.ManagerTokenCache>().Clear();
        host.Keys.SigningFails = true;
        var before = host.Platform.Requests.Count;

        await monitor.CheckOnceAsync(ct);

        var status = host.Control.GetStatus();
        status.RegistrationState.ShouldBe("restore-failed");
        status.Diagnostics.ShouldContain(d => d.Code == "key-inaccessible");
        StateFile(host).ShouldBeTrue();
        host.Keys.LiveCount.ShouldBe(1);
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();

        await monitor.CheckOnceAsync(ct);
        host.Platform.Requests.Count.ShouldBe(before);
    }
}
