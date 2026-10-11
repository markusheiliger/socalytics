using System.Net;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class DecisionTests(PostgresContainerFixture postgres)
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

    private static async Task<Guid> PendingAsync(PlatformApiFactory factory, ApiSession submitter, string protection, CancellationToken ct)
    {
        using var manager = new TestAnalystManager();
        using var anon = factory.CreateApiClient();
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager, protection), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        using var ok = await submitter.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        return JsonDocument.Parse(await ok.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
    }

    [Fact]
    public async Task ApproveRejectAndRefusals()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var registrar = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);

        var id = await PendingAsync(factory, registrar, "tpm", ct);
        using var denied = await registrar.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var deniedReject = await registrar.SendAsync(HttpMethod.Post, $"{Path}/{id}/rejection", new { reason = "no" }, ct);
        deniedReject.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied'", ct)).ShouldBe(2);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'pending_approval'", ct)).ShouldBe(1);

        using var noReason = await admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/rejection", new { reason = "" }, ct);
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var approve = await admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync(ct));
        JsonDocument.Parse(await approve.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("state").GetString().ShouldBe("approved");
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.approved'", ct)).ShouldBe(1);

        using var again = await admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/rejection", new { reason = "late" }, ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var second = await PendingAsync(factory, admin, "software", ct);
        using var reject = await admin.SendAsync(HttpMethod.Post, $"{Path}/{second}/rejection", new { reason = "unknown device" }, ct);
        reject.StatusCode.ShouldBe(HttpStatusCode.OK, await reject.Content.ReadAsStringAsync(ct));
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'rejected' AND decision_reason = 'unknown device' AND ended_at IS NOT NULL", ct)).ShouldBe(1);
        using var reapprove = await admin.SendAsync(HttpMethod.Post, $"{Path}/{second}/approval", null, ct);
        reapprove.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.rejected'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ApprovalAfterWindowExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var id = await PendingAsync(factory, admin, "tpm", ct);

        factory.Time.Advance(TimeSpan.FromHours(25));
        using var lateAdmin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var late = await lateAdmin.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await late.Content.ReadAsStringAsync(ct)).ShouldContain("analyst-manager-registration-expired");
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'expired'", ct)).ShouldBe(1);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.expired'", ct)).ShouldBe(1);
    }
}
