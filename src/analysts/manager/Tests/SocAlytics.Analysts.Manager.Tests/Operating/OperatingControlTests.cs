using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Operating;

public sealed class OperatingControlTests
{
    private static ManagerTestHost Host(SimulatedWork work, SimulatedRuntimeAdapter adapter, DrainTimeoutPolicy policy = DrainTimeoutPolicy.CancelRequest) =>
        new(configure: s =>
        {
            s.RemoveAll<IManagerWorkTracker>();
            s.AddSingleton<IManagerWorkTracker>(work);
            s.RemoveAll<RuntimeAdapterCatalog>();
            s.AddSingleton(new RuntimeAdapterCatalog([adapter]));
            s.RemoveAll<RuntimeCompatibilityList>();
            s.AddSingleton(SimulatedRuntimeAdapter.List());
            s.Configure<RuntimeOptions>(o => o.Profile = SimulatedRuntimeAdapter.TestProfile);
            s.Configure<OperatingOptions>(o => o.DrainTimeoutPolicy = policy);
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

    private static async Task<OperatingIntent?> Stored(ManagerTestHost h) =>
        (await h.Services.GetRequiredService<SignedStateStore>().ReadAsync(TestContext.Current.CancellationToken)).State?.Intent;

    [Fact]
    public async Task PauseStopsAdmissionAndPersistsIntent()
    {
        await using var host = Host(new SimulatedWork(), new SimulatedRuntimeAdapter());
        await Restore(host);
        Admits(host).ShouldBeTrue();
        (await host.Control.PauseAsync()).Succeeded.ShouldBeTrue();
        Admits(host).ShouldBeFalse();
        host.Control.GetStatus().OperatingState.ShouldBe("paused");
        (await Stored(host))!.Kind.ShouldBe(OperatingIntentKind.Paused);
        (await host.Control.ResumeAsync()).Succeeded.ShouldBeTrue();
        Admits(host).ShouldBeTrue();
        (await Stored(host))!.Kind.ShouldBe(OperatingIntentKind.Running);
    }

    [Fact]
    public async Task PauseWhileRuntimeUnavailableEntersPausedAfterPreflight()
    {
        var adapter = new SimulatedRuntimeAdapter { Failure = new InvalidOperationException() };
        await using var host = Host(new SimulatedWork(), adapter);
        await Restore(host, preflight: false);
        (await host.Control.PauseAsync()).Succeeded.ShouldBeTrue();
        host.Control.GetStatus().OperatingState.ShouldBe("runtime-unavailable");
        adapter.Failure = null;
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);
        host.Control.GetStatus().OperatingState.ShouldBe("paused");
        Admits(host).ShouldBeFalse();
    }

    [Fact]
    public async Task ResumeNeedsPassedPreflight()
    {
        var adapter = new SimulatedRuntimeAdapter { Failure = new InvalidOperationException() };
        await using var host = Host(new SimulatedWork(), adapter);
        await Restore(host, preflight: false, intent: OperatingIntent.Paused);
        (await host.Control.ResumeAsync()).Succeeded.ShouldBeTrue();
        host.Control.GetStatus().OperatingState.ShouldBe("runtime-unavailable");
        Admits(host).ShouldBeFalse();
        adapter.Failure = null;
        await host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);
        Admits(host).ShouldBeTrue();
    }

    [Fact]
    public async Task SafeExitKeepsRegistrationAndIntent()
    {
        var work = new SimulatedWork();
        await using var host = Host(work, new SimulatedRuntimeAdapter());
        await Restore(host, intent: OperatingIntent.Paused);
        var exited = false;
        host.Control.ExitCompleted += (_, _) => exited = true;
        work.Start();
        var task = host.Control.ExitAsync();
        host.Control.GetStatus().Drain!.Purpose.ShouldBe("safe-exit");
        work.Finish();
        (await task).Succeeded.ShouldBeTrue();
        exited.ShouldBeTrue();
        File.Exists(host.Services.GetRequiredService<SignedStateStore>().Path).ShouldBeTrue();
        (await Stored(host))!.Kind.ShouldBe(OperatingIntentKind.Paused);
    }

    [Fact]
    public async Task SafeExitTimeoutCancelsExitUnderCancelPolicy()
    {
        var work = new SimulatedWork();
        await using var host = Host(work, new SimulatedRuntimeAdapter());
        await Restore(host);
        work.Start();
        var task = host.Control.ExitAsync();
        host.Time.Advance(TimeSpan.FromMinutes(5));
        (await task).Succeeded.ShouldBeFalse();
        host.Control.GetStatus().OperatingState.ShouldBe("running");
    }

    [Fact]
    public async Task SafeExitTimeoutStopsWorkUnderStopPolicy()
    {
        var work = new SimulatedWork();
        await using var host = Host(work, new SimulatedRuntimeAdapter(), DrainTimeoutPolicy.StopAndCleanUp);
        await Restore(host);
        work.Start();
        var task = host.Control.ExitAsync();
        host.Time.Advance(TimeSpan.FromMinutes(5));
        (await task).Succeeded.ShouldBeTrue();
        work.Stopped.ShouldBeTrue();
    }

    [Fact]
    public async Task SessionEndUsesItsBound()
    {
        var work = new SimulatedWork();
        await using var host = Host(work, new SimulatedRuntimeAdapter());
        await Restore(host);
        var exited = false;
        host.Control.ExitCompleted += (_, _) => exited = true;
        work.Start();
        var task = host.Control.SessionEndingAsync();
        host.Control.GetStatus().Drain!.Purpose.ShouldBe("session-end");
        host.Time.Advance(TimeSpan.FromSeconds(5));
        await task;
        exited.ShouldBeTrue();
    }

    [Fact]
    public async Task ControlsAreRefusedUnlessActive()
    {
        await using var host = Host(new SimulatedWork(), new SimulatedRuntimeAdapter());
        (await host.Control.PauseAsync()).Code.ShouldBe("invalid-registration-state");
        (await host.Control.ResumeAsync()).Code.ShouldBe("invalid-registration-state");
        (await host.Control.ExitAsync()).Code.ShouldBe("invalid-registration-state");
    }
}
