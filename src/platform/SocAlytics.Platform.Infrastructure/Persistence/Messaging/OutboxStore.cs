using Dapper;

namespace SocAlytics.Platform.Infrastructure.Persistence.Messaging;

internal sealed record OutboxMessageRecord(
    Guid Id,
    string MessageType,
    string ContractVersion,
    string Subject,
    string Payload,
    Guid CausationId,
    Guid? RunId,
    Guid? LogicalJobId,
    string? CapabilityId,
    string State,
    int AttemptCount,
    int PublicationRound,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt,
    string? FailureCategory,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt);

internal sealed class OutboxStore(IDbSession session)
{
    private const string SelectSql =
        """
        SELECT id AS Id, message_type AS MessageType, contract_version AS ContractVersion, subject AS Subject,
               payload::text AS Payload, causation_id AS CausationId, run_id AS RunId,
               logical_job_id AS LogicalJobId, capability_id AS CapabilityId, state AS State,
               attempt_count AS AttemptCount, publication_round AS PublicationRound,
               last_attempt_at AS LastAttemptAt, next_attempt_at AS NextAttemptAt,
               failure_category AS FailureCategory, created_at AS CreatedAt, published_at AS PublishedAt
        FROM socalytics.outbox_messages
        """;

    public async Task<OutboxMessageRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<OutboxMessageRecord>(new CommandDefinition(
            SelectSql + " WHERE id = @id", new { id }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<OutboxMessageRecord>> ListByStateAsync(string state, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OutboxMessageRecord>(new CommandDefinition(
            SelectSql + " WHERE state = @state ORDER BY created_at, id LIMIT @limit",
            new { state, limit },
            session.Transaction,
            cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
