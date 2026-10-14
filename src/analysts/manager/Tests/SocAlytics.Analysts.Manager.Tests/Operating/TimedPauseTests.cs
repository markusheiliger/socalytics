using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Operating;

public sealed class TimedPauseTests
{
    private static ManagerTestHost Host(SimulatedRuntimeAdapter adapter) =>
        new(configure: s =>
        {
            s.RemoveAll<RuntimeAdapterCatalog>();
            s.AddSingleton(new RuntimeAdapterCatalog([adapter]));
            s.RemoveAll<RuntimeCompatibilityList>();
            s.AddSingleton(SimulatedRuntimeAdapter.List());
            s.Configure<RuntimeOptions>(o => o.Profile = SimulatedRuntimeAdapter.TestProfile);
        });

    private static async Task Restore(ManagerTestHost host, bool preflight = true, OperatingIntent? intent = null)
    {
        var key = await host.Keys.CreateKeyAsync(false, TestContext.Current.CancellationToken);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                intent ?? OperatingIntent.Running, false, host.Time.GetUtcNow()),
            key);
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(TestContext.Current.CancellationToken);
        if (preflight)
        {
            await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);
        }
    }

    private static bool Admits(ManagerTestHost h) => h.Services.GetRequiredService<AdmissionGate>().TryAdmit();

    private static TimedPauseScheduler Scheduler(ManagerTestHost h) => h.Services.GetRequiredService<TimedPauseScheduler>();

    private static async Task<OperatingIntent?> Stored(ManagerTestHost h) =>
        (await h.Services.GetRequiredService<SignedStateStore>().ReadAsync(TestContext.Current.CancellationToken)).State?.Intent;

    [Fact]
    public async Task TimedPauseStopsAdmissionAndReportsEnd()
    {
        await using var host = Host(new SimulatedRuntimeAdapter());
        await Restore(host);
        var end = host.Time.GetUtcNow().AddMinutes(10);
        (await host.Control.PauseAsync(TimeSpan.FromMinutes(10))).Succeeded.ShouldBeTrue();
        Admits(host).ShouldBeFalse();
        var status = host.Control.GetStatus();
        status.OperatingState.ShouldBe("paused");
        status.OperatingIntent.ShouldBe("paused-until");
        status.PausedUntil.ShouldBe(end);
        status.PauseRemainingSeconds!.Value.ShouldBe(600, 0.5);
        var stored = (await Stored(host))!;
        stored.Kind.ShouldBe(OperatingIntentKind.PausedUntil);
        stored.EndUtc.ShouldBe(end);
    }

    [Fact]
    public async Task AutoResumesAtEndWhenPreflightPassed()
    {
        await using var host = Host(new SimulatedRuntimeAdapter());
        await Restore(host);
        await host.Control.PauseAsync(TimeSpan.FromMinutes(2));
        host.Time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        (await Scheduler(host).CheckAsync()).ShouldBeFalse();
        Admits(host).ShouldBeFalse();
        host.Time.Advance(TimeSpan.FromSeconds(1));
        (await Scheduler(host).CheckAsync()).ShouldBeTrue();
        host.Control.GetStatus().OperatingState.ShouldBe("running");
        Admits(host).ShouldBeTrue();
        (await Stored(host))!.Kind.ShouldBe(OperatingIntentKind.Running);
    }

    [Fact]
    public async Task EndWhileRuntimeUnavailableRestoresIntentButAdmitsNothing()
    {
        var adapter = new SimulatedRuntimeAdapter();
        await using var host = Host(adapter);
        await Restore(host);
        await host.Control.PauseAsync(TimeSpan.FromMinutes(2));
        adapter.Failure = new InvalidOperationException();
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);
        host.Time.Advance(TimeSpan.FromMinutes(3));
        (await Scheduler(host).CheckAsync()).ShouldBeTrue();
        var status = host.Control.GetStatus();
        status.OperatingState.ShouldBe("runtime-unavailable");
        status.OperatingIntent.ShouldBe("running");
        Admits(host).ShouldBeFalse();
        adapter.Failure = null;
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);
        Admits(host).ShouldBeTrue();
    }

    [Fact]
    public async Task RestartBeforeEndContinuesToOriginalEnd()
    {
        await using var host = Host(new SimulatedRuntimeAdapter());
        var end = host.Time.GetUtcNow().AddMinutes(5);
        await Restore(host, intent: OperatingIntent.PausedUntil(end));
        host.Control.GetStatus().OperatingState.ShouldBe("paused");
        host.Control.GetStatus().PausedUntil.ShouldBe(end);
        Admits(host).ShouldBeFalse();
        host.Time.Advance(TimeSpan.FromMinutes(5));
        (await Scheduler(host).CheckAsync()).ShouldBeTrue();
        Admits(host).ShouldBeTrue();
    }

    [Fact]
    public async Task RestartAfterEndRestoresRunning()
    {
        await using var host = Host(new SimulatedRuntimeAdapter());
        await Restore(host, intent: OperatingIntent.PausedUntil(host.Time.GetUtcNow().AddMinutes(-1)));
        host.Control.GetStatus().OperatingIntent.ShouldBe("running");
        Admits(host).ShouldBeTrue();
    }

    [Fact]
    public async Task ExplicitResumeEndsEarly()
    {
        await using var host = Host(new SimulatedRuntimeAdapter());
        await Restore(host);
        await host.Control.PauseAsync(TimeSpan.FromHours(1));
        (await host.Control.ResumeAsync()).Succeeded.ShouldBeTrue();
        Admits(host).ShouldBeTrue();
        host.Control.GetStatus().PausedUntil.ShouldBeNull();
        (await Scheduler(host).CheckAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task DurationBoundsAreEnforced()
    {
        await using var host = Host(new SimulatedRuntimeAdapter());
        await Restore(host);
        (await host.Control.PauseAsync(TimeSpan.FromSeconds(59))).Code.ShouldBe("invalid-pause-duration");
        (await host.Control.PauseAsync(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1))).Code.ShouldBe("invalid-pause-duration");
        (await host.Control.PauseUntilAsync(host.Time.GetUtcNow().AddMinutes(-5))).Code.ShouldBe("invalid-pause-duration");
        (await host.Control.PauseAsync(TimeSpan.FromMinutes(1))).Succeeded.ShouldBeTrue();
        (await host.Control.PauseUntilAsync(host.Time.GetUtcNow().AddDays(7))).Succeeded.ShouldBeTrue();
        Admits(host).ShouldBeFalse();
    }
}
