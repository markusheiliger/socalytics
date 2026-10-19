using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Registration;

public sealed class ManagerVersionComparerTests
{
    private const string BuildVersion = "9.8.7";

    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "contracts", "analyst-manager", "status", "v1", "minimum-version.fixtures.json");

    public static TheoryData<string?, string?, bool> Cases()
    {
        var data = new TheoryData<string?, string?, bool>();
        foreach (var item in JsonNode.Parse(File.ReadAllText(FixturePath))!.AsArray())
        {
            data.Add((string?)item!["reported"], (string?)item["minimum"], (bool)item["below"]!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_shared_fixture_cases(string? reported, string? minimum, bool below) =>
        ManagerVersionComparer.IsBelow(reported, minimum).ShouldBe(below);

    [Theory]
    [InlineData("0.2")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("")]
    public void An_unparsable_minimum_counts_as_below(string minimum) =>
        ManagerVersionComparer.IsBelow("1.0.0", minimum).ShouldBeTrue();

    private static ManagerTestHost NewHost() =>
        new(configure: services =>
        {
            services.RemoveAll<ManagerBuildVersion>();
            services.AddSingleton(new ManagerBuildVersion(BuildVersion));
        });

    [Fact]
    public async Task Pairing_metadata_carries_the_build_version()
    {
        await using var host = NewHost();

        var result = await host.Control.RegisterAsync(
            new RegisterRequest("https://platform.test"), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        var pairing = host.Platform.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/pairings", StringComparison.Ordinal));
        ((string)JsonNode.Parse(pairing.Body)!["deviceMetadata"]!["managerVersion"]!).ShouldBe(BuildVersion);
    }

    [Fact]
    public async Task Status_report_and_snapshot_carry_the_build_version()
    {
        await using var host = NewHost();
        var key = await host.Keys.CreateKeyAsync(false, TestContext.Current.CancellationToken);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                OperatingIntent.Running, false, host.Time.GetUtcNow()),
            key);
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(TestContext.Current.CancellationToken);
        var monitor = host.Services.GetRequiredService<RegistrationStatusMonitor>();

        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        var report = host.Platform.StatusReports.Single();
        ((string)report["managerVersion"]!).ShouldBe(BuildVersion);
        host.Control.GetStatus().ManagerVersion.ShouldBe(BuildVersion);
    }
}
