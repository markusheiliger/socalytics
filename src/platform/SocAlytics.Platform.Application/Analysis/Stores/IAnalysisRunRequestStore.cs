namespace SocAlytics.Platform.Application.Analysis.Stores;

public static class AnalysisRunRequestOutcomes
{
    public const string RunCreated = "run-created";
    public const string RunExists = "run-exists";
    public const string Rejected = "rejected";
}

public sealed record AnalysisRunRequestRecord(
    string IdempotencyKey,
    Guid MatchId,
    Guid RequestedBy,
    Guid RequestId,
    string Outcome,
    string? RejectionCategory,
    Guid? RecordingSetVersionId,
    Guid? RunId,
    Guid? CurrentRunId,
    DateTimeOffset CreatedAt);

public sealed record AnalysisRunRequestInsertResult(bool Inserted, AnalysisRunRequestRecord Record);

public interface IAnalysisRunRequestStore
{
    Task<AnalysisRunRequestRecord?> FindAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the record in the caller's unit of work; a concurrent duplicate of the same key wins and its record is returned.
    /// </summary>
    Task<AnalysisRunRequestInsertResult> TryInsertAsync(AnalysisRunRequestRecord record, CancellationToken cancellationToken);
}
