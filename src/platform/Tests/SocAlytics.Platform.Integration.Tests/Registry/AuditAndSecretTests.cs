using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Application.Registry.Expiry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class AuditAndSecretTests(PostgresContainerFixture postgres)
{
    private const string Path = "/api/v1/analyst-manager-registrations";
    private const string Base = "https://localhost";
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string ActivationPath = "/api/v1/analyst-managers/pairings/activation";
    private const string BeginPath = "/api/v1/analyst-managers/self/unregistration";

    private sealed class Device(TestAnalystManager manager, string handle, string code, string fingerprint)
    {
        public TestAnalystManager Manager { get; } = manager;
        public string Handle { get; } = handle;
        public string Code { get; } = code;
        public string Fingerprint { get; } = fingerprint;
        public Guid Id { get; set; }
        public string ClientId { get; set; } = "";
    }

    private readonly List<string> _secrets = [];
    private readonly List<string> _bodies = [];

    private async Task<Device> PairAsync(HttpClient anon, TestAnalystManager manager, CancellationToken ct)
    {
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        pair.StatusCode.ShouldBe(HttpStatusCode.Created);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var d = new Device(manager, root.GetProperty("pollingHandle").GetString()!, root.GetProperty("pairingCode").GetString()!,
            root.GetProperty("deviceFingerprint").GetString()!);
        _secrets.Add(d.Handle);
        _secrets.Add(d.Code);
        return d;
    }

    private async Task SubmitAsync(ApiSession admin, Device d, CancellationToken ct)
    {
        using var r = await admin.SendAsync(HttpMethod.Post, Path, new { pairingCode = d.Code, deviceFingerprint = d.Fingerprint }, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        d.Id = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
    }

    private async Task<string> ChallengeAsync(HttpClient anon, Device d, CancellationToken ct)
    {
        using var st = await RegistryTestHost.StatusAsync(anon, d.Handle, ct);
        var challenge = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
        _secrets.Add(challenge);
        return challenge;
    }

    private async Task ActivateAsync(PlatformApiFactory f, HttpClient anon, ApiSession admin, Device d, CancellationToken ct)
    {
        await SubmitAsync(admin, d, ct);
        using var ok = await admin.SendAsync(HttpMethod.Post, $"{Path}/{d.Id}/approval", null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var challenge = await ChallengeAsync(anon, d, ct);
        var proof = d.Manager.ActivationProof(f.Time.GetUtcNow(), Base + ActivationPath, challenge);
        _secrets.Add(proof);
        using var act = await anon.PostAsJsonAsync(ActivationPath, new { pollingHandle = d.Handle, proof }, ct);
        act.StatusCode.ShouldBe(HttpStatusCode.OK);
        d.ClientId = JsonDocument.Parse(await act.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;
    }

    private async Task<HttpResponseMessage> TokenRequestAsync(PlatformApiFactory f, HttpClient anon, Device d, CancellationToken ct)
    {
        var now = f.Time.GetUtcNow();
        var assertion = d.Manager.ClientAssertion(now, Base + TokenPath, d.ClientId);
        _secrets.Add(assertion);
        var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
                ["client_id"] = d.ClientId,
            }),
        };
        request.Headers.Add("DPoP", d.Manager.DPoPProof(now, "POST", Base + TokenPath));
        return await anon.SendAsync(request, ct);
    }

    private async Task<string> TokenAsync(PlatformApiFactory f, HttpClient anon, Device d, CancellationToken ct)
    {
        using var r = await TokenRequestAsync(f, anon, d, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!;
        _secrets.Add(token);
        return token;
    }

    private async Task<HttpResponseMessage> PostWithTokenAsync(PlatformApiFactory f, HttpClient anon, Device d, string path, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + token);
        request.Headers.Add("DPoP", d.Manager.DPoPProof(f.Time.GetUtcNow(), "POST", Base + path, token));
        return await anon.SendAsync(request, ct);
    }

    private async Task KeepAsync(HttpResponseMessage r, CancellationToken ct)
    {
        _bodies.Add(await r.Content.ReadAsStringAsync(ct));
        r.Dispose();
    }

    private static async Task<List<string>> QueryAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public async Task EveryLifecycleDecisionIsAuditedOnceAndNoSecretIsStoredOrLogged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var f = RegistryTestHost.Create(db, softwareKeys: SoftwareKeyPolicy.Refused);
        await f.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        using var admin = await ApiSession.SignInAsync(f, "admin-1", TestMembers.DefaultPassword, ct);
        using var registrar = await ApiSession.SignInAsync(f, "reg-1", TestMembers.DefaultPassword, ct);
        using var anon = f.CreateApiClient();
        using var m1 = new TestAnalystManager("m1");
        using var m2 = new TestAnalystManager("m2");
        using var m3 = new TestAnalystManager("m3");
        using var m4 = new TestAnalystManager("m4");
        using var m5 = new TestAnalystManager("m5");

        await KeepAsync(await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(m5, "software"), ct), ct);

        // Device 1: mismatch, denial, approval, activation refusal and success, token, unregistration.
        var d1 = await PairAsync(anon, m1, ct);
        await KeepAsync(await admin.SendAsync(HttpMethod.Post, Path, new { pairingCode = d1.Code, deviceFingerprint = "AAAA-BBBB" }, ct), ct);
        await SubmitAsync(admin, d1, ct);
        await KeepAsync(await registrar.SendAsync(HttpMethod.Post, $"{Path}/{d1.Id}/approval", null, ct), ct);
        using (var ok = await admin.SendAsync(HttpMethod.Post, $"{Path}/{d1.Id}/approval", null, ct))
        {
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await ChallengeAsync(anon, d1, ct);
        await KeepAsync(await anon.PostAsJsonAsync(ActivationPath, new { pollingHandle = d1.Handle, proof = "x" }, ct), ct);
        var challenge = await ChallengeAsync(anon, d1, ct);
        var proof = m1.ActivationProof(f.Time.GetUtcNow(), Base + ActivationPath, challenge);
        _secrets.Add(proof);
        using (var act = await anon.PostAsJsonAsync(ActivationPath, new { pollingHandle = d1.Handle, proof }, ct))
        {
            act.StatusCode.ShouldBe(HttpStatusCode.OK);
            d1.ClientId = JsonDocument.Parse(await act.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;
        }

        var token1 = await TokenAsync(f, anon, d1, ct);
        await KeepAsync(await PostWithTokenAsync(f, anon, d1, BeginPath, token1, ct), ct);
        await KeepAsync(await PostWithTokenAsync(f, anon, d1, BeginPath + "/completion", token1, ct), ct);

        // Device 2: activation, token, revocation, refused token.
        var d2 = await PairAsync(anon, m2, ct);
        await ActivateAsync(f, anon, admin, d2, ct);
        await TokenAsync(f, anon, d2, ct);
        await KeepAsync(await admin.SendAsync(HttpMethod.Post, $"{Path}/{d2.Id}/revocation", new { reason = "device lost" }, ct), ct);
        await KeepAsync(await TokenRequestAsync(f, anon, d2, ct), ct);

        // Device 3: rejection.
        var d3 = await PairAsync(anon, m3, ct);
        await SubmitAsync(admin, d3, ct);
        await KeepAsync(await admin.SendAsync(HttpMethod.Post, $"{Path}/{d3.Id}/rejection", new { reason = "unknown device" }, ct), ct);

        // Device 4: pairing window expiry.
        await PairAsync(anon, m4, ct);
        f.Time.Advance(TimeSpan.FromMinutes(11));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ExpireDueRegistrationsHandler>().RunAsync(ct)).Expired.ShouldBe(1);
        }

        var counts = new Dictionary<string, int>
        {
            ["analyst-manager.pairing-issued"] = 4,
            ["analyst-manager.pairing-refused"] = 1,
            ["analyst-manager.pending-request-created"] = 3,
            ["analyst-manager.submission-refused"] = 1,
            ["analyst-manager.approved"] = 2,
            ["analyst-manager.rejected"] = 1,
            ["analyst-manager.expired"] = 1,
            ["analyst-manager.activation-refused"] = 1,
            ["analyst-manager.activated"] = 2,
            ["analyst-manager.credential-issued"] = 2,
            ["analyst-manager.credential-refused"] = 1,
            ["analyst-manager.unregistration-started"] = 1,
            ["analyst-manager.unregistered"] = 1,
            ["analyst-manager.revoked"] = 1,
        };
        var actual = (await QueryAsync(db,
            "SELECT event_type || '=' || count(*) FROM socalytics.security_audit_event WHERE event_type LIKE 'analyst-manager.%' GROUP BY event_type", ct))
            .ToDictionary(x => x.Split('=')[0], x => int.Parse(x.Split('=')[1]));
        actual.OrderBy(x => x.Key).ShouldBe(counts.OrderBy(x => x.Key));

        var allowed = RegistryAuditEvents.AllowedDetailKeys;
        await using (var c = new NpgsqlConnection(db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT event_type, actor_kind, actor_account_id, resource_id, reason_code, correlation_id, outcome, " +
                                "(SELECT array_agg(k) FROM jsonb_object_keys(details) k) FROM socalytics.security_audit_event WHERE event_type LIKE 'analyst-manager.%'", c);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(0);
                if (reader.GetString(1) == "member")
                {
                    reader.IsDBNull(2).ShouldBeFalse($"{type}: actor id");
                }

                // A refused pairing creates no registration, so it has no resource id.
                if (type != "analyst-manager.pairing-refused")
                {
                    reader.IsDBNull(3).ShouldBeFalse($"{type}: resource id");
                }

                reader.GetString(5).ShouldNotBeNullOrWhiteSpace($"{type}: correlation id");
                if (reader.GetString(6) == "refused")
                {
                    reader.IsDBNull(4).ShouldBeFalse($"{type}: reason code");
                }

                foreach (var key in reader.IsDBNull(7) ? [] : reader.GetFieldValue<string[]>(7))
                {
                    allowed.ShouldContain(key, $"{type}: detail key");
                }
            }
        }

        var stored = string.Join("\n", await QueryAsync(db, "SELECT row_to_json(a)::text FROM socalytics.security_audit_event a", ct))
            + string.Join("\n", await QueryAsync(db, "SELECT row_to_json(r)::text FROM socalytics.analyst_manager_registration r", ct))
            + string.Join("\n", await QueryAsync(db, "SELECT row_to_json(t)::text FROM socalytics.analyst_manager_access_token t", ct));
        var logs = f.Logs.Entries.Select(e => e.Message + " " + string.Join(" ", e.State.Values) + " " + e.Exception).ToList();
        var logText = string.Join("\n", logs);
        _secrets.Count.ShouldBeGreaterThan(10);
        foreach (var secret in _secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct())
        {
            stored.ShouldNotContain(secret);
            logText.ShouldNotContain(secret);
            foreach (var body in _bodies)
            {
                body.ShouldNotContain(secret);
            }
        }
    }
}
