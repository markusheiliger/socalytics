namespace SocAlytics.Platform.Application.Analysis.Stores;

public sealed record NotificationReceiptRecord(
    Guid MessageId,
    string MessageType,
    Guid? RecordingSetVersionId,
    string Outcome,
    string? RejectionCategory,
    Guid? RunId,
    DateTimeOffset ReceivedAt);

public sealed record NotificationReceiptInsertResult(bool Inserted, NotificationReceiptRecord Receipt);

public interface INotificationReceiptStore
{
    Task<NotificationReceiptInsertResult> TryInsertAsync(NotificationReceiptRecord receipt, CancellationToken cancellationToken);

    Task<IReadOnlyList<NotificationReceiptRecord>> ListAsync(int skip, int take, CancellationToken cancellationToken);
}
