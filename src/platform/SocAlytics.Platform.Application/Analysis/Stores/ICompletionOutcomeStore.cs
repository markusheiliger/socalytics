namespace SocAlytics.Platform.Application.Analysis.Stores;

public sealed record CompletionOutcomeRecord(
    Guid LogicalJobId,
    string IdempotencyKey,
    Guid AttemptId,
    string RequestFingerprint,
    string Outcome,
    Guid? AcceptedResultId,
    DateTimeOffset RecordedAt,
    string? Reason = null);

public interface ICompletionOutcomeStore
{
    Task InsertAsync(CompletionOutcomeRecord outcome, CancellationToken cancellationToken);

    Task<CompletionOutcomeRecord?> GetAsync(Guid logicalJobId, string idempotencyKey, CancellationToken cancellationToken);
}
