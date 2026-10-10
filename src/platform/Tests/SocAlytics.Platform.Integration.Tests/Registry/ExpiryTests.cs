using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Application.Registry.Expiry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class ExpiryTests(PostgresContainerFixture postgres)
{
    private const string Path = "/api/v1/analyst-manager-registrations";

    private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, ApiSession Admin, HttpClient Anon) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Anon.Dispose();
            Admin.Dispose();
            await Factory.DisposeAsync();
            await Db.DisposeAsync();
        }

        public async Task<(string Handle, string Code, string Fingerprint)> PairAsync(CancellationToken ct)
        {
            using var manager = new TestAnalystManager();
            using var pair = await RegistryTestHost.PairAsync(Anon, RegistryTestHost.PairingBody(manager, "tpm"), ct);
            var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
            return (root.GetProperty("pollingHandle").GetString()!, root.GetProperty("pairingCode").GetString()!,
                root.GetProperty("deviceFingerprint").GetString()!);
        }

        public async Task<HttpResponseMessage> SubmitAsync((string Handle, string Code, string Fingerprint) p, CancellationToken ct) =>
            await Admin.SendAsync(HttpMethod.Post, Path, new { pairingCode = p.Code, deviceFingerprint = p.Fingerprint }, ct);

        public async Task<Guid> SubmitIdAsync((string Handle, string Code, string Fingerprint) p, CancellationToken ct)
        {
            using var r = await SubmitAsync(p, ct);
            r.StatusCode.ShouldBe(HttpStatusCode.Created);
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        }

        public async Task<ExpiryRunResult> RunAsync(CancellationToken ct)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ExpireDueRegistrationsHandler>().RunAsync(ct);
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
        return new Setup(db, factory, admin, factory.CreateApiClient());
    }

    private static async Task<object?> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct) =>
        (long)(await ScalarAsync(db, sql, ct))!;

    private static Task<long> ExpiredEventsAsync(Setup s, string reason, CancellationToken ct) =>
        CountAsync(s.Db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.expired' " +
            $"AND reason_code = '{reason}' AND actor_kind = 'system'", ct);

    [Fact]
    public async Task EachWindowExpiresWithItsReasonAndOneEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var pairing = await s.PairAsync(ct);
        var pending = await s.PairAsync(ct);
        await s.SubmitIdAsync(pending, ct);
        var approved = await s.PairAsync(ct);
        var id = await s.SubmitIdAsync(approved, ct);
        using var ok = await s.Admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await s.RunAsync(ct)).Expired.ShouldBe(0);

        s.Factory.Time.Advance(TimeSpan.FromMinutes(11));
        (await s.RunAsync(ct)).Expired.ShouldBe(2);
        (await ExpiredEventsAsync(s, "activation-window-elapsed", ct)).ShouldBe(1);

        s.Factory.Time.Advance(TimeSpan.FromHours(25));
        (await s.RunAsync(ct)).Expired.ShouldBe(1);
        (await s.RunAsync(ct)).Expired.ShouldBe(0);
        (await ExpiredEventsAsync(s, "approval-window-elapsed", ct)).ShouldBe(1);

        (await CountAsync(s.Db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'expired' " +
            "AND ended_at IS NOT NULL AND pairing_code_hash IS NULL AND challenge_hash IS NULL", ct)).ShouldBe(3);
        (await CountAsync(s.Db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.expired'", ct)).ShouldBe(3);

        using var status = await RegistryTestHost.StatusAsync(s.Anon, pairing.Handle, ct);
        status.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        using var admin2 = await ApiSession.SignInAsync(s.Factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var late = await admin2.SendAsync(HttpMethod.Post, Path, new { pairingCode = pending.Code, deviceFingerprint = pending.Fingerprint }, ct);
        late.StatusCode.ShouldNotBe(HttpStatusCode.Created);
        using var approve = await admin2.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
        approve.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var activate = await s.Anon.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation",
            new { pollingHandle = approved.Handle, proof = "x" }, ct);
        activate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ExpiredEventsAsync(s, "pairing-window-elapsed", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentRunsProduceOneEventAndNonDueRowsAreUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        await s.PairAsync(ct);
        var pending = await s.PairAsync(ct);
        await s.SubmitIdAsync(pending, ct);

        s.Factory.Time.Advance(TimeSpan.FromMinutes(11));
        var runs = await Task.WhenAll(s.RunAsync(ct), s.RunAsync(ct));
        runs.Sum(r => r.Expired).ShouldBe(1);
        (await CountAsync(s.Db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analyst-manager.expired'", ct)).ShouldBe(1);
        (await CountAsync(s.Db, "SELECT count(*) FROM socalytics.analyst_manager_registration WHERE state = 'pending_approval'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredReplayRowsArePurged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var now = s.Factory.Time.GetUtcNow();
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var replay = scope.ServiceProvider.GetRequiredService<IProofReplayStore>();
            await using var work = await unit.BeginAsync(ct);
            (await replay.TryRecordAsync("activation", [1], now.AddMinutes(1), ct)).ShouldBeTrue();
            (await replay.TryRecordAsync("activation", [2], now.AddHours(1), ct)).ShouldBeTrue();
            await work.CommitAsync(ct);
        }

        (await s.RunAsync(ct)).PurgedReplayRows.ShouldBe(0);
        s.Factory.Time.Advance(TimeSpan.FromMinutes(5));
        (await s.RunAsync(ct)).PurgedReplayRows.ShouldBe(1);
        (await CountAsync(s.Db, "SELECT count(*) FROM socalytics.analyst_manager_proof_replay", ct)).ShouldBe(1);
    }
}
