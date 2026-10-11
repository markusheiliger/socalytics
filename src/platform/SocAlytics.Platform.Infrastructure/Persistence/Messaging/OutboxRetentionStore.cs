using Dapper;
using SocAlytics.Platform.Application.Messaging;

namespace SocAlytics.Platform.Infrastructure.Persistence.Messaging;

internal sealed class OutboxRetentionStore(IDbSession session) : IOutboxRetentionStore
{
    public async Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM socalytics.outbox_messages
            WHERE id IN (
                SELECT m.id FROM socalytics.outbox_messages m
                WHERE ((m.state = 'published' AND m.published_at < @cutoff)
                       OR (m.state = 'discarded' AND m.created_at < @cutoff))
                  AND NOT EXISTS (
                      SELECT 1 FROM socalytics.analysis_logical_jobs j
                      WHERE j.state = 'Ready' AND j.current_readiness_occurrence_id = m.causation_id)
                ORDER BY m.created_at, m.id
                LIMIT @batchSize
                FOR UPDATE OF m SKIP LOCKED)
            """,
            new { cutoff, batchSize }, transaction, cancellationToken: cancellationToken));
    }
}
