using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Infrastructure.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class SignInHandlerTests(PostgresContainerFixture postgres)
{
    private const string Password = "correct-horse-battery";

    private sealed class Fixture(IsolatedDatabase db, MutableTimeProvider time, ServiceProvider provider, CountingPasswordHasher hasher) : IAsyncDisposable
    {
        public MutableTimeProvider Time => time;

        public CountingPasswordHasher Hasher => hasher;

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await db.DisposeAsync();
        }

        public async Task AddAccountAsync(string name, string? password, string status, CancellationToken ct)
        {
            var hash = password is null ? null : new Microsoft.AspNetCore.Identity.PasswordHasher<IdentityMemberAccount>()
                .HashPassword(new IdentityMemberAccount(), password);
            await ExecuteAsync(
                "INSERT INTO socalytics.member_account (id, account_name, normalized_account_name, password_hash, security_stamp, " +
                "membership_status, membership_changed_at, created_at) VALUES (@id, @n, @nn, @h, 'stamp-1', @s, @now, @now)",
                ct,
                ("id", Guid.NewGuid()), ("n", name), ("nn", name.ToUpperInvariant()), ("h", (object?)hash ?? DBNull.Value),
                ("s", status), ("now", time.GetUtcNow()));
        }

        public async Task<OperationResult<SignedInSession>> SignInAsync(string name, string password, byte[]? presented, CancellationToken ct)
        {
            await using var scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SignInHandler>()
                .HandleAsync(new SignInCommand(name, password, presented), ct);
        }

        public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct)
        {
            await using var c = new NpgsqlConnection(db.AppConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, c);
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? default : (T)value;
        }

        private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
        {
            await using var c = new NpgsqlConnection(db.AppConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, c);
            foreach (var (n, v) in args)
            {
                cmd.Parameters.AddWithValue(n, v);
            }

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<Fixture> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var time = new MutableTimeProvider();
        var hasher = new CountingPasswordHasher();
        return new Fixture(db, time, PlatformServices.Build(db, time: time, hasher: hasher), hasher);
    }

    [Fact]
    // Quickstart A9
    public async Task EveryRefusalIsIdenticalAndVerifiesExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        await f.AddAccountAsync("alice", Password, "active", ct);
        await f.AddAccountAsync("inactive", Password, "deactivated", ct);
        await f.AddAccountAsync("nopass", null, "active", ct);
        await f.AddAccountAsync("locked", Password, "active", ct);
        for (var i = 0; i < 5; i++)
        {
            (await f.SignInAsync("locked", "wrong-password-1", null, ct)).IsSuccess.ShouldBeFalse();
        }

        foreach (var (name, password) in new[]
                 {
                     ("ghost", Password), ("alice", "wrong-password-1"), ("locked", Password),
                     ("inactive", Password), ("nopass", Password),
                 })
        {
            var before = f.Hasher.Verifications;
            var result = await f.SignInAsync(name, password, null, ct);
            result.IsSuccess.ShouldBeFalse();
            result.Failure.ShouldBe(OperationFailure.SignInFailed());
            (f.Hasher.Verifications - before).ShouldBe(1, name);
        }
    }

    [Fact]
    public async Task EveryRefusalClassWritesOneAuditRowViaTheSharedStatementAndTouchesAccountsOnlyOnWrongPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        await f.AddAccountAsync("alice", Password, "active", ct);
        await f.AddAccountAsync("inactive", Password, "deactivated", ct);
        await f.AddAccountAsync("nopass", null, "active", ct);
        await f.AddAccountAsync("locked", Password, "active", ct);
        for (var i = 0; i < 5; i++)
        {
            await f.SignInAsync("locked", "wrong-password-1", null, ct);
        }

        foreach (var (name, password, reason, touches) in new[]
                 {
                     ("ghost", Password, "unknown-account", false),
                     ("alice", "wrong-password-1", "wrong-password", true),
                     ("locked", Password, "locked-out", false),
                     ("inactive", Password, "inactive-membership", false),
                     ("nopass", Password, "no-password", false),
                 })
        {
            var rowsBefore = await f.ScalarAsync<long>("SELECT count(*) FROM socalytics.security_audit_event WHERE reason_code = '" + reason + "'", ct);
            var versionBefore = await f.ScalarAsync<string>("SELECT string_agg(version::text, ',' ORDER BY account_name) FROM socalytics.member_account", ct);
            (await f.SignInAsync(name, password, null, ct)).IsSuccess.ShouldBeFalse();
            (await f.ScalarAsync<long>("SELECT count(*) FROM socalytics.security_audit_event WHERE reason_code = '" + reason + "'", ct))
                .ShouldBe(rowsBefore + 1, name);
            var versionAfter = await f.ScalarAsync<string>("SELECT string_agg(version::text, ',' ORDER BY account_name) FROM socalytics.member_account", ct);
            (versionAfter != versionBefore).ShouldBe(touches, name);
        }

        (await f.ScalarAsync<long>("SELECT count(*) FROM socalytics.security_audit_event WHERE resource_type = 'session' AND resource_id IS NOT NULL", ct)).ShouldBe(0);
    }

    [Fact]
    // Quickstart A56
    public async Task LockedAccountStaysUnchangedAndUnlocksAtOriginalTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        await f.AddAccountAsync("alice", Password, "active", ct);
        for (var i = 0; i < 5; i++)
        {
            await f.SignInAsync("alice", "wrong-password-1", null, ct);
        }

        var lockoutEnd = await f.ScalarAsync<DateTime>("SELECT lockout_end FROM socalytics.member_account WHERE account_name = 'alice'", ct);
        var count = await f.ScalarAsync<int>("SELECT access_failed_count FROM socalytics.member_account WHERE account_name = 'alice'", ct);
        (await f.ScalarAsync<long>("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'account.locked-out'", ct)).ShouldBe(1);

        f.Time.Advance(TimeSpan.FromMinutes(5));
        (await f.SignInAsync("alice", Password, null, ct)).IsSuccess.ShouldBeFalse();
        (await f.SignInAsync("alice", "wrong-password-1", null, ct)).IsSuccess.ShouldBeFalse();

        (await f.ScalarAsync<DateTime>("SELECT lockout_end FROM socalytics.member_account WHERE account_name = 'alice'", ct)).ShouldBe(lockoutEnd);
        (await f.ScalarAsync<int>("SELECT access_failed_count FROM socalytics.member_account WHERE account_name = 'alice'", ct)).ShouldBe(count);

        f.Time.Advance(TimeSpan.FromMinutes(10));
        (await f.SignInAsync("alice", Password, null, ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task SuccessCreatesSessionAndEndsPresentedSessionAsReplaced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        await f.AddAccountAsync("alice", Password, "active", ct);

        var first = await f.SignInAsync("alice", Password, null, ct);
        first.IsSuccess.ShouldBeTrue();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(first.Value.RawToken));

        var second = await f.SignInAsync("alice", Password, hash, ct);
        second.IsSuccess.ShouldBeTrue();
        second.Value.SessionId.ShouldNotBe(first.Value.SessionId);

        (await f.ScalarAsync<string>(
            $"SELECT end_reason FROM socalytics.member_session WHERE id = '{first.Value.SessionId}'", ct)).ShouldBe("replaced");
        (await f.ScalarAsync<long>(
            "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'session.sign-in' AND outcome = 'succeeded'", ct)).ShouldBe(2);
    }
}
