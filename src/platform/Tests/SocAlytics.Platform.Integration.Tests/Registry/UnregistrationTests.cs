using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class UnregistrationTests(PostgresContainerFixture postgres)
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string SelfPath = "/api/v1/analyst-managers/self/registration";
    private const string Path = "/api/v1/analyst-manager-registrations";
    private const string Origin = "https://localhost";

    private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, HttpClient Anon, ApiSession Admin, ApiSession Registrar, TestAnalystManager Manager, string ClientId, Guid Id, string Token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            Registrar.Dispose();
            Anon.Dispose();
            Manager.Dispose();
            await Factory.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private async Task<Setup> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = RegistryTestHost.Create(db);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var registrar = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        var manager = new TestAnalystManager();
        var anon = factory.CreateApiClient();
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var sub = await admin.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        using var ok = await admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var st = await RegistryTestHost.StatusAsync(anon, handle, ct);
        var challenge = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
        var proof = manager.ActivationProof(factory.Time.GetUtcNow(), Origin + "/api/v1/analyst-managers/pairings/activation", challenge);
        using var act = await anon.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation", new { pollingHandle = handle, proof }, ct);
        act.StatusCode.ShouldBe(HttpStatusCode.OK);
        var clientId = JsonDocument.Parse(await act.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;
        var s = new Setup(db, factory, anon, admin, registrar, manager, clientId, id, "");
        return s with { Token = await TokenAsync(s, ct) };
    }

    private static async Task<string> TokenAsync(Setup s, CancellationToken ct)
    {
        using var r = await TokenRequestAsync(s, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!;
    }

    private static Task<HttpResponseMessage> TokenRequestAsync(Setup s, CancellationToken ct)
    {
        var now = s.Factory.Time.GetUtcNow();
        var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = s.Manager.ClientAssertion(now, Origin + TokenPath, s.ClientId),
                ["client_id"] = s.ClientId,
            }),
        };
        request.Headers.Add("DPoP", s.Manager.DPoPProof(now, "POST", Origin + TokenPath));
        return s.Anon.SendAsync(request, ct);
    }

    private static Task<HttpResponseMessage> PostAsync(Setup s, string path, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + token);
        request.Headers.Add("DPoP", s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "POST", Origin + path, token));
        return s.Anon.SendAsync(request, ct);
    }

    private static Task<HttpResponseMessage> SelfAsync(Setup s, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, SelfPath);
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + token);
        request.Headers.Add("DPoP", s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "GET", Origin + SelfPath, token));
        return s.Anon.SendAsync(request, ct);
    }

    private static async Task<long> CountAsync(Setup s, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(s.Db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecAsync(Setup s, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(s.Db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AssertInactiveAsync(Setup s, string token, string state, CancellationToken ct)
    {
        using var self = await SelfAsync(s, token, ct);
        self.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        self.Headers.WwwAuthenticate.ToString().ShouldBe("DPoP error=\"invalid_token\"");
        var body = JsonDocument.Parse(await self.Content.ReadAsStringAsync(ct)).RootElement;
        body.GetProperty("code").GetString().ShouldBe("analyst-manager-registration-inactive");
        body.GetProperty("registrationState").GetString().ShouldBe(state);

        using var tok = await TokenRequestAsync(s, ct);
        tok.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var t = JsonDocument.Parse(await tok.Content.ReadAsStringAsync(ct)).RootElement;
        t.GetProperty("error").GetString().ShouldBe("invalid_client");
        t.GetProperty("registration_state").GetString().ShouldBe(state);
    }

    private const string BeginPath = "/api/v1/analyst-managers/self/unregistration";
    private const string CompletePath = BeginPath + "/completion";

    [Fact]
    public async Task ActiveToUnregisteringToUnregistered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var second = await TokenAsync(s, ct);

        using var early = await PostAsync(s, CompletePath, s.Token, ct);
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        for (var i = 0; i < 2; i++)
        {
            using var begin = await PostAsync(s, BeginPath, s.Token, ct);
            begin.StatusCode.ShouldBe(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync(ct));
            JsonDocument.Parse(await begin.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("state").GetString().ShouldBe("unregistering");
        }

        (await CountAsync(s, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.unregistration-started'", ct)).ShouldBe(1);
        (await CountAsync(s, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'unregistering' AND unregistration_started_at IS NOT NULL", ct)).ShouldBe(1);

        using (var own = await SelfAsync(s, s.Token, ct))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await TokenAsync(s, ct)).ShouldNotBeNullOrEmpty();
        using (var list = await s.Admin.SendAsync(HttpMethod.Get, $"{Path}/{s.Id}", null, ct))
        {
            list.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("state").GetString().ShouldBe("unregistering");
        }

        using var done = await PostAsync(s, CompletePath, s.Token, ct);
        done.StatusCode.ShouldBe(HttpStatusCode.OK, await done.Content.ReadAsStringAsync(ct));
        var doc = JsonDocument.Parse(await done.Content.ReadAsStringAsync(ct)).RootElement;
        doc.GetProperty("state").GetString().ShouldBe("unregistered");
        doc.GetProperty("managerId").GetString().ShouldBe(s.ClientId);
        (await CountAsync(s, "SELECT count(*) FROM socalytics.analyst_manager_access_token", ct)).ShouldBe(0);
        (await CountAsync(s, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'unregistered' AND ended_at IS NOT NULL", ct)).ShouldBe(1);
        (await CountAsync(s, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.unregistered'", ct)).ShouldBe(1);

        using var repeat = await PostAsync(s, CompletePath, second, ct);
        repeat.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        JsonDocument.Parse(await repeat.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("code").GetString().ShouldBe("analyst-manager-registration-inactive");
    }

    [Fact]
    public async Task AdminRevokesUnregisteringRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using var begin = await PostAsync(s, BeginPath, s.Token, ct);
        begin.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var revoke = await s.Admin.SendAsync(HttpMethod.Post, $"{Path}/{s.Id}/revocation", new { reason = "device lost" }, ct);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await revoke.Content.ReadAsStringAsync(ct));
        (await CountAsync(s, "SELECT count(*) FROM socalytics.analyst_manager_access_token", ct)).ShouldBe(0);
    }
}
