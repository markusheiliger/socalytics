namespace SocAlytics.Platform.Application.Recordings;

/// <summary>Stored outcome; <paramref name="Result"/> is identity-only JSON, never grants or credentials.</summary>
public sealed record RecordingRetryOutcome(string RequestDigest, int StatusCode, string Result);

public interface IRecordingRetryOutcomeStore
{
    Task<RecordingRetryOutcome?> FindAsync(RecordingRetryOperation operation, Guid matchId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Inserts in the active unit of work.</summary>
    /// <exception cref="DuplicateRetryKeyException">The key already exists.</exception>
    Task InsertAsync(
        RecordingRetryOperation operation,
        Guid matchId,
        string idempotencyKey,
        string requestDigest,
        int statusCode,
        string resultJson,
        Guid createdBy,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken);
}
