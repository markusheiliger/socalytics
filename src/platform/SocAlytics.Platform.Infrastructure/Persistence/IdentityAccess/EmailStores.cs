using Dapper;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.Email;

namespace SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess;

internal static class EmailRows
{
    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
}

internal sealed class MemberEmailAddressStore(IDbSession session) : IMemberEmailAddressStore
{
    private const string Columns =
        "account_id AS AccountId, address AS Address, address_normalized AS AddressNormalized, state AS State, " +
        "verified_at AS VerifiedAt, set_by AS SetBy, updated_at AS UpdatedAt, version AS Version";

    public async Task<StoredMemberEmailAddress?> FindAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.member_email_addresses WHERE account_id = @accountId",
            new { accountId }, session.Transaction, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new StoredMemberEmailAddress(
                row.AccountId, row.Address, row.AddressNormalized, row.State, EmailRows.Utc(row.VerifiedAt),
                row.SetBy, EmailRows.Utc(row.UpdatedAt), row.Version);
    }

    public async Task<bool> InsertAsync(
        Guid accountId, string address, string addressNormalized, Guid setBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.member_email_addresses (account_id, address, address_normalized, state, set_by, updated_at) " +
            "VALUES (@accountId, @address, @addressNormalized, 'unverified', @setBy, @now) ON CONFLICT (account_id) DO NOTHING",
            new { accountId, address, addressNormalized, setBy, now }, transaction, cancellationToken: cancellationToken));
        return rows > 0;
    }

    public Task<VersionedWriteResult> ReplaceAsync(
        Guid accountId, string address, string addressNormalized, Guid setBy, DateTimeOffset now, long expectedVersion,
        CancellationToken cancellationToken) =>
        Guarded(
            "UPDATE socalytics.member_email_addresses SET address = @address, address_normalized = @addressNormalized, " +
            "state = 'unverified', verified_at = NULL, set_by = @setBy, updated_at = @now " +
            "WHERE account_id = @accountId AND version = @expectedVersion RETURNING version",
            new { accountId, address, addressNormalized, setBy, now, expectedVersion }, accountId, cancellationToken);

    public Task<VersionedWriteResult> MarkVerifiedAsync(
        Guid accountId, DateTimeOffset now, long expectedVersion, CancellationToken cancellationToken) =>
        Guarded(
            "UPDATE socalytics.member_email_addresses SET state = 'verified', verified_at = @now, updated_at = @now " +
            "WHERE account_id = @accountId AND version = @expectedVersion RETURNING version",
            new { accountId, now, expectedVersion }, accountId, cancellationToken);

    public Task<VersionedWriteResult> DeleteAsync(Guid accountId, long expectedVersion, CancellationToken cancellationToken) =>
        Guarded(
            "DELETE FROM socalytics.member_email_addresses WHERE account_id = @accountId AND version = @expectedVersion RETURNING version",
            new { accountId, expectedVersion }, accountId, cancellationToken);

    private Task<VersionedWriteResult> Guarded(string sql, object parameters, Guid accountId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        return VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken),
            new CommandDefinition(
                "SELECT version FROM socalytics.member_email_addresses WHERE account_id = @accountId",
                new { accountId }, transaction, cancellationToken: cancellationToken));
    }

    private sealed record Row(
        Guid AccountId, string Address, string AddressNormalized, string State, DateTime? VerifiedAt, Guid SetBy,
        DateTime UpdatedAt, long Version);
}

internal sealed class EmailConfirmationStore(IDbSession session) : IEmailConfirmationStore
{
    private const string Columns =
        "id AS Id, account_id AS AccountId, address_hash AS AddressHash, state AS State, token_hash AS TokenHash, " +
        "issued_at AS IssuedAt, expires_at AS ExpiresAt, used_at AS UsedAt";

    public async Task CreatePendingAsync(Guid id, Guid accountId, string addressHash, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.email_confirmations (id, account_id, address_hash, state) VALUES (@id, @accountId, @addressHash, 'pending')",
            new { id, accountId, addressHash }, transaction, cancellationToken: cancellationToken));
    }

    public Task<StoredEmailConfirmation?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        QueryOne("id = @id", new { id }, cancellationToken);

    public Task<StoredEmailConfirmation?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        QueryOne("token_hash = @tokenHash", new { tokenHash }, cancellationToken);

    public async Task<bool> IssueAsync(
        Guid id, byte[] tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.email_confirmations SET state = 'issued', token_hash = @tokenHash, issued_at = @issuedAt, expires_at = @expiresAt " +
            "WHERE id = @id AND state IN ('pending', 'issued')",
            new { id, tokenHash, issuedAt, expiresAt }, cancellationToken) > 0;

    public async Task<bool> MarkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.email_confirmations SET state = 'used', used_at = @usedAt WHERE id = @id AND state = 'issued'",
            new { id, usedAt }, cancellationToken) > 0;

    public async Task<bool> MarkExpiredAsync(Guid id, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.email_confirmations SET state = 'expired' WHERE id = @id AND state = 'issued'",
            new { id }, cancellationToken) > 0;

    public Task<int> SupersedeOpenAsync(Guid accountId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE socalytics.email_confirmations SET state = 'superseded' WHERE account_id = @accountId AND state IN ('pending', 'issued')",
            new { accountId }, cancellationToken);

    private async Task<int> ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
    }

    private async Task<StoredEmailConfirmation?> QueryOne(string where, object parameters, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.email_confirmations WHERE {where}",
            parameters, session.Transaction, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new StoredEmailConfirmation(
                row.Id, row.AccountId, row.AddressHash, row.State, row.TokenHash, EmailRows.Utc(row.IssuedAt),
                EmailRows.Utc(row.ExpiresAt), EmailRows.Utc(row.UsedAt));
    }

    private sealed record Row(
        Guid Id, Guid AccountId, string AddressHash, string State, byte[]? TokenHash, DateTime? IssuedAt,
        DateTime? ExpiresAt, DateTime? UsedAt);
}

internal sealed class NotificationPreferenceStore(IDbSession session) : INotificationPreferenceStore
{
    private const string Columns =
        "account_id AS AccountId, notification_type AS NotificationType, enabled AS Enabled, updated_at AS UpdatedAt";

    public async Task<IReadOnlyList<StoredNotificationPreference>> ListAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.notification_preferences WHERE account_id = @accountId ORDER BY notification_type",
            new { accountId }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<StoredNotificationPreference?> FindAsync(
        Guid accountId, string notificationType, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.notification_preferences WHERE account_id = @accountId AND notification_type = @notificationType",
            new { accountId, notificationType }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    public async Task SetAsync(
        Guid accountId, string notificationType, bool enabled, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.notification_preferences (account_id, notification_type, enabled, updated_at) " +
            "VALUES (@accountId, @notificationType, @enabled, @now) " +
            "ON CONFLICT (account_id, notification_type) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = EXCLUDED.updated_at",
            new { accountId, notificationType, enabled, now }, transaction, cancellationToken: cancellationToken));
    }

    private static StoredNotificationPreference Map(Row row) =>
        new(row.AccountId, row.NotificationType, row.Enabled, EmailRows.Utc(row.UpdatedAt));

    private sealed record Row(Guid AccountId, string NotificationType, bool Enabled, DateTime UpdatedAt);
}

internal sealed class EmailMessageStore(IDbSession session) : IEmailMessageStore
{
    private const string Columns =
        "id AS Id, notification_type AS NotificationType, account_id AS AccountId, address_hash AS AddressHash, " +
        "confirmation_id AS ConfirmationId, state AS State, attempt_count AS AttemptCount, next_attempt_at AS NextAttemptAt, " +
        "lease_until AS LeaseUntil, last_error_category AS LastErrorCategory, created_at AS CreatedAt, sent_at AS SentAt";

    public async Task QueueAsync(NewEmailMessage message, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "INSERT INTO socalytics.email_messages (id, notification_type, account_id, address_hash, confirmation_id, state, next_attempt_at, created_at) " +
            "VALUES (@Id, @NotificationType, @AccountId, @AddressHash, @ConfirmationId, 'queued', @NextAttemptAt, @CreatedAt)",
            message, cancellationToken);

    public async Task<StoredEmailMessage?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.email_messages WHERE id = @id",
            new { id }, session.Transaction, cancellationToken: cancellationToken));
        return row?.ToStored();
    }

    public async Task<IReadOnlyList<StoredEmailMessage>> ClaimDueAsync(
        DateTimeOffset now, DateTimeOffset leaseUntil, int limit, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "WITH due AS (SELECT id FROM socalytics.email_messages " +
            "WHERE (state = 'queued' AND next_attempt_at <= @now) OR (state = 'sending' AND lease_until <= @now) " +
            "ORDER BY next_attempt_at, id LIMIT @limit FOR UPDATE SKIP LOCKED) " +
            "UPDATE socalytics.email_messages m SET state = 'sending', lease_until = @leaseUntil, attempt_count = m.attempt_count + 1 " +
            "FROM due WHERE m.id = due.id " +
            "RETURNING m.id AS Id, m.notification_type AS NotificationType, m.account_id AS AccountId, m.address_hash AS AddressHash, " +
            "m.confirmation_id AS ConfirmationId, m.state AS State, m.attempt_count AS AttemptCount, m.next_attempt_at AS NextAttemptAt, " +
            "m.lease_until AS LeaseUntil, m.last_error_category AS LastErrorCategory, m.created_at AS CreatedAt, m.sent_at AS SentAt",
            new { now, leaseUntil, limit }, transaction, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToStored()).OrderBy(m => m.NextAttemptAt).ThenBy(m => m.Id).ToList();
    }

    public async Task MarkSentAsync(Guid id, DateTimeOffset sentAt, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.email_messages SET state = 'sent', sent_at = @sentAt, lease_until = NULL, last_error_category = NULL " +
            "WHERE id = @id AND state = 'sending'",
            new { id, sentAt }, cancellationToken);

    public async Task MarkRetryAsync(Guid id, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.email_messages SET state = 'queued', next_attempt_at = @nextAttemptAt, lease_until = NULL, " +
            "last_error_category = 'transient' WHERE id = @id AND state = 'sending'",
            new { id, nextAttemptAt }, cancellationToken);

    public async Task MarkFailedAsync(Guid id, string errorCategory, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.email_messages SET state = 'failed', lease_until = NULL, last_error_category = @errorCategory " +
            "WHERE id = @id AND state = 'sending'",
            new { id, errorCategory }, cancellationToken);

    public async Task MarkDiscardedAsync(Guid id, string errorCategory, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.email_messages SET state = 'discarded', lease_until = NULL, last_error_category = @errorCategory " +
            "WHERE id = @id AND state IN ('queued', 'sending')",
            new { id, errorCategory }, cancellationToken);

    public async Task<int> CountCreatedSinceAsync(
        string notificationType, Guid accountId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM socalytics.email_messages " +
            "WHERE notification_type = @notificationType AND account_id = @accountId AND created_at >= @since",
            new { notificationType, accountId, since }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<DateTimeOffset?> OldestCreatedSinceAsync(
        string notificationType, Guid accountId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var oldest = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT min(created_at) FROM socalytics.email_messages " +
            "WHERE notification_type = @notificationType AND account_id = @accountId AND created_at >= @since",
            new { notificationType, accountId, since }, session.Transaction, cancellationToken: cancellationToken));
        return EmailRows.Utc(oldest);
    }

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
    }

    private sealed record Row(
        Guid Id, string NotificationType, Guid AccountId, string AddressHash, Guid? ConfirmationId, string State,
        int AttemptCount, DateTime NextAttemptAt, DateTime? LeaseUntil, string? LastErrorCategory, DateTime CreatedAt,
        DateTime? SentAt)
    {
        public StoredEmailMessage ToStored() =>
            new(Id, NotificationType, AccountId, AddressHash, ConfirmationId, State, AttemptCount,
                EmailRows.Utc(NextAttemptAt), EmailRows.Utc(LeaseUntil), LastErrorCategory, EmailRows.Utc(CreatedAt),
                EmailRows.Utc(SentAt));
    }
}
