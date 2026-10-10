using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Restore;

public sealed class RestoreTests
{
    private static async Task<IDeviceKey> Seed(ManagerTestHost host, OperatingIntent? intent = null, bool pending = false)
    {
        var key = await host.Keys.CreateKeyAsync(false, TestContext.Current.CancellationToken);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                intent ?? OperatingIntent.Running, pending, host.Time.GetUtcNow()),
            key);
        return key;
    }

    private static Task Run(ManagerTestHost host) =>
        host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(TestContext.Current.CancellationToken);

    private static string State(ManagerTestHost h) => h.Control.GetStatus().RegistrationState;

    private static bool Admits(ManagerTestHost h) => h.Services.GetRequiredService<AdmissionGate>().TryAdmit();

    [Fact]
    public async Task NoStateIsUnregistered()
    {
        await using var host = new ManagerTestHost();
        await Run(host);
        State(host).ShouldBe("unregistered");
    }

    [Fact]
    public async Task IntactStateRestoresWithOneTokenAndOneStatusCall()
    {
        await using var host = new ManagerTestHost();
        await Seed(host);
        await Run(host);
        var s = host.Control.GetStatus();
        s.RegistrationState.ShouldBe("active");
        s.ManagerId.ShouldBe("manager-1");
        s.StampId.ShouldBe("stamp-1");
        s.ClubName.ShouldBe("Club");
        s.OperatingState.ShouldBe("runtime-unavailable");
        host.Platform.TokenRequests.ShouldBe(1);
        host.Platform.SelfCalls.ShouldBe(1);
        Admits(host).ShouldBeFalse();
    }

    [Fact]
    public async Task PausedIntentRestoresPaused()
    {
        await using var host = new ManagerTestHost();
        await Seed(host, OperatingIntent.Paused);
        await Run(host);
        host.Control.GetStatus().OperatingIntent.ShouldBe("paused");
        Admits(host).ShouldBeFalse();
    }

    [Fact]
    public async Task MissingKeyFailsWithoutTokenRequest()
    {
        await using var host = new ManagerTestHost();
        var key = await Seed(host);
        await host.Keys.DeleteKeyAsync(key.Reference, TestContext.Current.CancellationToken);
        await Run(host);
        var s = host.Control.GetStatus();
        s.RegistrationState.ShouldBe("restore-failed");
        s.Diagnostics.ShouldContain(d => d.Code == "key-missing");
        host.Platform.TokenRequests.ShouldBe(0);
        Admits(host).ShouldBeFalse();
    }

    [Fact]
    public async Task StampMismatchFails()
    {
        await using var host = new ManagerTestHost();
        await Seed(host);
        host.Platform.OwnStampId = "other";
        await Run(host);
        State(host).ShouldBe("restore-failed");
        host.Control.GetStatus().Diagnostics.ShouldContain(d => d.Code == "stamp-mismatch");
        Admits(host).ShouldBeFalse();
    }

    [Fact]
    public async Task UnreachablePlatformRetriesWithBackoffAndKeepsState()
    {
        await using var host = new ManagerTestHost();
        await Seed(host);
        host.Platform.UnreachableCount = 2;
        var run = Run(host);
        for (var i = 0; i < 100 && !run.IsCompleted; i++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        run.IsCompleted.ShouldBeTrue();
        State(host).ShouldBe("active");
        File.Exists(host.Services.GetRequiredService<SignedStateStore>().Path).ShouldBeTrue();
    }

    [Fact]
    public async Task InactiveSignalClearsStateAndKey()
    {
        await using var host = new ManagerTestHost();
        await Seed(host);
        host.Platform.Inactive = true;
        await Run(host);
        State(host).ShouldBe("unregistered");
        host.Keys.LiveCount.ShouldBe(0);
        File.Exists(host.Services.GetRequiredService<SignedStateStore>().Path).ShouldBeFalse();
    }

    [Fact]
    public async Task PendingUnregisterWithActiveHoldsGate()
    {
        await using var host = new ManagerTestHost();
        await Seed(host, pending: true);
        await Run(host);
        var s = host.Control.GetStatus();
        s.RegistrationState.ShouldBe("active");
        s.PendingUnregister.ShouldBeTrue();
        Admits(host).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlatformUnregisteringRestoresUnregisteringWithFlag(bool flag)
    {
        await using var host = new ManagerTestHost();
        await Seed(host, pending: flag);
        host.Platform.OwnState = "unregistering";
        await Run(host);
        var s = host.Control.GetStatus();
        s.RegistrationState.ShouldBe("unregistering");
        s.OperatingState.ShouldBeNull();
        s.PendingUnregister.ShouldBeTrue();
        Admits(host).ShouldBeFalse();
        var read = await host.Services.GetRequiredService<SignedStateStore>().ReadAsync(TestContext.Current.CancellationToken);
        read.State!.PendingUnregister.ShouldBeTrue();
    }
}
