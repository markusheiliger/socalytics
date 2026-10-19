using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Stores;

public sealed record LogicalJobSnapshotRecord(
    int MaxAttempts,
    int LeaseSeconds,
    int StaleTimeoutSeconds,
    int ExecutionTimeoutSeconds,
    string AnalystProfileDigest,
    string ImageDigest,
    string? ModelDigest,
    string ResourcesJson,
    string SchemaVersion);

public sealed record LogicalJobRecord(
    Guid Id,
    Guid RunId,
    Guid NodeId,
    ExecutionScope ExecutionScope,
    Guid? RecordingVersionId,
    string? TimelineMappingDigest,
    string? SegmentationPolicyDigest,
    int? SegmentNumber,
    LogicalJobState State,
    int AttemptsUsed,
    Guid? CurrentReadinessOccurrenceId,
    Guid? CurrentAttemptId,
    string? FailureReason,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record NewLogicalJob(
    Guid Id,
    Guid RunId,
    Guid NodeId,
    ExecutionScope ExecutionScope,
    Guid? RecordingVersionId,
    string? TimelineMappingDigest,
    string? SegmentationPolicyDigest,
    int? SegmentNumber,
    LogicalJobState State,
    LogicalJobSnapshotRecord Snapshot);

public interface ILogicalJobStore
{
    // Job ids are assigned by the caller (Guid.CreateVersion7).
    Task InsertAsync(IReadOnlyList<NewLogicalJob> jobs, CancellationToken cancellationToken);

    Task<IReadOnlyList<LogicalJobRecord>> LoadByRunAsync(Guid runId, CancellationToken cancellationToken);

    Task<LogicalJobRecord?> LockJobAsync(Guid jobId, CancellationToken cancellationToken);

    Task<LogicalJobSnapshotRecord?> LoadSnapshotAsync(Guid jobId, CancellationToken cancellationToken);

    // Updates state only while the job is in expectedState; failureReason and attemptsUsed are kept when null.
    Task<bool> TryTransitionAsync(
        Guid jobId,
        LogicalJobState expectedState,
        LogicalJobState newState,
        string? failureReason,
        int? attemptsUsed,
        CancellationToken cancellationToken);

    // Returns false when the inputs were already recorded.
    Task<bool> TryInsertInputsAsync(Guid jobId, string inputsJson, CancellationToken cancellationToken);

    // Writes the entries, including coverage, as the job's input snapshot; returns false when already recorded.
    Task<bool> TryInsertInputSnapshotAsync(Guid jobId, IReadOnlyList<InputSnapshotEntry> entries, CancellationToken cancellationToken);

    // Snapshots written without coverage fields are read as complete.
    Task<IReadOnlyList<InputSnapshotEntry>> LoadInputSnapshotAsync(Guid jobId, CancellationToken cancellationToken);

    Task<string?> LoadInputsAsync(Guid jobId, CancellationToken cancellationToken);

    // Assigns the next sequence (1, 2, …) for the job and returns it.
    Task<int> InsertReadinessOccurrenceAsync(Guid jobId, Guid occurrenceId, string cause, CancellationToken cancellationToken);

    Task SetCurrentOccurrenceAsync(Guid jobId, Guid? occurrenceId, Guid? attemptId, CancellationToken cancellationToken);
}
