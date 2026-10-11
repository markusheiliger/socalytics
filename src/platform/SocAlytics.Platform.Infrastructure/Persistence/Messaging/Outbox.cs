using Dapper;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Infrastructure.Persistence.Messaging;

internal sealed class Outbox(IDbSession session, TimeProvider timeProvider) : IOutbox
{
    private const string InsertSql =
        """
        INSERT INTO socalytics.outbox_messages
            (id, message_type, contract_version, subject, payload, causation_id, run_id, logical_job_id,
             capability_id, state, attempt_count, publication_round, created_at)
        VALUES
            (@id, @messageType, @contractVersion, @subject, CAST(@payload AS jsonb), @causationId, @runId,
             @logicalJobId, @capabilityId, 'pending', 0, 1, @createdAt)
        ON CONFLICT (message_type, causation_id) DO NOTHING
        """;

    public async Task<bool> EnqueueAsync(OutboxMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                id = draft.Id,
                messageType = draft.MessageType,
                contractVersion = draft.ContractVersion,
                subject = draft.Subject,
                payload = draft.Payload.GetRawText(),
                causationId = draft.CausationId,
                runId = draft.RunId,
                logicalJobId = draft.LogicalJobId,
                capabilityId = draft.CapabilityId,
                createdAt = timeProvider.GetUtcNow(),
            },
            transaction,
            cancellationToken: cancellationToken));
        return rows == 1;
    }

    public async Task<int> DiscardUnpublishedReadyWorkAsync(Guid logicalJobId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.outbox_messages SET state = 'discarded', next_attempt_at = NULL
            WHERE logical_job_id = @logicalJobId AND message_type = 'analysis.ready-work' AND state IN ('pending', 'failed')
            """,
            new { logicalJobId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
