namespace SocAlytics.Platform.Application.Messaging;

public sealed record OutboxMessageView(
    Guid MessageId, string MessageType, string ContractVersion, string Subject, string State, int AttemptCount, int PublicationRound,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt, string? FailureCategory, DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt, long AgeSeconds, long? PublicationLagSeconds, Guid? RunId, Guid? LogicalJobId, string? CapabilityId);

public interface IOutboxMessageQueries
{
    Task<OperationsPageResult<OutboxMessageView>> ListAsync(
        string? state, string? messageType, OperationsPageKey? after, int pageSize, DateTimeOffset now, CancellationToken cancellationToken);

    Task<OutboxMessageView?> GetAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken);
}
