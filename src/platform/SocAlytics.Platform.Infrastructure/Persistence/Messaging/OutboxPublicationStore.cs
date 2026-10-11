using Dapper;
using SocAlytics.Platform.Application.Messaging;

namespace SocAlytics.Platform.Infrastructure.Persistence.Messaging;

internal sealed class OutboxPublicationStore(IDbSession session) : IOutboxPublicationStore
{
    public async Task<IReadOnlyList<OutboxPublicationRow>> ClaimDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OutboxPublicationRow>(new CommandDefinition(
            """
            SELECT id AS Id, subject AS Subject, payload::text AS Payload, attempt_count AS AttemptCount,
                   publication_round AS PublicationRound
            FROM socalytics.outbox_messages
            WHERE state = 'pending' AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
            ORDER BY created_at, id
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """,
            new { now, batchSize },
            transaction,
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task MarkPublishedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.outbox_messages
            SET state = 'published', published_at = @now, last_attempt_at = @now,
                next_attempt_at = NULL, failure_category = NULL
            WHERE id = @id
            """,
            new { id, now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task MarkAttemptFailedAsync(
        Guid id, DateTimeOffset now, string failureCategory, DateTimeOffset? nextAttemptAt, bool failed, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.outbox_messages
            SET attempt_count = attempt_count + 1, last_attempt_at = @now, failure_category = @failureCategory,
                next_attempt_at = @nextAttemptAt, state = CASE WHEN @failed THEN 'failed' ELSE state END
            WHERE id = @id
            """,
            new { id, now, failureCategory, nextAttemptAt, failed },
            transaction,
            cancellationToken: cancellationToken));
    }
}
