using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class NotificationReceiptStore(IDbSession session) : INotificationReceiptStore
{
    private const string Select =
        """
        SELECT message_id AS MessageId, message_type AS MessageType, recording_set_version_id AS RecordingSetVersionId,
               outcome AS Outcome, rejection_category AS RejectionCategory, run_id AS RunId, received_at AS ReceivedAt
        FROM socalytics.analysis_notification_receipts
        """;

    private sealed record Row(
        Guid MessageId, string MessageType, Guid? RecordingSetVersionId, string Outcome, string? RejectionCategory,
        Guid? RunId, DateTime ReceivedAt);

    private static NotificationReceiptRecord ToRecord(Row r) => new(
        r.MessageId, r.MessageType, r.RecordingSetVersionId, r.Outcome, r.RejectionCategory, r.RunId,
        new DateTimeOffset(DateTime.SpecifyKind(r.ReceivedAt, DateTimeKind.Utc)));

    public async Task<NotificationReceiptInsertResult> TryInsertAsync(NotificationReceiptRecord receipt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_notification_receipts
                (message_id, message_type, recording_set_version_id, outcome, rejection_category, run_id, received_at)
            VALUES (@id, @type, @set, @outcome, @category, @run, @receivedAt)
            ON CONFLICT (message_id) DO NOTHING
            """,
            new
            {
                id = receipt.MessageId,
                type = receipt.MessageType,
                set = receipt.RecordingSetVersionId,
                outcome = receipt.Outcome,
                category = receipt.RejectionCategory,
                run = receipt.RunId,
                receivedAt = receipt.ReceivedAt,
            },
            transaction,
            cancellationToken: cancellationToken)) == 1;
        if (inserted)
        {
            return new NotificationReceiptInsertResult(true, receipt);
        }

        var row = await connection.QuerySingleAsync<Row>(new CommandDefinition(
            Select + " WHERE message_id = @id", new { id = receipt.MessageId }, transaction, cancellationToken: cancellationToken));
        return new NotificationReceiptInsertResult(false, ToRecord(row));
    }

    public async Task<IReadOnlyList<NotificationReceiptRecord>> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Select + " ORDER BY received_at DESC, message_id OFFSET @skip LIMIT @take", new { skip, take },
            session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToRecord).ToList();
    }
}
