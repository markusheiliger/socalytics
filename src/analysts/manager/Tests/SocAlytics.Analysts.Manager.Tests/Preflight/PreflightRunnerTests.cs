using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Preflight;

public class PreflightRunnerTests
{
    private static PreflightRunner Runner(SimulatedRuntimeAdapter? adapter, RuntimeCompatibilityList list, string? profile) =>
        new(new RuntimeAdapterCatalog(adapter is null ? [] : [adapter]), list,
            Options.Create(new RuntimeOptions { Profile = profile }), new FakeTimeProvider());

    private static PreflightRunner Healthy(SimulatedRuntimeAdapter a) =>
        Runner(a, SimulatedRuntimeAdapter.List(), SimulatedRuntimeAdapter.TestProfile);

    [Fact]
    public async Task Healthy_runtime_passes_with_capabilities()
    {
        var r = await Healthy(new SimulatedRuntimeAdapter()).RunAsync(TestContext.Current.CancellationToken);
        r.Passed.ShouldBeTrue();
        r.Checks.ShouldAllBe(c => c.Passed);
        r.ValidatedCapabilities.ShouldBe(["gpu0", "gpu1"]);
    }

    private static readonly (string Id, Func<RuntimeProbe, ProbeCheck, RuntimeProbe> Break)[] Cases =
    [
        ("endpoint-reachable", (p, c) => p with { EndpointReachable = c }),
        ("endpoint-local", (p, c) => p with { EndpointLocal = c }),
        ("endpoint-authorized", (p, c) => p with { EndpointAuthorized = c }),
        ("profile-version-supported", (p, c) => p with { ProfileVersionSupported = c }),
        ("backend-available", (p, c) => p with { BackendAvailable = c }),
        ("registry-authentication", (p, c) => p with { RegistryAuthentication = c }),
        ("image-platform-compatible", (p, c) => p with { ImagePlatformCompatible = c }),
        ("resource-limits-feasible", (p, c) => p with { ResourceLimitsFeasible = c }),
    ];

    public static TheoryData<string> CheckIds => new(Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CheckIds))]
    public async Task Each_failing_check_fails_preflight(string id)
    {
        var adapter = new SimulatedRuntimeAdapter();
        adapter.Probe = Cases.Single(c => c.Id == id).Break(adapter.Probe, new ProbeCheck(false, "broken"));
        var r = await Healthy(adapter).RunAsync(TestContext.Current.CancellationToken);
        r.Passed.ShouldBeFalse();
        var failed = r.Checks.Where(c => !c.Passed).ToList();
        failed.Select(c => c.Id).ShouldBe([id]);
        failed[0].Remediation.ShouldNotBeNullOrWhiteSpace();
        r.ValidatedCapabilities.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failing_accelerator_fails_its_check_and_advertises_nothing()
    {
        var adapter = new SimulatedRuntimeAdapter();
        adapter.Probe = adapter.Probe with
        {
            Accelerators = [new AcceleratorProbe("gpu0", true), new AcceleratorProbe("gpu1", false, "no driver")],
        };
        var r = await Healthy(adapter).RunAsync(TestContext.Current.CancellationToken);
        r.Passed.ShouldBeFalse();
        r.Checks.Single(c => !c.Passed).Id.ShouldBe("accelerator-probe:gpu1");
        r.Checks.Single(c => c.Id == "accelerator-probe:gpu0").Passed.ShouldBeTrue();
        r.ValidatedCapabilities.ShouldBeEmpty();
    }

    [Fact]
    public async Task Runtime_version_outside_the_list_fails()
    {
        var adapter = new SimulatedRuntimeAdapter();
        adapter.Probe = adapter.Probe with { RuntimeVersion = "3.0.0" };
        var r = await Healthy(adapter).RunAsync(TestContext.Current.CancellationToken);
        r.Passed.ShouldBeFalse();
        r.Checks.Single(c => !c.Passed).Id.ShouldBe("runtime-version-supported");
        r.ValidatedCapabilities.ShouldBeEmpty();
    }

    [Fact]
    public async Task Production_defaults_fail_runtime_integration_configured()
    {
        var r = await Runner(null, new RuntimeCompatibilityList([]), null).RunAsync(TestContext.Current.CancellationToken);
        r.Passed.ShouldBeFalse();
        var check = r.Checks.ShouldHaveSingleItem();
        check.Id.ShouldBe("runtime-integration-configured");
        check.Remediation.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Embedded_list_is_empty() => RuntimeCompatibilityList.LoadEmbedded().Entries.ShouldBeEmpty();
}
