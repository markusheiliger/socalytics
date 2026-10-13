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

public sealed class FleetReadTests(PostgresContainerFixture postgres)
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string SelfPath = "/api/v1/analyst-managers/self/registration";
    private const string BeginPath = "/api/v1/analyst-managers/self/unregistration";
    private const string CompletePath = BeginPath + "/completion";
    private const string StatusPath = "/api/v1/analyst-managers/self/status";
    private const string Path = "/api/v1/analyst-manager-registrations";
    private const string Origin = "https://localhost";

    private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, HttpClient Anon, ApiSession Admin, TestAnalystManager Manager, string ClientId, Guid Id, string Token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
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
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
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
        var s = new Setup(db, factory, anon, admin, manager, clientId, id, "");
        return s with { Token = await TokenAsync(s, ct) };
    }

    private static async Task<string> TokenAsync(Setup s, CancellationToken ct)
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
        using var r = await s.Anon.SendAsync(request, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!;
    }

    private static Task<HttpResponseMessage> ReportAsync(Setup s, object body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, StatusPath) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + s.Token);
        request.Headers.Add("DPoP", s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "POST", Origin + StatusPath, s.Token));
        return s.Anon.SendAsync(request, ct);
    }

    private static Task<HttpResponseMessage> SelfAsync(Setup s, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, SelfPath);
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + s.Token);
        request.Headers.Add("DPoP", s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "GET", Origin + SelfPath, s.Token));
        return s.Anon.SendAsync(request, ct);
    }

    private static object Report(string state = "running", string? pausedUntil = null, string? reason = null, int active = 1, int max = 2) => new
    {
        operatingState = state,
        pausedUntil,
        runtimeUnavailableReason = reason,
        activeAnalysts = active,
        maxAnalysts = max,
        managerVersion = "0.3.0",
    };

    private static async Task<T> ScalarAsync<T>(Setup s, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(s.Db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static Task<HttpResponseMessage> PostSelfAsync(Setup s, string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + s.Token);
        request.Headers.Add("DPoP", s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "POST", Origin + path, s.Token));
        return s.Anon.SendAsync(request, ct);
    }

    private static async Task<JsonElement> EntryAsync(Setup s, CancellationToken ct)
    {
        using var r = await s.Admin.SendAsync(HttpMethod.Get, $"{Path}/{s.Id}", null, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        var detail = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.Clone();

        using var list = await s.Admin.SendAsync(HttpMethod.Get, Path, null, ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fromList = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("registrationId").GetGuid() == s.Id);
        fromList.GetProperty("connectivity").GetString().ShouldBe(detail.GetProperty("connectivity").GetString());
        fromList.GetProperty("status").ValueKind.ShouldBe(detail.GetProperty("status").ValueKind);
        return detail;
    }

    private static async Task ReportOkAsync(Setup s, object body, CancellationToken ct)
    {
        using var r = await ReportAsync(s, body, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task ReportedManagerIsOnlineWithStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        await ReportOkAsync(s, Report("paused", "2026-10-12T18:00:00Z", active: 1, max: 3), ct);

        var e = await EntryAsync(s, ct);
        e.GetProperty("connectivity").GetString().ShouldBe("online");
        var st = e.GetProperty("status");
        st.GetProperty("operatingState").GetString().ShouldBe("paused");
        st.GetProperty("activeAnalysts").GetInt32().ShouldBe(1);
        st.GetProperty("maxAnalysts").GetInt32().ShouldBe(3);
        st.GetProperty("managerVersion").GetString().ShouldBe("0.3.0");
        st.GetProperty("pausedUntil").ValueKind.ShouldBe(JsonValueKind.String);
        st.GetProperty("reportedAt").GetDateTimeOffset().ShouldBe(s.Factory.Time.GetUtcNow(), TimeSpan.FromSeconds(1));
        e.GetProperty("deviceMetadata").GetProperty("claimedKeyProtection").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task ManagerIsOfflineAfterTheConfiguredMissedIntervals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        await ReportOkAsync(s, Report(), ct);

        s.Factory.Time.Advance(TimeSpan.FromSeconds(180));
        (await EntryAsync(s, ct)).GetProperty("connectivity").GetString().ShouldBe("online");

        s.Factory.Time.Advance(TimeSpan.FromSeconds(1));
        var e = await EntryAsync(s, ct);
        e.GetProperty("connectivity").GetString().ShouldBe("offline");
        e.GetProperty("status").GetProperty("operatingState").GetString().ShouldBe("running");
    }

    [Fact]
    public async Task NeverReportedManagerIsOffline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var e = await EntryAsync(s, ct);
        e.GetProperty("state").GetString().ShouldBe("active");
        e.GetProperty("connectivity").GetString().ShouldBe("offline");
        e.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ChangedIntervalDoesNotChangeEarlierReports()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        await ReportOkAsync(s, Report(), ct);
        s.Factory.Time.Advance(TimeSpan.FromSeconds(400));
        (await EntryAsync(s, ct)).GetProperty("connectivity").GetString().ShouldBe("offline");

        // The row keeps the interval in force at receipt (here 300 s, as if configured then).
        await using (var c = new NpgsqlConnection(s.Db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("UPDATE socalytics.analyst_manager_status SET interval_seconds = 300", c);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        (await EntryAsync(s, ct)).GetProperty("connectivity").GetString().ShouldBe("online");
    }

    [Fact]
    public async Task RevocationDeletesTheReportAndEndedRegistrationsAreNotApplicable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        await ReportOkAsync(s, Report(), ct);
        (await ScalarAsync<long>(s, "SELECT count(*) FROM socalytics.analyst_manager_status", ct)).ShouldBe(1);

        using var revoke = await s.Admin.SendAsync(HttpMethod.Post, $"{Path}/{s.Id}/revocation", new { reason = "device lost" }, ct);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await revoke.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("state").GetString().ShouldBe("revoked");

        (await ScalarAsync<long>(s, "SELECT count(*) FROM socalytics.analyst_manager_status", ct)).ShouldBe(0);
        var e = await EntryAsync(s, ct);
        e.GetProperty("connectivity").GetString().ShouldBe("not-applicable");
        e.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task UnregistrationCompletionDeletesTheReport()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        await ReportOkAsync(s, Report(), ct);

        using (var begin = await PostSelfAsync(s, BeginPath, ct))
        {
            begin.StatusCode.ShouldBe(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync(ct));
        }

        (await EntryAsync(s, ct)).GetProperty("connectivity").GetString().ShouldBe("online");
        (await ScalarAsync<long>(s, "SELECT count(*) FROM socalytics.analyst_manager_status", ct)).ShouldBe(1);

        using (var complete = await PostSelfAsync(s, CompletePath, ct))
        {
            complete.StatusCode.ShouldBe(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync(ct));
        }

        (await ScalarAsync<long>(s, "SELECT count(*) FROM socalytics.analyst_manager_status", ct)).ShouldBe(0);
        var e = await EntryAsync(s, ct);
        e.GetProperty("state").GetString().ShouldBe("unregistered");
        e.GetProperty("connectivity").GetString().ShouldBe("not-applicable");
    }

    [Fact]
    public async Task ApprovalAndRejectionAreUnchangedAndNotApplicable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var other = new TestAnalystManager();
        using var pair = await RegistryTestHost.PairAsync(s.Anon, RegistryTestHost.PairingBody(other), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        using var sub = await s.Admin.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        sub.StatusCode.ShouldBe(HttpStatusCode.Created);
        var pending = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement;
        var id = pending.GetProperty("registrationId").GetGuid();
        pending.GetProperty("state").GetString().ShouldBe("pending_approval");
        pending.TryGetProperty("connectivity", out _).ShouldBeFalse();

        using var detail = await s.Admin.SendAsync(HttpMethod.Get, $"{Path}/{id}", null, ct);
        var d = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(ct)).RootElement;
        d.GetProperty("connectivity").GetString().ShouldBe("not-applicable");
        d.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);

        using var reject = await s.Admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/rejection", new { reason = "unknown device" }, ct);
        reject.StatusCode.ShouldBe(HttpStatusCode.OK, await reject.Content.ReadAsStringAsync(ct));
        JsonDocument.Parse(await reject.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("state").GetString().ShouldBe("rejected");
        other.Dispose();
    }
}
