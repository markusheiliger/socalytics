using System.Net;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class PairingTests(PostgresContainerFixture postgres)
{
    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(bool software, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = RegistryTestHost.Create(db, software ? SoftwareKeyPolicy.All : SoftwareKeyPolicy.Refused);
        await factory.WaitUntilHealthyAsync(ct);
        return (db, factory);
    }

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task PairingIssuesCodeAndStoresNoRawSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(true, ct);
        await using var _ = db;
        await using var __ = factory;
        using var manager = new TestAnalystManager();
        using var client = factory.CreateApiClient();

        using var response = await RegistryTestHost.PairAsync(client, RegistryTestHost.PairingBody(manager), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        var code = root.GetProperty("pairingCode").GetString()!;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        root.GetProperty("stampId").GetString().ShouldBe("local-dev-stamp");

        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'pairing'", ct)).ShouldBe(1);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.pairing-issued'", ct)).ShouldBe(1);
        (await CountAsync(db, $"SELECT count(*) FROM socalytics.analyst_manager_registration r WHERE r::text LIKE '%{handle}%' OR r::text LIKE '%{code.Replace("-", "")}%'", ct)).ShouldBe(0);

        using var status = await RegistryTestHost.StatusAsync(client, handle, ct);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("status").GetString().ShouldBe("awaiting_submission");

        using var again = await RegistryTestHost.PairAsync(client, RegistryTestHost.PairingBody(manager), ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RefusalsAndSoftwareKeyGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(false, ct);
        await using var _ = db;
        await using var __ = factory;
        using var manager = new TestAnalystManager();
        using var client = factory.CreateApiClient();

        using var mismatch = await RegistryTestHost.PairAsync(client, RegistryTestHost.PairingBody(manager, stampId: "other"), ct);
        mismatch.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var software = await RegistryTestHost.PairAsync(client, RegistryTestHost.PairingBody(manager, "software"), ct);
        software.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.pairing-refused'", ct)).ShouldBe(1);

        using var discovery = await client.GetAsync("/api/v1/analyst-managers/stamp", ct);
        JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("softwareKeysAllowed").GetBoolean().ShouldBeFalse();

        var priv = new Dictionary<string, object>(manager.PublicJwkObject()) { ["d"] = "AAAA" };
        using var privateKey = await RegistryTestHost.PairAsync(client, new
        {
            stampId = "local-dev-stamp", publicKey = priv, nonce = "nonce-0123456789abcdef",
            deviceMetadata = new { os = "linux", osVersion = "6", architecture = "x64", managerVersion = "1", claimedKeyProtection = "tpm" },
        }, ct);
        privateKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var http = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        using var notHttps = await http.GetAsync("/api/v1/analyst-managers/stamp", ct);
        notHttps.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var bodies = new List<string>();
        foreach (var handle in new[] { "unknown", new string('A', 43), "" })
        {
            using var r = await RegistryTestHost.StatusAsync(client, handle, ct);
            r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            bodies.Add(RegistryTestHost.WithoutCorrelation(await r.Content.ReadAsStringAsync(ct)));
        }

        bodies.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task SoftwareKeyPairsWhenAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(true, ct);
        await using var _ = db;
        await using var __ = factory;
        using var manager = new TestAnalystManager();
        using var client = factory.CreateApiClient();
        using var response = await RegistryTestHost.PairAsync(client, RegistryTestHost.PairingBody(manager, "software"), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
