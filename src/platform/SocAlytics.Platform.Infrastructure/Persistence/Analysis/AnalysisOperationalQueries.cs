using Dapper;
using SocAlytics.Platform.Application.Analysis.Queries;
using SocAlytics.Platform.Application.Messaging;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class AnalysisOperationalQueries(IDbSession session) : IAnalysisOperationalQueries
{
    private sealed record Row(
        Guid MessageId, string MessageType, Guid? RecordingSetVersionId, string Outcome, string? RejectionCategory, Guid? RunId, DateTime ReceivedAt);

    private sealed record StateCount(string State, int Count);

    private sealed record Scalars(int Blocked, int Stale, int Rejected, int Pending, int Failed, DateTime? OldestPending);

    public async Task<AnalysisOperationalCounts> GetCountsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var states = await connection.QueryAsync<StateCount>(new CommandDefinition(
            "SELECT state AS State, count(*)::int AS Count FROM socalytics.analysis_runs GROUP BY state",
            transaction: session.Transaction, cancellationToken: cancellationToken));
        var s = await connection.QuerySingleAsync<Scalars>(new CommandDefinition(
            """
            SELECT (SELECT count(DISTINCT run_id) FROM socalytics.analysis_workflow_nodes WHERE blocked_by_node_id IS NOT NULL)::int AS Blocked,
                   (SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE state = 'Stale')::int AS Stale,
                   (SELECT count(*) FROM socalytics.analysis_notification_receipts WHERE outcome = 'rejected')::int AS Rejected,
                   (SELECT count(*) FROM socalytics.outbox_messages WHERE state = 'pending')::int AS Pending,
                   (SELECT count(*) FROM socalytics.outbox_messages WHERE state = 'failed')::int AS Failed,
                   (SELECT min(created_at) FROM socalytics.outbox_messages WHERE state = 'pending') AS OldestPending
            """,
            transaction: session.Transaction, cancellationToken: cancellationToken));
        long? oldest = s.OldestPending is null
            ? null
            : Math.Max(0, (long)(now - new DateTimeOffset(DateTime.SpecifyKind(s.OldestPending.Value, DateTimeKind.Utc))).TotalSeconds);
        return new AnalysisOperationalCounts(
            states.ToDictionary(x => x.State, x => x.Count), s.Blocked, s.Stale, s.Rejected, s.Pending, s.Failed, oldest);
    }

    public async Task<OperationsPageResult<NotificationReceiptView>> ListReceiptsAsync(
        string? outcome, OperationsPageKey? after, int pageSize, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT message_id AS MessageId, message_type AS MessageType, recording_set_version_id AS RecordingSetVersionId,
                   outcome AS Outcome, rejection_category AS RejectionCategory, run_id AS RunId, received_at AS ReceivedAt
            FROM socalytics.analysis_notification_receipts
            WHERE (@outcome::text IS NULL OR outcome = @outcome)
              AND (@hasAfter = false OR (received_at, message_id) < (@afterAt, @afterId))
            ORDER BY received_at DESC, message_id DESC
            LIMIT @limit
            """,
            new
            {
                outcome, hasAfter = after is not null,
                afterAt = after?.At.UtcDateTime ?? DateTime.UtcNow, afterId = after?.Id ?? Guid.Empty, limit = pageSize + 1,
            },
            session.Transaction, cancellationToken: cancellationToken))).ToList();
        return new OperationsPageResult<NotificationReceiptView>(
            [.. rows.Take(pageSize).Select(r => new NotificationReceiptView(
                r.MessageId, r.MessageType, r.RecordingSetVersionId, r.Outcome, r.RejectionCategory, r.RunId,
                new DateTimeOffset(DateTime.SpecifyKind(r.ReceivedAt, DateTimeKind.Utc))))],
            rows.Count > pageSize);
    }

    public async Task<Guid?> GetClubIdAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM socalytics.club", transaction: session.Transaction, cancellationToken: cancellationToken));
    }
}
