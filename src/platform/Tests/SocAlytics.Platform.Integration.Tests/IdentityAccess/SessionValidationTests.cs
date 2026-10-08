using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class SessionValidationTests(PostgresContainerFixture postgres)
{
    private const string RawToken = "raw-session-token";

    private sealed record Fixture(IsolatedDatabase Db, MutableTimeProvider Time, ServiceProvider Provider, Guid AccountId, Guid SessionId) : IAsyncDisposable
    {
        public byte[] Hash => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RawToken));

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await Db.DisposeAsync();
        }

        public async Task<OperationResult<ValidatedSession>> ValidateAsync(CancellationToken ct)
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ValidateSessionHandler>()
                .HandleAsync(new ValidateSessionQuery(Hash), ct);
        }

        public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct)
        {
            await using var c = new NpgsqlConnection(Db.AppConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, c);
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? default : (T)value;
        }

        public async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var c = new NpgsqlConnection(Db.AppConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, c);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<Fixture> CreateAsync(bool passwordChangeRequired, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        var time = new MutableTimeProvider();
        var provider = PlatformServices.Build(db, time: time);

        var accountId = Guid.NewGuid();
        await using (var c = new NpgsqlConnection(db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO socalytics.member_account (id, account_name, normalized_account_name, security_stamp, " +
                "password_change_required, membership_status, membership_changed_at, created_at) " +
                "VALUES (@id, 'sessionuser', 'SESSIONUSER', 'stamp-1', @pcr, 'active', @now, @now)", c);
            cmd.Parameters.AddWithValue("id", accountId);
            cmd.Parameters.AddWithValue("pcr", passwordChangeRequired);
            cmd.Parameters.AddWithValue("now", time.GetUtcNow());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Guid sessionId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var work = await uow.BeginAsync(ct);
            var created = await scope.ServiceProvider.GetRequiredService<ISessionStore>().CreateAsync(
                accountId,
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RawToken)),
                "stamp-1",
                time.GetUtcNow(),
                ct);
            await work.CommitAsync(ct);
            sessionId = created.SessionId;
        }

        return new Fixture(db, time, provider, accountId, sessionId);
    }

    [Fact]
    public async Task FreshSessionValidatesAndReportsPasswordChangeRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(passwordChangeRequired: true, ct);

        var result = await f.ValidateAsync(ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SessionId.ShouldBe(f.SessionId);
        result.Value.AccountId.ShouldBe(f.AccountId);
        result.Value.PasswordChangeRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task SessionFailsAfterIdleTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(false, ct);

        f.Time.Advance(TimeSpan.FromMinutes(30));

        (await f.ValidateAsync(ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task SessionFailsAfterAbsoluteLifetimeEvenWhenActive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(false, ct);

        for (var i = 0; i < 16; i++)
        {
            f.Time.Advance(TimeSpan.FromMinutes(29));
            (await f.ValidateAsync(ct)).IsSuccess.ShouldBeTrue();
        }

        f.Time.Advance(TimeSpan.FromMinutes(29));
        (await f.ValidateAsync(ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task SlidingHappensAtMostOncePerSixtySeconds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(false, ct);
        var start = f.Time.GetUtcNow();

        f.Time.Advance(TimeSpan.FromSeconds(30));
        (await f.ValidateAsync(ct)).IsSuccess.ShouldBeTrue();
        (await f.ScalarAsync<DateTime>("SELECT last_seen_at FROM socalytics.member_session", ct)).ShouldBe(start.UtcDateTime);

        f.Time.Advance(TimeSpan.FromSeconds(31));
        (await f.ValidateAsync(ct)).IsSuccess.ShouldBeTrue();
        (await f.ScalarAsync<DateTime>("SELECT last_seen_at FROM socalytics.member_session", ct))
            .ShouldBe(f.Time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task EndedSessionIsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(false, ct);
        await f.ExecuteAsync("UPDATE socalytics.member_session SET ended_at = now(), end_reason = 'replaced'", ct);

        (await f.ValidateAsync(ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task DeactivatedAccountInvalidatesSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(false, ct);
        await f.ExecuteAsync("UPDATE socalytics.member_account SET membership_status = 'deactivated'", ct);

        (await f.ValidateAsync(ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task RotatedSecurityStampInvalidatesSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(false, ct);
        await f.ExecuteAsync("UPDATE socalytics.member_account SET security_stamp = 'stamp-2'", ct);

        (await f.ValidateAsync(ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task SignOutEndsSessionAndRecordsAudit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(false, ct);

        await using (var scope = f.Provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TestRequestContext>();
            context.ActorKind = AuditActorKind.Member;
            context.MemberAccountId = f.AccountId;
            context.SessionId = f.SessionId;
            (await scope.ServiceProvider.GetRequiredService<SignOutHandler>()
                .HandleAsync(new SignOutCommand(f.SessionId), ct)).IsSuccess.ShouldBeTrue();
        }

        (await f.ScalarAsync<string>("SELECT end_reason FROM socalytics.member_session", ct)).ShouldBe("sign-out");
        (await f.ScalarAsync<long>("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'session.sign-out'", ct))
            .ShouldBe(1);
        (await f.ValidateAsync(ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task StoredTokenHashDiffersFromRawToken()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(false, ct);

        var stored = await f.ScalarAsync<byte[]>("SELECT token_hash FROM socalytics.member_session", ct);

        stored.ShouldNotBeNull();
        stored.ShouldNotBe(System.Text.Encoding.UTF8.GetBytes(RawToken));
        stored.ShouldBe(f.Hash);
    }
}
