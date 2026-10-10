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

public sealed class TokenTests(PostgresContainerFixture postgres)
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string Origin = "https://localhost";

    private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, HttpClient Anon, TestAnalystManager Manager, string ClientId, Guid Id) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Anon.Dispose();
            Manager.Dispose();
            await Factory.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private async Task<Setup> StartAsync(string protection, bool activate, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = RegistryTestHost.Create(db);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var manager = new TestAnalystManager();
        var anon = factory.CreateApiClient();
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager, protection), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var sub = await admin.SendAsync(HttpMethod.Post, "/api/v1/analyst-manager-registrations",
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        using var ok = await admin.SendAsync(HttpMethod.Post, $"/api/v1/analyst-manager-registrations/{id}/approval", null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var clientId = "";
        if (activate)
        {
            using var st = await RegistryTestHost.StatusAsync(anon, handle, ct);
            var challenge = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
            var proof = manager.ActivationProof(factory.Time.GetUtcNow(), Origin + "/api/v1/analyst-managers/pairings/activation", challenge);
            using var act = await anon.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation", new { pollingHandle = handle, proof }, ct);
            act.StatusCode.ShouldBe(HttpStatusCode.OK);
            clientId = JsonDocument.Parse(await act.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;
        }

        return new Setup(db, factory, anon, manager, clientId, id);
    }

    private static Task<HttpResponseMessage> RequestAsync(
        Setup s, string? assertion = null, string? dpop = null, string grant = "client_credentials", string? scope = null,
        string? clientId = null, bool noDpop = false)
    {
        var now = s.Factory.Time.GetUtcNow();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = grant,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertion ?? s.Manager.ClientAssertion(now, Origin + TokenPath, clientId ?? s.ClientId),
            ["client_id"] = clientId ?? s.ClientId,
        };
        if (scope is not null)
        {
            form["scope"] = scope;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, TokenPath) { Content = new FormUrlEncodedContent(form) };
        if (!noDpop)
        {
            request.Headers.Add("DPoP", dpop ?? s.Manager.DPoPProof(now, "POST", Origin + TokenPath));
        }

        return s.Anon.SendAsync(request);
    }

    private static async Task<long> CountAsync(Setup s, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(s.Db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    [Theory]
    [InlineData("tpm")]
    [InlineData("software")]
    public async Task ActiveRegistrationGetsDPoPToken(string protection)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(protection, true, ct);
        using var r = await RequestAsync(s);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        r.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        root.GetProperty("token_type").GetString().ShouldBe("DPoP");
        root.GetProperty("scope").GetString().ShouldBe("analyst-manager");
        root.GetProperty("expires_in").GetInt32().ShouldBe(300);
        var token = root.GetProperty("access_token").GetString()!;
        (await CountAsync(s, "SELECT count(*) FROM socalytics.analyst_manager_access_token", ct)).ShouldBe(1);
        (await CountAsync(s, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.credential-issued'", ct)).ShouldBe(1);
        s.Factory.Logs.ToString()!.ShouldNotContain(token);
    }

    [Fact]
    public async Task RefusalsIssueNoToken()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync("tpm", true, ct);
        using var other = new TestAnalystManager();
        var now = s.Factory.Time.GetUtcNow();
        var aud = Origin + TokenPath;

        var assertion = s.Manager.ClientAssertion(now, aud, s.ClientId);
        (await RequestAsync(s, assertion: assertion)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var replay = await RequestAsync(s, assertion: assertion);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var attempts = new (string Name, Func<Task<HttpResponseMessage>> Send, string Error)[]
        {
            ("dpop-jti", () => RequestAsync(s, dpop: s.Manager.DPoPProof(now, "POST", aud, jti: "fixed")), "ok-first"),
            ("aud", () => RequestAsync(s, assertion: s.Manager.ClientAssertion(now, Origin + "/other", s.ClientId)), "invalid_client"),
            ("lifetime", () => RequestAsync(s, assertion: s.Manager.ClientAssertion(now, aud, s.ClientId, TimeSpan.FromHours(1))), "invalid_client"),
            ("dpop-key", () => RequestAsync(s, dpop: other.DPoPProof(now, "POST", aud)), "invalid_dpop_proof"),
            ("other-key-assertion", () => RequestAsync(s, assertion: s.Manager.ClientAssertion(now, aud, s.ClientId, signer: other)), "invalid_client"),
            ("htu", () => RequestAsync(s, dpop: s.Manager.DPoPProof(now, "POST", Origin + "/x")), "invalid_dpop_proof"),
            ("no-dpop", () => RequestAsync(s, noDpop: true), "invalid_dpop_proof"),
            ("grant", () => RequestAsync(s, grant: "password"), "unsupported_grant_type"),
            ("scope", () => RequestAsync(s, scope: "other"), "invalid_scope"),
        };
        foreach (var (name, send, error) in attempts)
        {
            using var r = await send();
            if (error == "ok-first")
            {
                r.StatusCode.ShouldBe(HttpStatusCode.OK, name);
                continue;
            }

            r.StatusCode.ShouldBe(HttpStatusCode.BadRequest, name);
            JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("error").GetString().ShouldBe(error, name);
            r.Headers.CacheControl!.NoStore.ShouldBeTrue();
        }

        // two accepted requests above (first assertion, first jti)
        (await CountAsync(s, "SELECT count(*) FROM socalytics.analyst_manager_access_token", ct)).ShouldBe(2);
    }

    [Fact]
    public async Task NonActiveAndStampMismatchAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync("tpm", false, ct);
        // Approved registrations have no manager id yet, so the client is unknown.
        using var r = await RequestAsync(s, clientId: Guid.NewGuid().ToString());
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountAsync(s, "SELECT count(*) FROM socalytics.analyst_manager_access_token", ct)).ShouldBe(0);

        await using var t = await StartAsync("tpm", true, ct);
        await using (var c = new NpgsqlConnection(t.Db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("UPDATE socalytics.analyst_manager_registration SET stamp_id = 'another-stamp'", c);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var mismatch = await RequestAsync(t);
        mismatch.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountAsync(t, "SELECT count(*) FROM socalytics.analyst_manager_access_token", ct)).ShouldBe(0);
        (await CountAsync(t, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.credential-refused' AND reason_code = 'stamp-mismatch'", ct)).ShouldBe(1);
    }
}
