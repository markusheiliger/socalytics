namespace SocAlytics.Platform.Application.Messaging;

public sealed record OutboxPublicationRow(Guid Id, string Subject, string Payload, int AttemptCount, int PublicationRound, DateTime CreatedAt, string MessageType = "");

public interface IOutboxPublicationStore
{
    Task<IReadOnlyList<OutboxPublicationRow>> ClaimDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    Task<Telemetry.OutboxBacklog> GetBacklogAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkPublishedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    Task MarkAttemptFailedAsync(
        Guid id,
        DateTimeOffset now,
        string failureCategory,
        DateTimeOffset? nextAttemptAt,
        bool failed,
        CancellationToken cancellationToken);
}
