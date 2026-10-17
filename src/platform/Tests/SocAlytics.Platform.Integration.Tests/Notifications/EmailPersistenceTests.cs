using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.Email;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

public sealed class EmailPersistenceTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        return db;
    }

    [Fact]
    public async Task AddressRoundTripsAndVersionConflictsAreDetected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(db, "email.member", cancellationToken: ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
        var store = new MemberEmailAddressStore(session);
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        (await store.InsertAsync(accountId, "Jane.Doe@Example.com", "jane.doe@example.com", accountId, Now, ct)).ShouldBeTrue();
        (await store.InsertAsync(accountId, "other@example.com", "other@example.com", accountId, Now, ct)).ShouldBeFalse();

        var stored = await store.FindAsync(accountId, ct);
        stored.ShouldNotBeNull();
        stored.Address.ShouldBe("Jane.Doe@Example.com");
        stored.AddressNormalized.ShouldBe("jane.doe@example.com");
        stored.State.ShouldBe(EmailAddressStates.Unverified);
        stored.VerifiedAt.ShouldBeNull();
        stored.Version.ShouldBe(1);

        var verified = await store.MarkVerifiedAsync(accountId, Now.AddMinutes(1), stored.Version, ct);
        verified.Outcome.ShouldBe(VersionedWriteOutcome.Applied);
        verified.Version.ShouldBe(2);

        var stale = await store.ReplaceAsync(accountId, "new@example.com", "new@example.com", accountId, Now, stored.Version, ct);
        stale.Outcome.ShouldBe(VersionedWriteOutcome.ConcurrencyConflict);
        stale.Version.ShouldBe(2);

        var replaced = await store.ReplaceAsync(accountId, "new@example.com", "new@example.com", accountId, Now.AddMinutes(2), 2, ct);
        replaced.Outcome.ShouldBe(VersionedWriteOutcome.Applied);
        var after = await store.FindAsync(accountId, ct);
        after!.State.ShouldBe(EmailAddressStates.Unverified);
        after.VerifiedAt.ShouldBeNull();
        after.Address.ShouldBe("new@example.com");

        (await store.DeleteAsync(accountId, 1, ct)).Outcome.ShouldBe(VersionedWriteOutcome.ConcurrencyConflict);
        (await store.DeleteAsync(accountId, after.Version, ct)).Outcome.ShouldBe(VersionedWriteOutcome.Applied);
        (await store.FindAsync(accountId, ct)).ShouldBeNull();
        (await store.DeleteAsync(accountId, 1, ct)).Outcome.ShouldBe(VersionedWriteOutcome.NotFound);
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task ConfirmationsRoundTripThroughTheirLifecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(db, "confirm.member", cancellationToken: ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var store = new EmailConfirmationStore(scope.ServiceProvider.GetRequiredService<IDbSession>());
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var first = Guid.NewGuid();
        await store.CreatePendingAsync(first, accountId, "hash-1", ct);
        var pending = await store.FindAsync(first, ct);
        pending!.State.ShouldBe(EmailConfirmationStates.Pending);
        pending.TokenHash.ShouldBeNull();

        var token1 = new byte[] { 1, 2, 3 };
        (await store.IssueAsync(first, token1, Now, Now.AddHours(24), ct)).ShouldBeTrue();
        var token2 = new byte[] { 4, 5, 6 };
        (await store.IssueAsync(first, token2, Now.AddMinutes(1), Now.AddHours(24), ct)).ShouldBeTrue();
        (await store.FindByTokenHashAsync(token1, ct)).ShouldBeNull();
        var issued = await store.FindByTokenHashAsync(token2, ct);
        issued!.Id.ShouldBe(first);
        issued.State.ShouldBe(EmailConfirmationStates.Issued);
        issued.ExpiresAt.ShouldBe(Now.AddHours(24));

        (await store.MarkUsedAsync(first, Now.AddMinutes(5), ct)).ShouldBeTrue();
        (await store.MarkUsedAsync(first, Now.AddMinutes(6), ct)).ShouldBeFalse();
        (await store.IssueAsync(first, token1, Now, Now.AddHours(1), ct)).ShouldBeFalse();
        (await store.FindAsync(first, ct))!.UsedAt.ShouldBe(Now.AddMinutes(5));

        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        await store.CreatePendingAsync(second, accountId, "hash-2", ct);
        await store.CreatePendingAsync(third, accountId, "hash-2", ct);
        (await store.IssueAsync(third, new byte[] { 9 }, Now, Now.AddHours(1), ct)).ShouldBeTrue();
        (await store.SupersedeOpenAsync(accountId, ct)).ShouldBe(2);
        (await store.FindAsync(second, ct))!.State.ShouldBe(EmailConfirmationStates.Superseded);
        (await store.FindAsync(first, ct))!.State.ShouldBe(EmailConfirmationStates.Used);

        var fourth = Guid.NewGuid();
        await store.CreatePendingAsync(fourth, accountId, "hash-3", ct);
        (await store.MarkExpiredAsync(fourth, ct)).ShouldBeFalse();
        (await store.IssueAsync(fourth, new byte[] { 7 }, Now, Now.AddHours(1), ct)).ShouldBeTrue();
        (await store.MarkExpiredAsync(fourth, ct)).ShouldBeTrue();
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task PreferencesRoundTripAndUpsert()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(db, "pref.member", cancellationToken: ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var store = new NotificationPreferenceStore(scope.ServiceProvider.GetRequiredService<IDbSession>());
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        (await store.FindAsync(accountId, "registration-pending", ct)).ShouldBeNull();
        await store.SetAsync(accountId, "registration-pending", false, Now, ct);
        (await store.FindAsync(accountId, "registration-pending", ct))!.Enabled.ShouldBeFalse();
        await store.SetAsync(accountId, "registration-pending", true, Now.AddMinutes(1), ct);

        var all = await store.ListAsync(accountId, ct);
        all.Count.ShouldBe(1);
        all[0].Enabled.ShouldBeTrue();
        all[0].UpdatedAt.ShouldBe(Now.AddMinutes(1));
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task MessagesRoundTripThroughTheirLifecycleAndClaimsAreLeased()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var accountId = await TestMembers.SeedAsync(db, "message.member", cancellationToken: ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var store = new EmailMessageStore(scope.ServiceProvider.GetRequiredService<IDbSession>());
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var later = Guid.NewGuid();
        await store.QueueAsync(new NewEmailMessage(a, "registration-pending", accountId, "h", null, Now, Now), ct);
        await store.QueueAsync(new NewEmailMessage(b, "registration-pending", accountId, "h", null, Now, Now), ct);
        await store.QueueAsync(new NewEmailMessage(later, "registration-pending", accountId, "h", null, Now, Now.AddHours(1)), ct);

        var claimed = await store.ClaimDueAsync(Now, Now.AddMinutes(2), 10, ct);
        claimed.Select(m => m.Id).ShouldBe(new[] { a, b }, ignoreOrder: true);
        claimed.ShouldAllBe(m => m.State == EmailMessageStates.Sending && m.AttemptCount == 1);
        (await store.ClaimDueAsync(Now.AddMinutes(1), Now.AddMinutes(3), 10, ct)).ShouldBeEmpty();
        (await store.ClaimDueAsync(Now.AddMinutes(3), Now.AddMinutes(5), 10, ct)).Count.ShouldBe(2);

        await store.MarkSentAsync(a, Now.AddMinutes(4), ct);
        var sent = await store.FindAsync(a, ct);
        sent!.State.ShouldBe(EmailMessageStates.Sent);
        sent.SentAt.ShouldBe(Now.AddMinutes(4));
        sent.AttemptCount.ShouldBe(2);

        await store.MarkRetryAsync(b, Now.AddMinutes(10), ct);
        var retry = await store.FindAsync(b, ct);
        retry!.State.ShouldBe(EmailMessageStates.Queued);
        retry.NextAttemptAt.ShouldBe(Now.AddMinutes(10));
        retry.LastErrorCategory.ShouldBe(EmailErrorCategories.Transient);

        await store.MarkSentAsync(b, Now, ct);
        (await store.FindAsync(b, ct))!.State.ShouldBe(EmailMessageStates.Queued);

        await store.MarkDiscardedAsync(later, EmailErrorCategories.Ineligible, ct);
        (await store.FindAsync(later, ct))!.State.ShouldBe(EmailMessageStates.Discarded);

        var claimedAgain = await store.ClaimDueAsync(Now.AddMinutes(10), Now.AddMinutes(12), 10, ct);
        claimedAgain.Single().Id.ShouldBe(b);
        await store.MarkFailedAsync(b, EmailErrorCategories.Permanent, ct);
        var failed = await store.FindAsync(b, ct);
        failed!.State.ShouldBe(EmailMessageStates.Failed);
        failed.LastErrorCategory.ShouldBe(EmailErrorCategories.Permanent);

        (await store.CountCreatedSinceAsync("registration-pending", accountId, Now, ct)).ShouldBe(3);
        (await store.CountCreatedSinceAsync("registration-pending", accountId, Now.AddSeconds(1), ct)).ShouldBe(0);
        (await store.CountCreatedSinceAsync("email-confirmation", accountId, Now, ct)).ShouldBe(0);
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task MessagesAndAddressesRequireAnExistingAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var store = new EmailMessageStore(scope.ServiceProvider.GetRequiredService<IDbSession>());
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var ex = await Should.ThrowAsync<PostgresException>(() => store.QueueAsync(
            new NewEmailMessage(Guid.NewGuid(), "registration-pending", Guid.NewGuid(), "h", null, Now, Now), ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task EmailMessagesStoreNoAddressBodyOrTokenColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'socalytics' AND table_name = 'email_messages'",
            connection);
        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                columns.Add(reader.GetString(0));
            }
        }

        columns.ShouldNotBeEmpty();
        columns.ShouldNotContain(c => c == "address" || c.Contains("body") || c.Contains("token") || c.Contains("subject") || c.Contains("link"));
        columns.ShouldContain("address_hash");
    }
}
