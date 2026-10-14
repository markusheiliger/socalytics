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

namespace SocAlytics.Analysts.Manager.Tests.Preflight;

public sealed class PreflightLifecycleTests
{
    private static ManagerTestHost Host(SimulatedRuntimeAdapter? adapter, bool production = false) =>
        new(configure: s =>
        {
            if (production)
            {
                return;
            }

            s.RemoveAll<RuntimeAdapterCatalog>();
            s.AddSingleton(new RuntimeAdapterCatalog(adapter is null ? [] : [adapter]));
            s.RemoveAll<RuntimeCompatibilityList>();
            s.AddSingleton(SimulatedRuntimeAdapter.List());
            s.Configure<RuntimeOptions>(o => o.Profile = SimulatedRuntimeAdapter.TestProfile);
        });

    private static async Task Restore(ManagerTestHost host, OperatingIntent? intent = null)
    {
        var key = await host.Keys.CreateKeyAsync(false, TestContext.Current.CancellationToken);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                intent ?? OperatingIntent.Running, false, host.Time.GetUtcNow()),
            key);
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(TestContext.Current.CancellationToken);
    }

    private static Task Check(ManagerTestHost host) =>
        host.Services.GetRequiredService<PreflightScheduler>().CheckAsync(TestContext.Current.CancellationToken);

    private static bool Admits(ManagerTestHost h) => h.Services.GetRequiredService<AdmissionGate>().TryAdmit();

    [Fact]
    public async Task Healthy_runtime_runs_after_restore()
    {
        await using var host = Host(new SimulatedRuntimeAdapter());
        await Restore(host);
        Admits(host).ShouldBeFalse();
        await Check(host);
        var s = host.Control.GetStatus();
        s.OperatingState.ShouldBe("running");
        s.Preflight.ShouldBeOfType<PreflightResult>().Passed.ShouldBeTrue();
        Admits(host).ShouldBeTrue();
    }

    [Fact]
    public async Task Paused_intent_restores_paused()
    {
        await using var host = Host(new SimulatedRuntimeAdapter());
        await Restore(host, OperatingIntent.Paused);
        await Check(host);
        host.Control.GetStatus().OperatingState.ShouldBe("paused");
        Admits(host).ShouldBeFalse();
    }

    [Fact]
    public async Task Failure_reports_check_and_closes_gate_then_recovers()
    {
        var adapter = new SimulatedRuntimeAdapter();
        await using var host = Host(adapter);
        await Restore(host);
        await Check(host);
        adapter.Probe = adapter.Probe with { BackendAvailable = new ProbeCheck(false, "down") };
        await Check(host);
        var s = host.Control.GetStatus();
        s.OperatingState.ShouldBe("runtime-unavailable");
        var failed = s.Preflight.ShouldBeOfType<PreflightResult>().Checks.Single(c => !c.Passed);
        failed.Id.ShouldBe("backend-available");
        failed.Remediation.ShouldNotBeNullOrEmpty();
        Admits(host).ShouldBeFalse();
        adapter.Probe = SimulatedRuntimeAdapter.Healthy();
        await Check(host);
        host.Control.GetStatus().OperatingState.ShouldBe("running");
    }

    [Fact]
    public async Task Lost_runtime_and_unlisted_upgrade_are_unavailable()
    {
        var adapter = new SimulatedRuntimeAdapter();
        await using var host = Host(adapter);
        await Restore(host);
        await Check(host);
        adapter.Failure = new IOException("gone");
        await Check(host);
        host.Control.GetStatus().OperatingState.ShouldBe("runtime-unavailable");
        adapter.Failure = null;
        await Check(host);
        host.Control.GetStatus().OperatingState.ShouldBe("running");
        adapter.Probe = adapter.Probe with { RuntimeVersion = "3.0.0" };
        await Check(host);
        host.Control.GetStatus().OperatingState.ShouldBe("runtime-unavailable");
    }

    [Fact]
    public async Task Revocation_is_observed_while_runtime_unavailable()
    {
        var adapter = new SimulatedRuntimeAdapter { Failure = new IOException("gone") };
        await using var host = Host(adapter);
        await Restore(host);
        await Check(host);
        var machine = host.Services.GetRequiredService<RegistrationCoordinator>().Machine!;
        machine.State.Kind.ShouldBe(OperatingStateKind.RuntimeUnavailable);
        host.Platform.Inactive = true;
        await host.Services.GetRequiredService<RegistrationStatusMonitor>().CheckOnceAsync(TestContext.Current.CancellationToken);
        host.Control.GetStatus().RegistrationState.ShouldBe("unregistered");
        machine.IsRevoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Production_defaults_stay_runtime_unavailable()
    {
        await using var host = Host(null, production: true);
        await Restore(host);
        await Check(host);
        var s = host.Control.GetStatus();
        s.OperatingState.ShouldBe("runtime-unavailable");
        s.Preflight.ShouldBeOfType<PreflightResult>().Checks[0].Id.ShouldBe("runtime-integration-configured");
        Admits(host).ShouldBeFalse();
    }
}
