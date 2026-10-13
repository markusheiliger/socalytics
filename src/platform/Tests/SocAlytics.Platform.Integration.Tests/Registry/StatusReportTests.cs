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

public sealed class StatusReportTests(PostgresContainerFixture postgres)
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string SelfPath = "/api/v1/analyst-managers/self/registration";
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

    [Fact]
    public async Task ValidReportIsStoredAndOwnRegistrationReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using var r = await ReportAsync(s, Report("paused", "2026-10-12T18:00:00Z"), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        r.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        root.GetProperty("managerId").GetString().ShouldBe(s.ClientId);
        root.GetProperty("state").GetString().ShouldBe("active");
        root.GetProperty("reportAccepted").GetBoolean().ShouldBeTrue();
        root.GetProperty("reportViolations").GetArrayLength().ShouldBe(0);
        root.GetProperty("statusCheckIntervalSeconds").GetInt32().ShouldBe(60);

        (await ScalarAsync<string>(s, "SELECT operating_state FROM socalytics.analyst_manager_status", ct)).ShouldBe("paused");
        (await ScalarAsync<int>(s, "SELECT active_analysts FROM socalytics.analyst_manager_status", ct)).ShouldBe(1);
        (await ScalarAsync<int>(s, "SELECT interval_seconds FROM socalytics.analyst_manager_status", ct)).ShouldBe(60);
    }

    [Fact]
    public async Task IntervalIsReturnedByBothResponses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using var self = await SelfAsync(s, ct);
        self.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await self.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("statusCheckIntervalSeconds").GetInt32().ShouldBe(60);
    }

    [Fact]
    public async Task NewReportOverwritesWithoutHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using (var first = await ReportAsync(s, Report("running", active: 2), ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var second = await ReportAsync(s, Report("runtime-unavailable", reason: "runtime-missing", active: 0), ct))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await ScalarAsync<long>(s, "SELECT count(*) FROM socalytics.analyst_manager_status", ct)).ShouldBe(1);
        (await ScalarAsync<string>(s, "SELECT operating_state FROM socalytics.analyst_manager_status", ct)).ShouldBe("runtime-unavailable");
        (await ScalarAsync<string>(s, "SELECT runtime_unavailable_reason FROM socalytics.analyst_manager_status", ct)).ShouldBe("runtime-missing");
    }

    [Fact]
    public async Task InvalidReportIsRefusedWhileRegistrationIsReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using (var good = await ReportAsync(s, Report("running", active: 1), ct))
        {
            good.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var bad = await ReportAsync(s, Report("running", active: 5, max: 2), ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.OK, await bad.Content.ReadAsStringAsync(ct));
        var root = JsonDocument.Parse(await bad.Content.ReadAsStringAsync(ct)).RootElement;
        root.GetProperty("managerId").GetString().ShouldBe(s.ClientId);
        root.GetProperty("reportAccepted").GetBoolean().ShouldBeFalse();
        root.GetProperty("reportViolations").EnumerateArray().Select(v => v.GetProperty("field").GetString()).ShouldBe(["activeAnalysts"]);
        root.GetProperty("statusCheckIntervalSeconds").GetInt32().ShouldBe(60);
        (await ScalarAsync<int>(s, "SELECT active_analysts FROM socalytics.analyst_manager_status", ct)).ShouldBe(1);

        using var unknown = await ReportAsync(s, Report("sleeping"), ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("reportAccepted").GetBoolean().ShouldBeFalse();

        using var pausedUntilOnRunning = await ReportAsync(s, Report("running", "2026-10-12T18:00:00Z"), ct);
        JsonDocument.Parse(await pausedUntilOnRunning.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("reportAccepted").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task RevokedManagerIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using var revoke = await s.Admin.SendAsync(HttpMethod.Post, $"{Path}/{s.Id}/revocation", new { reason = "device lost" }, ct);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await revoke.Content.ReadAsStringAsync(ct));

        using var r = await ReportAsync(s, Report(), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("code").GetString().ShouldBe("analyst-manager-registration-inactive");
        (await ScalarAsync<long>(s, "SELECT count(*) FROM socalytics.analyst_manager_status", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task StoredReportTimeIsThePlatformReceiptTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s0 = await StartAsync(ct);
        s0.Factory.Time.Advance(TimeSpan.FromMinutes(5));
        var s = s0 with { Token = await TokenAsync(s0, ct) };
        var expected = s.Factory.Time.GetUtcNow();

        // The Manager's own clock (here a pause end in the far past) never influences the stored time.
        using var r = await ReportAsync(s, Report("paused", "2001-01-01T00:00:00Z"), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        var received = await ScalarAsync<DateTime>(s, "SELECT received_at FROM socalytics.analyst_manager_status", ct);
        new DateTimeOffset(DateTime.SpecifyKind(received, DateTimeKind.Utc)).ShouldBe(expected, TimeSpan.FromMilliseconds(1));
    }
}
