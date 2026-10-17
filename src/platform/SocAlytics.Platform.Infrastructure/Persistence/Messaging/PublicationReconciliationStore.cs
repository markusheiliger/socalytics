using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;

namespace SocAlytics.Platform.Infrastructure.Persistence.Messaging;

internal sealed class PublicationReconciliationStore(IDbSession session) : IPublicationReconciliationStore
{
    private const string Reset =
        "state = 'pending', publication_round = publication_round + 1, attempt_count = 0, next_attempt_at = NULL, failure_category = NULL";

    public async Task<IReadOnlyList<Guid>> ListRunsWithReadyJobsAsync(Guid after, int batchSize, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT DISTINCT run_id FROM socalytics.analysis_logical_jobs
            WHERE state = 'Ready' AND run_id > @after
            ORDER BY run_id LIMIT @batchSize
            """,
            new { after, batchSize }, transaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<int> ResetReadyWorkAsync(Guid runId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE socalytics.outbox_messages m SET {Reset}
            FROM socalytics.analysis_logical_jobs j
            WHERE j.run_id = @runId AND j.state = 'Ready'
              AND m.message_type = 'analysis.ready-work'
              AND m.causation_id = j.current_readiness_occurrence_id
            """,
            new { runId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<int> DiscardReadyWorkAsync(Guid runId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.outbox_messages m SET state = 'discarded', next_attempt_at = NULL
            FROM socalytics.analysis_logical_jobs j
            WHERE j.run_id = @runId AND m.logical_job_id = j.id
              AND m.message_type = 'analysis.ready-work' AND m.state IN ('pending', 'failed')
            """,
            new { runId }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> ListNotificationsAsync(Guid after, int batchSize, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT m.id FROM socalytics.outbox_messages m
            WHERE m.id > @after AND (
                (m.message_type = 'analysis.run-state-changed' AND m.state IN ('pending', 'failed'))
                OR (m.message_type = 'matches.recordings-finalized' AND (
                    m.state IN ('pending', 'failed')
                    OR (m.state = 'published' AND NOT EXISTS (
                        SELECT 1 FROM socalytics.analysis_notification_receipts r WHERE r.message_id = m.id)))))
            ORDER BY m.id LIMIT @batchSize
            """,
            new { after, batchSize }, transaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<int> ResetMessagesAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE socalytics.outbox_messages SET {Reset} WHERE id = ANY(@ids)",
            new { ids = ids.ToArray() }, transaction, cancellationToken: cancellationToken));
    }
}
