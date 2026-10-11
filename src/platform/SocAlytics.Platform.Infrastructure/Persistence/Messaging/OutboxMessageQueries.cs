using Dapper;
using SocAlytics.Platform.Application.Messaging;

namespace SocAlytics.Platform.Infrastructure.Persistence.Messaging;

internal sealed class OutboxMessageQueries(IDbSession session) : IOutboxMessageQueries
{
    private const string Select =
        """
        SELECT id AS MessageId, message_type AS MessageType, contract_version AS ContractVersion, subject AS Subject, state AS State,
               attempt_count AS AttemptCount, publication_round AS PublicationRound, last_attempt_at AS LastAttemptAt,
               next_attempt_at AS NextAttemptAt, failure_category AS FailureCategory, created_at AS CreatedAt, published_at AS PublishedAt,
               run_id AS RunId, logical_job_id AS LogicalJobId, capability_id AS CapabilityId
        FROM socalytics.outbox_messages
        """;

    private sealed record Row(
        Guid MessageId, string MessageType, string ContractVersion, string Subject, string State, int AttemptCount, int PublicationRound,
        DateTime? LastAttemptAt, DateTime? NextAttemptAt, string? FailureCategory, DateTime CreatedAt, DateTime? PublishedAt,
        Guid? RunId, Guid? LogicalJobId, string? CapabilityId);

    private static DateTimeOffset Utc(DateTime v) => new(DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? v) => v is null ? null : Utc(v.Value);

    private static OutboxMessageView ToView(Row r, DateTimeOffset now)
    {
        var created = Utc(r.CreatedAt);
        var published = Utc(r.PublishedAt);
        return new OutboxMessageView(
            r.MessageId, r.MessageType, r.ContractVersion, r.Subject, r.State, r.AttemptCount, r.PublicationRound,
            Utc(r.LastAttemptAt), Utc(r.NextAttemptAt), r.FailureCategory, created, published,
            Math.Max(0, (long)(now - created).TotalSeconds),
            published is null ? null : Math.Max(0, (long)(published.Value - created).TotalSeconds),
            r.RunId, r.LogicalJobId, r.CapabilityId);
    }

    public async Task<OperationsPageResult<OutboxMessageView>> ListAsync(
        string? state, string? messageType, OperationsPageKey? after, int pageSize, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            Select +
            """

            WHERE (@state::text IS NULL OR state = @state)
              AND (@messageType::text IS NULL OR message_type = @messageType)
              AND (@hasAfter = false OR (created_at, id) > (@afterAt, @afterId))
            ORDER BY created_at, id
            LIMIT @limit
            """,
            new
            {
                state, messageType, hasAfter = after is not null,
                afterAt = after?.At.UtcDateTime ?? DateTime.UtcNow, afterId = after?.Id ?? Guid.Empty, limit = pageSize + 1,
            },
            session.Transaction, cancellationToken: cancellationToken))).ToList();
        return new OperationsPageResult<OutboxMessageView>([.. rows.Take(pageSize).Select(r => ToView(r, now))], rows.Count > pageSize);
    }

    public async Task<OutboxMessageView?> GetAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Select + " WHERE id = @messageId", new { messageId }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToView(row, now);
    }
}
