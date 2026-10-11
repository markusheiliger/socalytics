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

public sealed class ActivationTests(PostgresContainerFixture postgres)
{
    private const string Path = "/api/v1/analyst-manager-registrations";
    private const string ActivationPath = "/api/v1/analyst-managers/pairings/activation";

    private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, ApiSession Admin, HttpClient Anon, TestAnalystManager Manager, string Handle, Guid Id) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Anon.Dispose();
            Admin.Dispose();
            Manager.Dispose();
            await Factory.DisposeAsync();
            await Db.DisposeAsync();
        }

        public string Aud => "https://localhost" + ActivationPath;
    }

    private async Task<Setup> StartAsync(bool approve, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = RegistryTestHost.Create(db);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var manager = new TestAnalystManager();
        var anon = factory.CreateApiClient();
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager, "tpm"), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        using var sub = await admin.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        sub.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        if (approve)
        {
            using var ok = await admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return new Setup(db, factory, admin, anon, manager, root.GetProperty("pollingHandle").GetString()!, id);
    }

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<string> ChallengeAsync(Setup s, CancellationToken ct)
    {
        using var r = await RegistryTestHost.StatusAsync(s.Anon, s.Handle, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        root.GetProperty("status").GetString().ShouldBe("approved");
        return root.GetProperty("challenge").GetString()!;
    }

    private static Task<HttpResponseMessage> ActivateAsync(Setup s, string proof, CancellationToken ct) =>
        s.Anon.PostAsJsonAsync(ActivationPath, new { pollingHandle = s.Handle, proof }, ct);

    private static Task<long> AuditAsync(Setup s, string type, CancellationToken ct) =>
        CountAsync(s.Db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{type}'", ct);

    [Fact]
    public async Task HappyPathActivates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(true, ct);
        var first = await ChallengeAsync(s, ct);
        var second = await ChallengeAsync(s, ct);
        second.ShouldNotBe(first);

        using var stale = await ActivateAsync(s, s.Manager.ActivationProof(s.Factory.Time.GetUtcNow(), s.Aud, first), ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var latest = await ChallengeAsync(s, ct);

        using var ok = await ActivateAsync(s, s.Manager.ActivationProof(s.Factory.Time.GetUtcNow(), s.Aud, latest), ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(ct));
        var root = JsonDocument.Parse(await ok.Content.ReadAsStringAsync(ct)).RootElement;
        Guid.Parse(root.GetProperty("managerId").GetString()!).ShouldNotBe(Guid.Empty);
        root.GetProperty("keyThumbprint").GetString().ShouldBe(s.Manager.Thumbprint);
        (await CountAsync(s.Db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'active' AND manager_id IS NOT NULL", ct)).ShouldBe(1);
        (await AuditAsync(s, "analyst-manager.activated", ct)).ShouldBe(1);
        (await AuditAsync(s, "analyst-manager.activation-refused", ct)).ShouldBe(1);

        using var after = await RegistryTestHost.StatusAsync(s.Anon, s.Handle, ct);
        after.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var again = await ActivateAsync(s, s.Manager.ActivationProof(s.Factory.Time.GetUtcNow(), s.Aud, latest), ct);
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefusedProofsLeaveRegistrationApproved()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(true, ct);
        using var other = new TestAnalystManager();
        var now = () => s.Factory.Time.GetUtcNow();
        var attempts = new Func<string, string>[]
        {
            c => s.Manager.ActivationProof(now(), s.Aud, "wrong-challenge"),
            c => s.Manager.ActivationProof(now().AddHours(-1), s.Aud, c),
            c => s.Manager.ActivationProof(now(), "https://localhost/other", c),
            c => s.Manager.ActivationProof(now(), s.Aud, c, signer: other),
            c => s.Manager.ActivationProof(now(), s.Aud, c, header: h => h["jwk"] = s.Manager.PublicJwkObject()),
        };
        foreach (var build in attempts)
        {
            var challenge = await ChallengeAsync(s, ct);
            using var r = await ActivateAsync(s, build(challenge), ct);
            r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await r.Content.ReadAsStringAsync(ct)).ShouldContain("analyst-manager-activation-proof-invalid");
        }

        var replayChallenge = await ChallengeAsync(s, ct);
        var proof = s.Manager.ActivationProof(now(), s.Aud, "wrong", jti: "same-jti");
        using var a = await ActivateAsync(s, proof, ct);
        a.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var b = await ActivateAsync(s, proof, ct);
        b.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var consumed = await ChallengeAsync(s, ct);
        using var first = await ActivateAsync(s, s.Manager.ActivationProof(now(), s.Aud, consumed), ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        _ = replayChallenge;
        (await AuditAsync(s, "analyst-manager.activation-refused", ct)).ShouldBe(attempts.Length + 2);
    }

    [Fact]
    public async Task ConsumedChallengeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(true, ct);
        var challenge = await ChallengeAsync(s, ct);
        using var bad = await ActivateAsync(s, s.Manager.ActivationProof(s.Factory.Time.GetUtcNow(), s.Aud, "other"), ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var consumed = await ActivateAsync(s, s.Manager.ActivationProof(s.Factory.Time.GetUtcNow(), s.Aud, challenge), ct);
        consumed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountAsync(s.Db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'approved'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ActivationWindowElapsedExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(true, ct);
        var challenge = await ChallengeAsync(s, ct);
        s.Factory.Time.Advance(TimeSpan.FromMinutes(11));
        using var r = await ActivateAsync(s, s.Manager.ActivationProof(s.Factory.Time.GetUtcNow(), s.Aud, challenge), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await r.Content.ReadAsStringAsync(ct)).ShouldContain("analyst-manager-registration-expired");
        (await CountAsync(s.Db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'expired'", ct)).ShouldBe(1);
        (await AuditAsync(s, "analyst-manager.expired", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ActivationOfPendingApprovalIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(false, ct);
        using var r = await ActivateAsync(s, s.Manager.ActivationProof(s.Factory.Time.GetUtcNow(), s.Aud, "x"), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var bogus = await s.Anon.PostAsJsonAsync(ActivationPath, new { pollingHandle = "nope", proof = "x" }, ct);
        bogus.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ActivationOfRejectedIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(false, ct);
        using var rej = await s.Admin.SendAsync(HttpMethod.Post, $"{Path}/{s.Id}/rejection", new { reason = "no" }, ct);
        rej.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var r = await ActivateAsync(s, s.Manager.ActivationProof(s.Factory.Time.GetUtcNow(), s.Aud, "x"), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
