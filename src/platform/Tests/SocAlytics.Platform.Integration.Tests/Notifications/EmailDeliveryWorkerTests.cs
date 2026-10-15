using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.IdentityAccess.Email;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Infrastructure.Email;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

[Collection(MailpitCollection.Name)]
public sealed partial class EmailDeliveryWorkerTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
    private sealed class Harness(IsolatedDatabase db, ServiceProvider provider, MutableTimeProvider time, CapturingLoggerProvider logs) : IAsyncDisposable
    {
        public IsolatedDatabase Db { get; } = db;

        public MutableTimeProvider Time { get; } = time;

        public CapturingLoggerProvider Logs { get; } = logs;

        public ServiceProvider Provider { get; } = provider;

        public async Task<int> ProcessAsync(CancellationToken ct)
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<EmailDeliveryProcessor>().ProcessDueAsync(ct);
        }

        public async Task<T> InUnitOfWorkAsync<T>(Func<IServiceProvider, Task<T>> action, CancellationToken ct)
        {
            await using var scope = Provider.CreateAsyncScope();
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var result = await action(scope.ServiceProvider);
            await uow.CommitAsync(ct);
            return result;
        }

        public async Task<StoredEmailMessage> MessageAsync(Guid id, CancellationToken ct) =>
            (await InUnitOfWorkAsync(sp => sp.GetRequiredService<IEmailMessageStore>().FindAsync(id, ct), ct))!;

        public async Task<StoredEmailConfirmation> ConfirmationAsync(Guid id, CancellationToken ct) =>
            (await InUnitOfWorkAsync(sp => sp.GetRequiredService<IEmailConfirmationStore>().FindAsync(id, ct), ct))!;

        public async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(Db.AppConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private async Task<Harness> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var logs = new CapturingLoggerProvider();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var provider = PlatformServices.Build(db, logs, time, extraSettings: new Dictionary<string, string?>
        {
            ["Email:Smtp:Host"] = mailpit.SmtpHost,
            ["Email:Smtp:Port"] = mailpit.SmtpPort.ToString(),
            ["Email:Delivery:MaxAttempts"] = "3",
        });
        return new Harness(db, provider, time, logs);
    }

    private static string UniqueAddress(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.test";

    private sealed record Recipient(Guid AccountId, string Address);

    private async Task<Recipient> SeedAsync(
        Harness h, CancellationToken ct, bool clubAdmin = true, bool verified = true, string prefix = "member")
    {
        var accountId = await TestMembers.SeedAsync(
            h.Db, prefix + Guid.NewGuid().ToString("N")[..8], clubRoles: clubAdmin ? ["club-admin"] : null, cancellationToken: ct);
        var address = UniqueAddress(prefix);
        await h.InUnitOfWorkAsync(async sp =>
        {
            var store = sp.GetRequiredService<IMemberEmailAddressStore>();
            await store.InsertAsync(accountId, address, address, accountId, h.Time.GetUtcNow(), ct);
            if (verified)
            {
                await store.MarkVerifiedAsync(accountId, h.Time.GetUtcNow(), 1, ct);
            }

            return 0;
        }, ct);
        return new Recipient(accountId, address);
    }

    private static Task EnsureClubAsync(Harness h, Guid adminId, CancellationToken ct) =>
        h.ExecuteAsync(
            $"INSERT INTO socalytics.club (id, display_name, bootstrap_admin_account_id, created_at) VALUES ('{Guid.NewGuid()}', 'Example FC', '{adminId}', now())", ct);

    private static Task<Guid?> QueuePendingAsync(Harness h, Recipient r, CancellationToken ct) =>
        h.InUnitOfWorkAsync(sp => sp.GetRequiredService<IEmailOutbox>().QueueAsync(NotificationTypes.RegistrationPendingCode, r.AccountId, null, ct), ct);

    private static Task<(Guid Message, Guid Confirmation)> QueueConfirmationAsync(Harness h, Recipient r, CancellationToken ct) =>
        h.InUnitOfWorkAsync(async sp =>
        {
            EmailAddress.TryCreate(r.Address, out var address, out _);
            var confirmation = Guid.NewGuid();
            await sp.GetRequiredService<IEmailConfirmationStore>().CreatePendingAsync(confirmation, r.AccountId, address!.Hash, ct);
            var message = await sp.GetRequiredService<IEmailOutbox>().QueueAsync(NotificationTypes.EmailConfirmationCode, r.AccountId, confirmation, ct);
            return (message!.Value, confirmation);
        }, ct);

    private async Task<string> TokenFromMailAsync(string address, int count, CancellationToken ct)
    {
        var found = await mailpit.WaitForMessagesAsync($"to:{address}", count, TimeSpan.FromSeconds(30), ct);
        var text = await mailpit.GetTextBodyAsync(found.First().Id, ct);
        return TokenPattern().Match(text).Groups[1].Value;
    }

    [GeneratedRegex(@"/email/confirm#token=([A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();

    [Fact]
    public async Task DeliversConfirmationAndIssuesTheTokenOnlyAtSend()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var r = await SeedAsync(h, ct, verified: false);
        await EnsureClubAsync(h, r.AccountId, ct);
        var (message, confirmation) = await QueueConfirmationAsync(h, r, ct);

        (await h.ConfirmationAsync(confirmation, ct)).TokenHash.ShouldBeNull();
        (await h.MessageAsync(message, ct)).State.ShouldBe(EmailMessageStates.Queued);

        await h.ProcessAsync(ct);

        var token = await TokenFromMailAsync(r.Address, 1, ct);
        token.Length.ShouldBeGreaterThanOrEqualTo(43);
        (await h.MessageAsync(message, ct)).State.ShouldBe(EmailMessageStates.Sent);
        var stored = await h.ConfirmationAsync(confirmation, ct);
        stored.State.ShouldBe(EmailConfirmationStates.Issued);
        stored.TokenHash.ShouldBe(ConfirmationTokens.Hash(token));
        stored.ExpiresAt.ShouldBe(stored.IssuedAt!.Value.AddDays(1));

        var sent = await mailpit.WaitForMessagesAsync($"to:{r.Address}", 1, TimeSpan.FromSeconds(30), ct);
        (await mailpit.GetTextBodyAsync(sent.Last().Id, ct)).ShouldContain("Example FC");

        await h.ProcessAsync(ct);
        (await mailpit.SearchAsync($"to:{r.Address}", ct)).Count.ShouldBe(1);

        var logged = string.Join('\n', h.Logs.Entries.Select(e => e.Message + e.Exception));
        logged.ShouldNotContain(token);
        logged.ShouldNotContain(r.Address);
    }

    [Fact]
    public async Task DeliversRegistrationPendingToAnActiveVerifiedClubAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var r = await SeedAsync(h, ct);
        var id = (await QueuePendingAsync(h, r, ct))!.Value;

        await h.ProcessAsync(ct);

        var found = await mailpit.WaitForMessagesAsync($"to:{r.Address}", 1, TimeSpan.FromSeconds(30), ct);
        found.Single().Subject.ShouldBe("An Analyst Manager registration request waits for approval");
        (await mailpit.GetTextBodyAsync(found.Single().Id, ct)).ShouldContain("http://localhost:5173/analyst-managers");
        (await h.MessageAsync(id, ct)).State.ShouldBe(EmailMessageStates.Sent);
    }

    public enum Change
    {
        Deactivated,
        AddressChanged,
        AddressRemoved,
        Unverified,
        OptedOut,
        NoLongerClubAdmin,
    }

    [Theory]
    [InlineData(Change.Deactivated)]
    [InlineData(Change.AddressChanged)]
    [InlineData(Change.AddressRemoved)]
    [InlineData(Change.Unverified)]
    [InlineData(Change.OptedOut)]
    [InlineData(Change.NoLongerClubAdmin)]
    public async Task DiscardsMessagesWhoseRecipientIsNoLongerEligible(Change change)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var r = await SeedAsync(h, ct);
        var id = (await QueuePendingAsync(h, r, ct))!.Value;
        var account = $"'{r.AccountId}'";

        switch (change)
        {
            case Change.Deactivated:
                await h.ExecuteAsync($"DELETE FROM socalytics.club_role_assignment WHERE member_account_id = {account}; UPDATE socalytics.member_account SET membership_status = 'deactivated' WHERE id = {account}", ct);
                break;
            case Change.AddressChanged:
                await h.ExecuteAsync($"UPDATE socalytics.member_email_addresses SET address = 'other@example.test', address_normalized = 'other@example.test' WHERE account_id = {account}", ct);
                break;
            case Change.AddressRemoved:
                await h.ExecuteAsync($"DELETE FROM socalytics.member_email_addresses WHERE account_id = {account}", ct);
                break;
            case Change.Unverified:
                await h.ExecuteAsync($"UPDATE socalytics.member_email_addresses SET state = 'unverified', verified_at = NULL WHERE account_id = {account}", ct);
                break;
            case Change.OptedOut:
                await h.ExecuteAsync($"INSERT INTO socalytics.notification_preferences (account_id, notification_type, enabled, updated_at) VALUES ({account}, 'registration-pending', false, now())", ct);
                break;
            case Change.NoLongerClubAdmin:
                await h.ExecuteAsync($"DELETE FROM socalytics.club_role_assignment WHERE member_account_id = {account}", ct);
                break;
        }

        await h.ProcessAsync(ct);

        var stored = await h.MessageAsync(id, ct);
        stored.State.ShouldBe(EmailMessageStates.Discarded);
        stored.LastErrorCategory.ShouldBe(EmailErrorCategories.Ineligible);
        (await mailpit.SearchAsync($"to:{r.Address}", ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ARetryIssuesANewTokenAndTheEarlierLinkStopsWorking()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var r = await SeedAsync(h, ct, verified: false);
        await EnsureClubAsync(h, r.AccountId, ct);
        var (message, confirmation) = await QueueConfirmationAsync(h, r, ct);

        await h.ProcessAsync(ct);
        var first = await TokenFromMailAsync(r.Address, 1, ct);

        // Simulates a crash before the outcome was recorded: the lease runs out and the message is claimed again.
        await h.ExecuteAsync($"UPDATE socalytics.email_messages SET state = 'sending', lease_until = '{h.Time.GetUtcNow().AddMinutes(-1):O}', sent_at = NULL WHERE id = '{message}'", ct);
        await h.ProcessAsync(ct);

        var second = await TokenFromMailAsync(r.Address, 2, ct);
        second.ShouldNotBe(first);
        var stored = await h.ConfirmationAsync(confirmation, ct);
        stored.TokenHash.ShouldBe(ConfirmationTokens.Hash(second));
        stored.TokenHash.ShouldNotBe(ConfirmationTokens.Hash(first));
    }

    [Fact]
    public async Task EveryQueuedMessageIsDeliveredExactlyOnceAfterARelayOutage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var recipients = new List<Recipient>();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var r = await SeedAsync(h, ct, prefix: "outage");
            recipients.Add(r);
            ids.Add((await QueuePendingAsync(h, r, ct))!.Value);
        }

        await mailpit.StopAsync(ct);
        try
        {
            await h.ProcessAsync(ct);
            foreach (var id in ids)
            {
                var stored = await h.MessageAsync(id, ct);
                stored.State.ShouldBe(EmailMessageStates.Queued);
                stored.LastErrorCategory.ShouldBe(EmailErrorCategories.Transient);
                stored.NextAttemptAt.ShouldBe(h.Time.GetUtcNow().AddMinutes(1));
            }
        }
        finally
        {
            await mailpit.StartAsync(ct);
        }

        await h.ProcessAsync(ct);
        (await h.MessageAsync(ids[0], ct)).State.ShouldBe(EmailMessageStates.Queued);

        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.ProcessAsync(ct);
        await h.ProcessAsync(ct);

        foreach (var (r, id) in recipients.Zip(ids))
        {
            (await h.MessageAsync(id, ct)).State.ShouldBe(EmailMessageStates.Sent);
            (await mailpit.WaitForMessagesAsync($"to:{r.Address}", 1, TimeSpan.FromSeconds(30), ct)).Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task ATransientFailureEndsAsFailedAfterMaxAttempts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var r = await SeedAsync(h, ct);
        var id = (await QueuePendingAsync(h, r, ct))!.Value;

        await mailpit.StopAsync(ct);
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await h.ProcessAsync(ct);
                h.Time.Advance(TimeSpan.FromHours(2));
            }
        }
        finally
        {
            await mailpit.StartAsync(ct);
        }

        var stored = await h.MessageAsync(id, ct);
        stored.State.ShouldBe(EmailMessageStates.Failed);
        stored.AttemptCount.ShouldBe(3);
        stored.LastErrorCategory.ShouldBe(EmailErrorCategories.Transient);
    }

    [Fact]
    public async Task APermanentFailureEndsAsFailedWithoutRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var r = await SeedAsync(h, ct);
        var id = (await QueuePendingAsync(h, r, ct))!.Value;
        // A stored address the relay cannot take: the hash still matches because both are rewritten together.
        var bad = "bad..address@example.test";
        EmailAddress.TryCreate(r.Address, out var original, out _);
        await h.ExecuteAsync($"UPDATE socalytics.member_email_addresses SET address = '{bad}' WHERE account_id = '{r.AccountId}'", ct);

        await h.ProcessAsync(ct);

        var stored = await h.MessageAsync(id, ct);
        stored.State.ShouldBeOneOf(EmailMessageStates.Failed, EmailMessageStates.Discarded);
        stored.AttemptCount.ShouldBe(1);
        original.ShouldNotBeNull();
        (await mailpit.SearchAsync($"to:{r.Address}", ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RestartRecoversAMessageWhoseLeaseExpired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var r = await SeedAsync(h, ct);
        var id = (await QueuePendingAsync(h, r, ct))!.Value;
        await h.ExecuteAsync($"UPDATE socalytics.email_messages SET state = 'sending', attempt_count = 1, lease_until = '{h.Time.GetUtcNow().AddMinutes(-1):O}' WHERE id = '{id}'", ct);

        await h.ProcessAsync(ct);

        (await h.MessageAsync(id, ct)).State.ShouldBe(EmailMessageStates.Sent);
        (await mailpit.WaitForMessagesAsync($"to:{r.Address}", 1, TimeSpan.FromSeconds(30), ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AMessageUnderALiveLeaseIsNotClaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        var r = await SeedAsync(h, ct);
        var id = (await QueuePendingAsync(h, r, ct))!.Value;
        await h.ExecuteAsync($"UPDATE socalytics.email_messages SET state = 'sending', lease_until = '{h.Time.GetUtcNow().AddMinutes(1):O}' WHERE id = '{id}'", ct);

        (await h.ProcessAsync(ct)).ShouldBe(0);
        (await mailpit.SearchAsync($"to:{r.Address}", ct)).ShouldBeEmpty();
    }

    [Fact]
    public void BackoffDoublesFromOneMinuteAndCapsAtOneHour()
    {
        EmailDeliveryProcessor.Backoff(1).ShouldBe(TimeSpan.FromMinutes(1));
        EmailDeliveryProcessor.Backoff(2).ShouldBe(TimeSpan.FromMinutes(2));
        EmailDeliveryProcessor.Backoff(3).ShouldBe(TimeSpan.FromMinutes(4));
        EmailDeliveryProcessor.Backoff(8).ShouldBe(TimeSpan.FromHours(1));
        EmailDeliveryProcessor.Backoff(40).ShouldBe(TimeSpan.FromHours(1));
    }
}
