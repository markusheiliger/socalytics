using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Structure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class SelfServicePasswordRecoveryStoreTests(PostgresContainerFixture postgres)
{
    private const string NewPassword = "a-brand-new-password";
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static async Task<int> ExecuteAsync(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> ScalarAsync<T>(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var value2 = await command.ExecuteScalarAsync(ct);
        return value2 is null or DBNull ? default : (T)value2;
    }

    private static Task SeedAddressAsync(IsolatedDatabase db, Guid accountId, string address, CancellationToken ct)
    {
        EmailAddress.TryCreate(address, out var email, out _).ShouldBeTrue();
        return ExecuteAsync(
            db,
            "INSERT INTO socalytics.member_email_addresses (account_id, address, address_normalized, state, verified_at, set_by, updated_at) " +
            "VALUES (@id, @a, @n, 'verified', @now, @id, @now)",
            ct,
            ("id", accountId), ("a", email!.Value), ("n", email.Normalized), ("now", Now));
    }

    private static Task InsertRecoveryAsync(
        IsolatedDatabase db, Guid id, Guid accountId, string state, byte[]? tokenHash, bool issued, DateTimeOffset? usedAt, CancellationToken ct) =>
        ExecuteAsync(
            db,
            "INSERT INTO socalytics.password_recoveries (id, account_id, address_hash, security_stamp, state, token_hash, requested_at, issued_at, expires_at, used_at) " +
            "VALUES (@id, @acc, 'hash', 'stamp', @state, @token, @now, @issued, @expires, @used)",
            ct,
            ("id", id), ("acc", accountId), ("state", state),
            ("token", (object?)tokenHash ?? DBNull.Value), ("now", Now),
            ("issued", issued ? Now : DBNull.Value), ("expires", issued ? Now.AddHours(1) : DBNull.Value),
            ("used", (object?)usedAt ?? DBNull.Value));

    private static async Task<T> InUnitOfWorkAsync<T>(ServiceProvider provider, Func<IServiceProvider, Task<T>> action, CancellationToken ct)
    {
        await using var scope = provider.CreateAsyncScope();
        await using var work = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var result = await action(scope.ServiceProvider);
        await work.CommitAsync(ct);
        return result;
    }

    [Fact]
    public async Task MigrationAppliesAndTheStructureTestsAcceptTheTableAndColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);

        (await ScalarAsync<long>(db, "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'socalytics' " +
            "AND table_name = 'email_messages' AND column_name = 'password_recovery_id'", ct)).ShouldBe(1);
        (await ScalarAsync<long>(db, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'socalytics' " +
            "AND table_name = 'password_recoveries'", ct)).ShouldBe(1);

        var violations = await PersistenceStructureChecker.CheckAsync(
            db.MigratorConnectionString, PersistedTableClassifications.Platform, ct);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public async Task StateConstraintsRefuseInconsistentRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(db, "constraint.member", cancellationToken: ct);
        var token = new byte[] { 1, 2, 3 };

        await Should.ThrowAsync<PostgresException>(() =>
            InsertRecoveryAsync(db, Guid.NewGuid(), accountId, "pending", token, issued: false, usedAt: null, ct));
        await Should.ThrowAsync<PostgresException>(() =>
            InsertRecoveryAsync(db, Guid.NewGuid(), accountId, "used", token, issued: true, usedAt: null, ct));

        await InsertRecoveryAsync(db, Guid.NewGuid(), accountId, "pending", null, issued: false, usedAt: null, ct);
        await InsertRecoveryAsync(db, Guid.NewGuid(), accountId, "used", token, issued: true, usedAt: Now, ct);
    }

    [Fact]
    public async Task TokenHashesAreUnique()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(db, "unique.member", cancellationToken: ct);
        var token = new byte[] { 9, 9, 9 };

        await InsertRecoveryAsync(db, Guid.NewGuid(), accountId, "issued", token, issued: true, usedAt: null, ct);
        var duplicate = await Should.ThrowAsync<PostgresException>(() =>
            InsertRecoveryAsync(db, Guid.NewGuid(), accountId, "issued", token, issued: true, usedAt: null, ct));
        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task CandidatesAreFoundCaseInsensitivelyByNormalizedAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(db, "candidate.member", cancellationToken: ct);
        await SeedAddressAsync(db, accountId, "Jane.Doe@Example.com", ct);
        await using var provider = PlatformServices.Build(db);

        EmailAddress.TryCreate("JANE.DOE@EXAMPLE.COM", out var requested, out _).ShouldBeTrue();
        var found = await InUnitOfWorkAsync(
            provider, sp => sp.GetRequiredService<IPasswordRecoveryStore>().ListCandidatesAsync(requested!.Normalized, ct), ct);
        var candidate = found.ShouldHaveSingleItem();
        candidate.AccountId.ShouldBe(accountId);
        candidate.AccountName.ShouldBe("candidate.member");
        candidate.AddressState.ShouldBe("verified");
        candidate.HasPassword.ShouldBeTrue();
        candidate.AddressHash.ShouldBe(requested.Hash);

        EmailAddress.TryCreate("unknown@example.com", out var unknown, out _).ShouldBeTrue();
        (await InUnitOfWorkAsync(
            provider, sp => sp.GetRequiredService<IPasswordRecoveryStore>().ListCandidatesAsync(unknown!.Normalized, ct), ct))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task SupersessionChangesOnlyOpenRecoveriesOfTheAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(db, "supersede.member", cancellationToken: ct);
        var otherId = await TestMembers.SeedAsync(db, "supersede.other", cancellationToken: ct);
        var pending = Guid.NewGuid();
        var issued = Guid.NewGuid();
        var used = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var otherPending = Guid.NewGuid();
        await InsertRecoveryAsync(db, pending, accountId, "pending", null, issued: false, usedAt: null, ct);
        await InsertRecoveryAsync(db, issued, accountId, "issued", [1], issued: true, usedAt: null, ct);
        await InsertRecoveryAsync(db, used, accountId, "used", [2], issued: true, usedAt: Now, ct);
        await InsertRecoveryAsync(db, expired, accountId, "expired", [3], issued: true, usedAt: null, ct);
        await InsertRecoveryAsync(db, otherPending, otherId, "pending", null, issued: false, usedAt: null, ct);
        await using var provider = PlatformServices.Build(db);

        var changed = await InUnitOfWorkAsync(
            provider, sp => sp.GetRequiredService<IPasswordRecoveryStore>().SupersedeOpenAsync(accountId, ct), ct);

        changed.ShouldBe(2);
        async Task<string?> StateAsync(Guid id) =>
            await ScalarAsync<string>(db, "SELECT state FROM socalytics.password_recoveries WHERE id = @id", ct, ("id", id));
        (await StateAsync(pending)).ShouldBe("superseded");
        (await StateAsync(issued)).ShouldBe("superseded");
        (await StateAsync(used)).ShouldBe("used");
        (await StateAsync(expired)).ShouldBe("expired");
        (await StateAsync(otherPending)).ShouldBe("pending");
    }

    [Fact]
    public async Task ResetByRecoveryChangesTheStampKeepsLockoutAndEndsEverySession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(
            db, "reset.member", passwordChangeRequired: true, lockedOut: true, cancellationToken: ct);
        await using var provider = PlatformServices.Build(db);

        var stampBefore = await ScalarAsync<string>(db, "SELECT security_stamp FROM socalytics.member_account WHERE id = @id", ct, ("id", accountId));
        var hashBefore = await ScalarAsync<string>(db, "SELECT password_hash FROM socalytics.member_account WHERE id = @id", ct, ("id", accountId));
        var lockoutBefore = await ScalarAsync<DateTime>(db, "SELECT lockout_end FROM socalytics.member_account WHERE id = @id", ct, ("id", accountId));
        await InUnitOfWorkAsync(provider, async sp =>
        {
            var sessions = sp.GetRequiredService<ISessionStore>();
            await sessions.CreateAsync(accountId, [1, 1], stampBefore!, Now, ct);
            await sessions.CreateAsync(accountId, [2, 2], stampBefore!, Now, ct);
            return true;
        }, ct);

        var outcome = await InUnitOfWorkAsync(
            provider, sp => sp.GetRequiredService<IAccountCredentialService>().ResetPasswordByRecoveryAsync(accountId, NewPassword, ct), ct);

        outcome.ShouldBe(CredentialRedemptionOutcome.Succeeded);
        (await ScalarAsync<string>(db, "SELECT security_stamp FROM socalytics.member_account WHERE id = @id", ct, ("id", accountId)))
            .ShouldNotBe(stampBefore);
        (await ScalarAsync<string>(db, "SELECT password_hash FROM socalytics.member_account WHERE id = @id", ct, ("id", accountId)))
            .ShouldNotBe(hashBefore);
        (await ScalarAsync<bool>(db, "SELECT password_change_required FROM socalytics.member_account WHERE id = @id", ct, ("id", accountId)))
            .ShouldBeFalse();
        (await ScalarAsync<DateTime>(db, "SELECT lockout_end FROM socalytics.member_account WHERE id = @id", ct, ("id", accountId)))
            .ShouldBe(lockoutBefore);
        (await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.member_session WHERE member_account_id = @id AND ended_at IS NULL", ct, ("id", accountId)))
            .ShouldBe(0);
        (await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.member_session WHERE member_account_id = @id AND end_reason = 'password-reset'", ct, ("id", accountId)))
            .ShouldBe(2);
    }

    [Fact]
    public async Task ResetByRecoveryRefusesAPolicyViolationWithoutChangingTheAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(db, "policy.member", cancellationToken: ct);
        await using var provider = PlatformServices.Build(db);
        var hashBefore = await ScalarAsync<string>(db, "SELECT password_hash FROM socalytics.member_account WHERE id = @id", ct, ("id", accountId));

        var outcome = await InUnitOfWorkAsync(
            provider, sp => sp.GetRequiredService<IAccountCredentialService>().ResetPasswordByRecoveryAsync(accountId, "short", ct), ct);

        outcome.ShouldBe(CredentialRedemptionOutcome.PolicyViolation);
        (await ScalarAsync<string>(db, "SELECT password_hash FROM socalytics.member_account WHERE id = @id", ct, ("id", accountId)))
            .ShouldBe(hashBefore);
    }
}
