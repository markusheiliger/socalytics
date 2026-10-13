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

public sealed class SubmissionTests(PostgresContainerFixture postgres)
{
    private const string Path = "/api/v1/analyst-manager-registrations";

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = RegistryTestHost.Create(db);
        await factory.WaitUntilHealthyAsync(ct);
        return (db, factory);
    }

    private static async Task<(string Code, string Fingerprint)> PairAsync(HttpClient client, TestAnalystManager manager, CancellationToken ct)
    {
        using var response = await RegistryTestHost.PairAsync(client, RegistryTestHost.PairingBody(manager), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        return (root.GetProperty("pairingCode").GetString()!, root.GetProperty("deviceFingerprint").GetString()!);
    }

    [Fact]
    public async Task RegistrarSubmissionAndAdminReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "plain-1", cancellationToken: ct);
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var registrar = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        using var plain = await ApiSession.SignInAsync(factory, "plain-1", TestMembers.DefaultPassword, ct);
        using var manager = new TestAnalystManager();
        using var anon = factory.CreateApiClient();
        var (code, fingerprint) = await PairAsync(anon, manager, ct);

        using var wrong = await registrar.SendAsync(HttpMethod.Post, Path, new { pairingCode = code, deviceFingerprint = "BBBB-BBBB" }, ct);
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var wrongBody = RegistryTestHost.WithoutCorrelation(await wrong.Content.ReadAsStringAsync(ct));
        wrongBody.ShouldContain("analyst-manager-pairing-code-invalid");
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.submission-refused'", ct)).ShouldBe(1);

        using var unknown = await registrar.SendAsync(HttpMethod.Post, Path, new { pairingCode = "ZZZZ-ZZZZ-ZZZZ", deviceFingerprint = fingerprint }, ct);
        RegistryTestHost.WithoutCorrelation(await unknown.Content.ReadAsStringAsync(ct)).ShouldBe(wrongBody);

        using var denied = await plain.SendAsync(HttpMethod.Post, Path, new { pairingCode = code, deviceFingerprint = fingerprint }, ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied'", ct)).ShouldBe(1);

        using var noCsrf = await registrar.Client.PostAsJsonAsync(Path, new { pairingCode = code, deviceFingerprint = fingerprint }, ct);
        noCsrf.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var ok = await registrar.SendAsync(HttpMethod.Post, Path, new { pairingCode = code.ToLowerInvariant(), deviceFingerprint = fingerprint.ToLowerInvariant() }, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync(ct));
        ok.Headers.ETag.ShouldNotBeNull();
        ok.Headers.Location.ShouldNotBeNull();
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.pending-request-created'", ct)).ShouldBe(1);

        using var again = await registrar.SendAsync(HttpMethod.Post, Path, new { pairingCode = code, deviceFingerprint = fingerprint }, ct);
        RegistryTestHost.WithoutCorrelation(await again.Content.ReadAsStringAsync(ct)).ShouldBe(wrongBody);

        var id = JsonDocument.Parse(await ok.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        using var get = await admin.SendAsync(HttpMethod.Get, $"{Path}/{id}", null, ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct)).RootElement;
        view.GetProperty("state").GetString().ShouldBe("pending_approval");
        view.GetProperty("deviceFingerprint").GetString().ShouldBe(fingerprint);
        view.GetProperty("approvalNotice").GetString().ShouldNotBeNullOrEmpty();
        view.GetProperty("deviceMetadata").GetProperty("claimedKeyProtection").GetString().ShouldBe("tpm");
        view.GetProperty("pairingOrigin").TryGetProperty("requestedAt", out var requestedAt).ShouldBeTrue();

        using var list = await admin.SendAsync(HttpMethod.Get, $"{Path}?state=pending_approval", null, ct);
        JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("items").GetArrayLength().ShouldBe(1);

        using var listDenied = await plain.SendAsync(HttpMethod.Get, Path, null, ct);
        listDenied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var missing = await admin.SendAsync(HttpMethod.Get, $"{Path}/{Guid.NewGuid()}", null, ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ThirdMismatchExpiresPairing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        using var registrar = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        using var manager = new TestAnalystManager();
        using var anon = factory.CreateApiClient();
        var (code, fingerprint) = await PairAsync(anon, manager, ct);

        for (var i = 0; i < 3; i++)
        {
            using var r = await registrar.SendAsync(HttpMethod.Post, Path, new { pairingCode = code, deviceFingerprint = "BBBB-BBBB" }, ct);
            r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'expired' AND end_reason = 'fingerprint-mismatch-limit'", ct)).ShouldBe(1);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.expired'", ct)).ShouldBe(1);
        using var late = await registrar.SendAsync(HttpMethod.Post, Path, new { pairingCode = code, deviceFingerprint = fingerprint }, ct);
        late.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
