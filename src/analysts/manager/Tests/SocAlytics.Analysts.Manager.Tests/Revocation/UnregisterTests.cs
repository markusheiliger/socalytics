using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Revocation;

public sealed class UnregisterTests
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

    private static Task Restore(ManagerTestHost host) =>
        host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(TestContext.Current.CancellationToken);

    private static Task Resume(ManagerTestHost host) =>
        host.Services.GetRequiredService<RegistrationRestorer>().ResumePendingAsync(TestContext.Current.CancellationToken);

    private static string State(ManagerTestHost h) => h.Control.GetStatus().RegistrationState;

    private static List<string> Calls(ManagerTestHost h) =>
        h.Platform.Requests.Select(r => r.Uri.AbsolutePath).Where(p => p.Contains("/self/unregistration")).ToList();

    private static ManagerTestHost WithWork(SimulatedWork work, out SimulatedWork tracker)
    {
        tracker = work;
        return new ManagerTestHost(configure: s =>
        {
            s.RemoveAll<IManagerWorkTracker>();
            s.AddSingleton<IManagerWorkTracker>(work);
        });
    }

    [Fact]
    public async Task DrainCompletesBeforeBeginThenCompletionThenCleanup()
    {
        await using var host = new ManagerTestHost();
        await Seed(host);
        await Restore(host);
        var result = await host.Control.UnregisterAsync();
        result.Succeeded.ShouldBeTrue();
        Calls(host).Count.ShouldBe(2);
        Calls(host)[0].ShouldEndWith("/self/unregistration");
        Calls(host)[1].ShouldEndWith("/self/unregistration/completion");
        State(host).ShouldBe("unregistered");
        File.Exists(host.Services.GetRequiredService<SignedStateStore>().Path).ShouldBeFalse();
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledDrainMakesNoPlatformCallAndRestoresIntent(bool paused)
    {
        var work = new SimulatedWork();
        await using var host = WithWork(work, out _);
        var intent = paused ? OperatingIntent.Paused : OperatingIntent.Running;
        await Seed(host, intent);
        await Restore(host);
        work.Start();
        var pending = host.Control.UnregisterAsync();
        var during = host.Control.GetStatus();
        during.RegistrationState.ShouldBe("active");
        during.OperatingState.ShouldBe("draining");
        Calls(host).ShouldBeEmpty();
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var result = await pending;
        result.Succeeded.ShouldBeFalse();
        Calls(host).ShouldBeEmpty();
        var s = host.Control.GetStatus();
        s.RegistrationState.ShouldBe("active");
        s.PendingUnregister.ShouldBeFalse();
        s.OperatingIntent.ShouldBe(paused ? "paused" : "running");
        s.Diagnostics.ShouldContain(d => d.Code == "unregister-cancelled");
        var read = await host.Services.GetRequiredService<SignedStateStore>().ReadAsync(TestContext.Current.CancellationToken);
        read.State!.PendingUnregister.ShouldBeFalse();
    }

    [Fact]
    public async Task UnreachablePlatformKeepsUnregisteringAndCompletesLater()
    {
        await using var host = new ManagerTestHost();
        await Seed(host);
        await Restore(host);
        host.Platform.UnregistrationUnreachableCount = 1;
        var result = await host.Control.UnregisterAsync();
        result.Succeeded.ShouldBeTrue();
        State(host).ShouldBe("unregistering");
        host.Control.GetStatus().PendingUnregister.ShouldBeTrue();
        var completion = host.Services.GetRequiredService<RegistrationCoordinator>().UnregisterCompletion;
        for (var i = 0; i < 200 && !completion.IsCompleted; i++)
        {
            host.Time.Advance(TimeSpan.FromMinutes(2));
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        await completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        State(host).ShouldBe("unregistered");
    }

    [Fact]
    public async Task RestartWithPendingFlagAndActivePlatformDrainsAndCompletes()
    {
        await using var host = new ManagerTestHost();
        await Seed(host, pending: true);
        await Restore(host);
        State(host).ShouldBe("active");
        await Resume(host);
        State(host).ShouldBe("unregistered");
        Calls(host).Count.ShouldBe(2);
    }

    [Fact]
    public async Task RestartWithPlatformUnregisteringSkipsDrainAndCompletes()
    {
        await using var host = new ManagerTestHost();
        await Seed(host);
        host.Platform.OwnState = "unregistering";
        await Restore(host);
        State(host).ShouldBe("unregistering");
        await Resume(host);
        State(host).ShouldBe("unregistered");
        Calls(host).Count.ShouldBe(2);
    }

    [Fact]
    public async Task InactiveSignalDuringUnregisterCompletesIt()
    {
        await using var host = new ManagerTestHost();
        await Seed(host);
        await Restore(host);
        host.Platform.InactiveOnUnregistration = true;
        (await host.Control.UnregisterAsync()).Succeeded.ShouldBeTrue();
        State(host).ShouldBe("unregistered");
    }

    [Fact]
    public async Task UnregisterRefusedUnlessActive()
    {
        await using var host = new ManagerTestHost();
        var result = await host.Control.UnregisterAsync();
        result.Code.ShouldBe("invalid-registration-state");
    }

    [Fact]
    public async Task DiscardOnlyFromRestoreFailed()
    {
        await using var host = new ManagerTestHost();
        (await host.Control.DiscardLocalRegistrationAsync()).Code.ShouldBe("invalid-registration-state");

        var key = await Seed(host);
        host.Platform.OwnStampId = "other";
        await Restore(host);
        State(host).ShouldBe("restore-failed");
        var result = await host.Control.DiscardLocalRegistrationAsync();
        result.Succeeded.ShouldBeTrue();
        State(host).ShouldBe("unregistered");
        File.Exists(host.Services.GetRequiredService<SignedStateStore>().Path).ShouldBeFalse();
        await Should.ThrowAsync<DeviceKeyException>(
            () => host.Keys.OpenKeyAsync(key.Reference, TestContext.Current.CancellationToken));
    }
}
