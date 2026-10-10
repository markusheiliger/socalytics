using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class OneTimeCredentialTests(PostgresContainerFixture postgres)
{
    private const string NewPassword = "a-brand-new-password";

    private sealed record Fixture(IsolatedDatabase Db, MutableTimeProvider Time, ServiceProvider Provider, Guid IssuerId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await Db.DisposeAsync();
        }

        public async Task<T> InScopeAsync<T>(Func<IAccountCredentialService, Task<T>> action, CancellationToken ct)
        {
            await using var scope = Provider.CreateAsyncScope();
            await using var work = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var result = await action(scope.ServiceProvider.GetRequiredService<IAccountCredentialService>());
            await work.CommitAsync(ct);
            return result;
        }

        public Task<IssuedCredential> IssueAsync(Guid accountId, CredentialPurpose purpose, CancellationToken ct) =>
            InScopeAsync(s => s.IssueCredentialAsync(accountId, purpose, IssuerId, ct), ct);

        public Task<CredentialRedemptionResult> RedeemAsync(string name, string raw, CancellationToken ct) =>
            InScopeAsync(s => s.RedeemCredentialAsync(Name(name), raw, NewPassword, ct), ct);

        public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct)
        {
            await using var c = new NpgsqlConnection(Db.AppConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, c);
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? default : (T)value;
        }
    }

    private static AccountName Name(string value)
    {
        AccountName.TryCreate(value, out var name, out _).ShouldBeTrue();
        return name!;
    }

    private async Task<Fixture> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var time = new MutableTimeProvider();
        var issuer = await TestMembers.SeedAsync(db, "issuer", cancellationToken: ct);
        return new Fixture(db, time, PlatformServices.Build(db, time: time), issuer);
    }

    [Fact]
    public async Task CredentialRedeemsOnceAndThenFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        var id = await TestMembers.SeedAsync(f.Db, "newcomer", password: null, cancellationToken: ct);
        var credential = await f.IssueAsync(id, CredentialPurpose.SetPassword, ct);

        (await f.RedeemAsync("newcomer", credential.RawCredential, ct)).Outcome.ShouldBe(CredentialRedemptionOutcome.Succeeded);
        (await f.ScalarAsync<string>("SELECT password_hash FROM socalytics.member_account WHERE account_name = 'newcomer'", ct)).ShouldNotBeNull();
        (await f.RedeemAsync("newcomer", credential.RawCredential, ct)).Outcome.ShouldBe(CredentialRedemptionOutcome.Invalid);
    }

    [Fact]
    public async Task ExpiredCredentialFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        var id = await TestMembers.SeedAsync(f.Db, "newcomer", password: null, cancellationToken: ct);
        var credential = await f.IssueAsync(id, CredentialPurpose.SetPassword, ct);

        f.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        (await f.RedeemAsync("newcomer", credential.RawCredential, ct)).Outcome.ShouldBe(CredentialRedemptionOutcome.Invalid);
    }

    [Fact]
    public async Task CredentialForAnotherAccountFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        var id = await TestMembers.SeedAsync(f.Db, "newcomer", password: null, cancellationToken: ct);
        await TestMembers.SeedAsync(f.Db, "other", password: null, cancellationToken: ct);
        var credential = await f.IssueAsync(id, CredentialPurpose.SetPassword, ct);

        (await f.RedeemAsync("other", credential.RawCredential, ct)).Outcome.ShouldBe(CredentialRedemptionOutcome.Invalid);
    }

    [Fact]
    public async Task SupersededCredentialFailsAndIsAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        var id = await TestMembers.SeedAsync(f.Db, "newcomer", password: null, cancellationToken: ct);
        var first = await f.IssueAsync(id, CredentialPurpose.SetPassword, ct);
        var second = await f.IssueAsync(id, CredentialPurpose.SetPassword, ct);

        (await f.RedeemAsync("newcomer", first.RawCredential, ct)).Outcome.ShouldBe(CredentialRedemptionOutcome.Invalid);
        (await f.ScalarAsync<string>(
            $"SELECT revocation_reason FROM socalytics.one_time_credential WHERE id = '{first.CredentialId}'", ct)).ShouldBe("superseded");
        (await f.ScalarAsync<long>(
            "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'credential.revoked' " +
            $"AND resource_id = '{first.CredentialId}' AND reason_code = 'superseded'", ct)).ShouldBe(1);
        (await f.RedeemAsync("newcomer", second.RawCredential, ct)).Outcome.ShouldBe(CredentialRedemptionOutcome.Succeeded);
    }

    [Fact]
    public async Task RedemptionFailsForDeactivatedTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        var id = await TestMembers.SeedAsync(f.Db, "newcomer", password: null, cancellationToken: ct);
        var credential = await f.IssueAsync(id, CredentialPurpose.SetPassword, ct);
        await using (var c = new NpgsqlConnection(f.Db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                $"UPDATE socalytics.member_account SET membership_status = 'deactivated' WHERE id = '{id}'", c);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        (await f.RedeemAsync("newcomer", credential.RawCredential, ct)).Outcome.ShouldBe(CredentialRedemptionOutcome.Invalid);
    }

    [Fact]
    public async Task RevocationByIssuerAndByTargetSetsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        var a = await TestMembers.SeedAsync(f.Db, "first", password: null, cancellationToken: ct);
        var b = await TestMembers.SeedAsync(f.Db, "second", password: null, cancellationToken: ct);
        var forA = await f.IssueAsync(a, CredentialPurpose.SetPassword, ct);
        var forB = await f.IssueAsync(b, CredentialPurpose.SetPassword, ct);

        var byTarget = await f.InScopeAsync(s => s.RevokeOpenCredentialsForAccountAsync(a, "target-deactivated", ct), ct);
        var byIssuer = await f.InScopeAsync(s => s.RevokeOpenCredentialsIssuedByAsync(f.IssuerId, "issuer-lost-authority", ct), ct);

        byTarget.Select(r => r.CredentialId).ShouldBe([forA.CredentialId]);
        byTarget[0].Reason.ShouldBe("target-deactivated");
        byIssuer.Select(r => r.CredentialId).ShouldBe([forB.CredentialId]);
        byIssuer[0].Purpose.ShouldBe(CredentialPurpose.SetPassword);
        (await f.ScalarAsync<string>(
            $"SELECT revocation_reason FROM socalytics.one_time_credential WHERE id = '{forB.CredentialId}'", ct)).ShouldBe("issuer-lost-authority");
    }

    [Fact]
    public async Task OnlyTheHashIsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await CreateAsync(ct);
        var id = await TestMembers.SeedAsync(f.Db, "newcomer", password: null, cancellationToken: ct);
        var credential = await f.IssueAsync(id, CredentialPurpose.SetPassword, ct);

        var stored = await f.ScalarAsync<byte[]>(
            $"SELECT credential_hash FROM socalytics.one_time_credential WHERE id = '{credential.CredentialId}'", ct);

        stored.ShouldBe(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(credential.RawCredential)));
        credential.RawCredential.Length.ShouldBe(43);
    }
}
