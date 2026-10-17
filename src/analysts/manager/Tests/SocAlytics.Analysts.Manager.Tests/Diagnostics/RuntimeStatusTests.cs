using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Docker;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.RuntimeStatus;

public sealed class RuntimeStatusTests
{
    private sealed class OfferingAdapter(SimulatedRuntimeAdapter inner) : IContainerRuntimeAdapter
    {
        public string Profile => inner.Profile;

        public IReadOnlyList<QueueCapability> Capabilities { get; } = [new("person-detection", "1")];

        public IReadOnlyList<NotOfferedCapability> NotOffered { get; } =
            [new("pose", "1", DockerRuntimeAdapter.AcceleratorRequired)];

        public Task<RuntimeProbe> ProbeAsync(RuntimeProbeRequest request, CancellationToken cancellationToken) =>
            inner.ProbeAsync(request, cancellationToken);

        public Task<IAttemptExecution> StartAttemptAsync(AttemptLaunch launch, CancellationToken cancellationToken) =>
            inner.StartAttemptAsync(launch, cancellationToken);
    }

    private static PreflightRunner Runner(IContainerRuntimeAdapter adapter) =>
        new(new RuntimeAdapterCatalog([adapter]), SimulatedRuntimeAdapter.List(),
            Options.Create(new RuntimeOptions { Profile = SimulatedRuntimeAdapter.TestProfile }), new FakeTimeProvider());

    [Fact]
    public async Task Passing_preflight_carries_the_offer_and_the_reasons()
    {
        var result = await Runner(new OfferingAdapter(new SimulatedRuntimeAdapter())).RunAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeTrue();
        result.Offered.ShouldBe([new PreflightCapability("person-detection", "1")]);
        result.NotOffered.ShouldBe([new PreflightCapability("pose", "1", "accelerator-required")]);
    }

    [Fact]
    public async Task Failed_preflight_offers_nothing()
    {
        var inner = new SimulatedRuntimeAdapter();
        inner.Probe = inner.Probe with { EndpointReachable = new ProbeCheck(false, "down") };

        var result = await Runner(new OfferingAdapter(inner)).RunAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeFalse();
        result.Offered.ShouldBeEmpty();
        result.NotOffered.ShouldBeEmpty();
    }

    [Fact]
    public void Text_shows_runtime_health_failed_checks_with_remediation_and_the_offer()
    {
        var result = new PreflightResult(
            DateTimeOffset.UnixEpoch, "docker-desktop", "Docker Desktop", "4.40.0", "wsl2", false,
            [
                new("endpoint-reachable", true, null, null),
                new("backend-available", false, "Windows containers", "Switch to Linux containers."),
            ],
            [])
        {
            Offered = [new PreflightCapability("person-detection", "1")],
            NotOffered = [new PreflightCapability("pose", "1", "exceeds-host-limits")],
        };

        var text = string.Join('\n', RuntimeStatusText.Lines(result));

        text.ShouldContain("Runtime: docker-desktop, Docker Desktop 4.40.0");
        text.ShouldContain("Endpoint: healthy");
        text.ShouldContain("Backend: wsl2, unhealthy");
        text.ShouldContain("Failed check backend-available: Windows containers — Switch to Linux containers.");
        text.ShouldContain("Offered capabilities: person-detection@1");
        text.ShouldContain("Not offered: pose@1 (exceeds-host-limits)");
    }

    [Fact]
    public void Text_without_a_preflight_or_offer_says_so()
    {
        RuntimeStatusText.Lines(null).ShouldBe(["Runtime: no preflight yet"]);

        var empty = new PreflightResult(DateTimeOffset.UnixEpoch, null, null, null, null, false, [], []);
        RuntimeStatusText.Lines(empty).ShouldContain("Offered capabilities: none");
    }

    [Fact]
    public void Offer_survives_the_local_control_json_round_trip()
    {
        var result = new PreflightResult(DateTimeOffset.UnixEpoch, "docker-desktop", null, null, null, true, [], [])
        {
            Offered = [new PreflightCapability("person-detection", "1")],
            NotOffered = [new PreflightCapability("pose", "1", "excluded-by-allowlist")],
        };

        var node = JsonSerializer.SerializeToNode(result, LocalControlProtocol.Json)!;
        var back = node.Deserialize<PreflightResult>(LocalControlProtocol.Json)!;

        node["notOffered"]![0]!["reason"]!.GetValue<string>().ShouldBe("excluded-by-allowlist");
        back.Offered.ShouldBe(result.Offered);
        back.NotOffered.ShouldBe(result.NotOffered);
    }
}
