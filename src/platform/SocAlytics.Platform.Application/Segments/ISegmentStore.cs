using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Application.Segments;

// The current association of an available segment; immutable once published.
public sealed record SegmentAssociation(
    string MediaObjectKey,
    string ManifestObjectKey,
    string MediaDigest,
    long MediaSizeBytes,
    string ManifestChecksum);

public sealed record MaterializedSegmentRecord(
    Guid Id,
    MaterializedSegmentIdentity Identity,
    int PolicyVersion,
    int DurationSeconds,
    MaterializedSegmentState State,
    bool Demanded,
    Guid? CandidateId,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    int AttemptsUsed,
    DateTimeOffset? NextAttemptAt,
    string? FailureReason,
    SegmentAssociation? Association,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AvailableAt,
    DateTimeOffset? PurgedAt,
    long Version);

// A claimed row; ReclaimedCandidateId is the expired candidate whose objects the worker deletes.
public sealed record ClaimedSegment(MaterializedSegmentRecord Segment, Guid? ReclaimedCandidateId);

public sealed record WaitingSegmentJob(Guid JobId, Guid RunId);

public sealed record SegmentPurgeCandidate(MaterializedSegmentRecord Segment, bool InUse);

// Every write requires an active unit of work.
public interface ISegmentStore
{
    // Creates the row as absent and demanded, or sets demand on an existing absent row; a failed row returns to
    // absent with attempts_used reset to 0. Available and materializing rows are returned unchanged.
    Task<MaterializedSegmentRecord> DemandAsync(
        MaterializedSegmentIdentity identity,
        int policyVersion,
        int durationSeconds,
        CancellationToken cancellationToken);

    // Claims up to limit demanded absent rows due for an attempt and materializing rows with expired leases in
    // creation order, skipping rows locked by other workers; each claim gets a new candidate and consumes an attempt.
    Task<IReadOnlyList<ClaimedSegment>> ClaimDueAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        int limit,
        CancellationToken cancellationToken);

    // Compare-and-set on the row's materializing state and candidate; false when the candidate no longer owns it.
    Task<bool> TryPublishAsync(
        Guid segmentId,
        Guid candidateId,
        SegmentAssociation association,
        CancellationToken cancellationToken);

    // Returns the row to absent and demanded, due at retryAt, while the candidate owns it.
    Task<bool> TryRecordTransientFailureAsync(
        Guid segmentId,
        Guid candidateId,
        DateTimeOffset retryAt,
        CancellationToken cancellationToken);

    // Moves the row to failed with a sanitized reason while the candidate owns it.
    Task<bool> TryFailAsync(Guid segmentId, Guid candidateId, string failureReason, CancellationToken cancellationToken);

    // Moves an available row to failed and returns its previous association so the caller deletes its objects;
    // null when the row is not available with the expected media digest.
    Task<SegmentAssociation?> TryInvalidateAvailableAsync(
        Guid segmentId,
        string expectedMediaDigest,
        string failureReason,
        CancellationToken cancellationToken);

    Task<MaterializedSegmentRecord?> FindAsync(Guid segmentId, CancellationToken cancellationToken);

    Task<MaterializedSegmentRecord?> FindAsync(MaterializedSegmentIdentity identity, CancellationToken cancellationToken);

    Task<IReadOnlyList<MaterializedSegmentRecord>> ListByRecordingVersionsAsync(
        IReadOnlyCollection<Guid> recordingVersionIds,
        CancellationToken cancellationToken);

    // Waiting jobs of Requested or Running runs that await the segment, in job id order.
    Task<IReadOnlyList<WaitingSegmentJob>> ListWaitingJobsAsync(
        MaterializedSegmentIdentity identity,
        CancellationToken cancellationToken);

    // Locks the rows of the recording versions in id order; a row is in use while it is materializing or a Waiting,
    // Ready, or Claimed job of a Requested or Running run references its identity.
    Task<IReadOnlyList<SegmentPurgeCandidate>> LockForPurgeAsync(
        IReadOnlyCollection<Guid> recordingVersionIds,
        CancellationToken cancellationToken);

    // Returns an available or failed row to absent without demand and clears its association.
    Task<bool> TryPurgeAsync(Guid segmentId, CancellationToken cancellationToken);
}
