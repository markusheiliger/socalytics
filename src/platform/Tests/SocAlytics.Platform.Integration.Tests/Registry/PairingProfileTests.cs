using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class PairingProfileTests(PostgresContainerFixture postgres)
{
    private const string Path = "/api/v1/analyst-manager-registrations";
    private const string ActivationPath = "/api/v1/analyst-managers/pairings/activation";
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string Origin = "https://localhost";

    private static JsonObject Body(TestAnalystManager manager, string? profile, string os = "linux")
    {
        var metadata = new JsonObject
        {
            ["os"] = os,
            ["osVersion"] = "6.1",
            ["architecture"] = "x64",
            ["managerVersion"] = "1.0.0",
            ["claimedKeyProtection"] = "tpm",
        };
        if (profile is not null)
        {
            metadata["claimedApplicationProfile"] = profile;
        }

        return new JsonObject
        {
            ["stampId"] = "local-dev-stamp",
            ["publicKey"] = JsonSerializer.SerializeToNode(manager.PublicJwkObject()),
            ["nonce"] = "nonce-0123456789abcdef",
            ["deviceMetadata"] = metadata,
        };
    }

    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = RegistryTestHost.Create(db);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        return (db, factory);
    }

    private static async Task<string?> AuditProfileAsync(IsolatedDatabase db, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT details->>'claimedApplicationProfile' FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.pairing-issued'", c);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is DBNull ? null : (string?)value;
    }

    [Theory]
    [InlineData("headless-server")]
    [InlineData("tray")]
    [InlineData(null)]
    public async Task ClaimIsStoredAndReturnedInEveryRead(string? profile)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var dbScope = db;
        await using var factoryScope = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var anon = factory.CreateApiClient();
        using var manager = new TestAnalystManager();

        using var pair = await RegistryTestHost.PairAsync(anon, Body(manager, profile), ct);
        pair.StatusCode.ShouldBe(HttpStatusCode.Created, await pair.Content.ReadAsStringAsync(ct));
        var paired = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        (await AuditProfileAsync(db, ct)).ShouldBe(profile);

        using var submitted = await admin.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = paired.GetProperty("pairingCode").GetString(), deviceFingerprint = paired.GetProperty("deviceFingerprint").GetString() }, ct);
        submitted.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(ct)).RootElement;
        var id = created.GetProperty("registrationId").GetGuid();

        void Check(JsonElement registration)
        {
            var metadata = registration.GetProperty("deviceMetadata");
            if (profile is null)
            {
                metadata.TryGetProperty("claimedApplicationProfile", out var ignored).ShouldBeFalse();
            }
            else
            {
                metadata.GetProperty("claimedApplicationProfile").GetString().ShouldBe(profile);
            }
        }

        Check(created);
        using var list = await admin.SendAsync(HttpMethod.Get, Path, null, ct);
        Check(JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("registrationId").GetGuid() == id));
        using var detail = await admin.SendAsync(HttpMethod.Get, $"{Path}/{id}", null, ct);
        Check(JsonDocument.Parse(await detail.Content.ReadAsStringAsync(ct)).RootElement);
    }

    [Theory]
    [InlineData("kiosk", "linux", "Must be tray or headless-server.")]
    [InlineData("headless-server", "windows", "The headless server profile is available only on Linux.")]
    [InlineData("headless-server", "macos", "The headless server profile is available only on Linux.")]
    public async Task InvalidClaimsAreRefused(string profile, string os, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var dbScope = db;
        await using var factoryScope = factory;
        using var anon = factory.CreateApiClient();
        using var manager = new TestAnalystManager();

        using var response = await RegistryTestHost.PairAsync(anon, Body(manager, profile, os), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync(ct);
        text.ShouldContain("deviceMetadata.claimedApplicationProfile");
        text.ShouldContain(message);
        (await AuditProfileAsync(db, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ClaimedProfileDoesNotChangeApprovalTokenOrQueueOutcomes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var dbScope = db;
        await using var factoryScope = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var anon = factory.CreateApiClient();

        var outcomes = new List<string>();
        foreach (var profile in new string?[] { "tray", "headless-server", null })
        {
            using var manager = new TestAnalystManager();
            using var pair = await RegistryTestHost.PairAsync(anon, Body(manager, profile), ct);
            var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
            var handle = root.GetProperty("pollingHandle").GetString()!;
            using var sub = await admin.SendAsync(HttpMethod.Post, Path,
                new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
            var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
            using var approval = await admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);

            using var st = await RegistryTestHost.StatusAsync(anon, handle, ct);
            var challenge = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
            var proof = manager.ActivationProof(factory.Time.GetUtcNow(), Origin + ActivationPath, challenge);
            using var activation = await anon.PostAsJsonAsync(ActivationPath, new { pollingHandle = handle, proof }, ct);
            var clientId = JsonDocument.Parse(await activation.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;

            var now = factory.Time.GetUtcNow();
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = manager.ClientAssertion(now, Origin + TokenPath, clientId),
                ["client_id"] = clientId,
            };
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, TokenPath) { Content = new FormUrlEncodedContent(form) };
            tokenRequest.Headers.Add("DPoP", manager.DPoPProof(now, "POST", Origin + TokenPath));
            using var token = await anon.SendAsync(tokenRequest, ct);
            var tokenBody = JsonDocument.Parse(await token.Content.ReadAsStringAsync(ct)).RootElement;

            outcomes.Add($"{(int)approval.StatusCode}|{(int)activation.StatusCode}|{(int)token.StatusCode}|{tokenBody.GetProperty("token_type").GetString()}|{tokenBody.GetProperty("scope").GetString()}");
        }

        outcomes.Distinct().Count().ShouldBe(1, string.Join("; ", outcomes));
        outcomes[0].ShouldStartWith("200|200|200|DPoP");
    }
}
