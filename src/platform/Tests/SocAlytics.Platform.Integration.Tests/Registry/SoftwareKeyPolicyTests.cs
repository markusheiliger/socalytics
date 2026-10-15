using System.Net;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class SoftwareKeyPolicyTests(PostgresContainerFixture postgres)
{
    [Theory]
    [InlineData(SoftwareKeyPolicy.Refused, "tray", false)]
    [InlineData(SoftwareKeyPolicy.Refused, "headless-server", false)]
    [InlineData(SoftwareKeyPolicy.Refused, null, false)]
    [InlineData(SoftwareKeyPolicy.HeadlessServerOnly, "tray", false)]
    [InlineData(SoftwareKeyPolicy.HeadlessServerOnly, "headless-server", true)]
    [InlineData(SoftwareKeyPolicy.HeadlessServerOnly, null, false)]
    [InlineData(SoftwareKeyPolicy.All, "tray", true)]
    [InlineData(SoftwareKeyPolicy.All, "headless-server", true)]
    [InlineData(SoftwareKeyPolicy.All, null, true)]
    public async Task SoftwareKeysAreAcceptedOnlyWhenThePolicyAcceptsTheClaimedProfile(
        SoftwareKeyPolicy policy, string? profile, bool accepted)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var factory = RegistryTestHost.Create(db, policy);
        await factory.WaitUntilHealthyAsync(ct);
        using var client = factory.CreateApiClient();
        using var manager = new TestAnalystManager();

        using var response = await RegistryTestHost.PairAsync(
            client, RegistryTestHost.PairingBody(manager, "software", profile: profile), ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (accepted)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, text);
            return;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, text);
        var problem = JsonDocument.Parse(text).RootElement;
        problem.GetProperty("code").GetString().ShouldBe("analyst-manager-software-key-not-allowed");
        problem.GetProperty("detail").GetString().ShouldBe("This stamp requires hardware-backed key storage for this Manager profile.");

        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT details->>'claimedApplicationProfile', details->>'claimedKeyProtection' FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.pairing-refused'", c);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).ShouldBeTrue();
        (reader.IsDBNull(0) ? null : reader.GetString(0)).ShouldBe(profile);
        reader.GetString(1).ShouldBe("software");
    }

    [Theory]
    [InlineData(SoftwareKeyPolicy.Refused, false, new string[0])]
    [InlineData(SoftwareKeyPolicy.HeadlessServerOnly, false, new[] { "headless-server" })]
    [InlineData(SoftwareKeyPolicy.All, true, new[] { "tray", "headless-server" })]
    public async Task DiscoveryReportsThePolicy(SoftwareKeyPolicy policy, bool allowed, string[] allowedFor)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var factory = RegistryTestHost.Create(db, policy);
        await factory.WaitUntilHealthyAsync(ct);
        using var client = factory.CreateApiClient();

        using var discovery = await client.GetAsync("/api/v1/analyst-managers/stamp", ct);
        var root = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(ct)).RootElement;
        root.GetProperty("softwareKeysAllowed").GetBoolean().ShouldBe(allowed);
        root.GetProperty("softwareKeysAllowedFor").EnumerateArray().Select(e => e.GetString()).ToArray().ShouldBe(allowedFor);
    }
}
