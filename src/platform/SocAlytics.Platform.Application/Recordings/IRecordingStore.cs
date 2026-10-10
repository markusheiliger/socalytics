using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings;

/// <summary>An upload session together with the database clock used to derive its reported state.</summary>
public sealed record StoredUploadSession(UploadSession Session, DateTimeOffset DatabaseNow);

public sealed record StoredTimelineMapping(
    Guid Id,
    Guid RecordingVersionId,
    Guid MatchId,
    IReadOnlyList<TimelineSpan> Spans,
    string Digest,
    DateTimeOffset CreatedAt);

public sealed record StoredCompletedUpload(RecordingVersion Version, StoredTimelineMapping Mapping);

public sealed record ExpiredUploadSession(Guid Id, Guid MatchId, Guid TeamId);

public sealed record UnreleasedUploadStorage(Guid Id, string ObjectKey, string MultipartUploadId);

public interface IRecordingStore
{
    /// <summary>Inserts the session in the active unit of work.</summary>
    Task InsertUploadSessionAsync(UploadSession session, CancellationToken cancellationToken);

    Task<StoredUploadSession?> FindUploadSessionAsync(Guid matchId, Guid uploadSessionId, CancellationToken cancellationToken);

    /// <summary>Guarded pending-to-completed transition in the active unit of work; false when the guard did not match.</summary>
    Task<bool> TryCompleteUploadSessionAsync(Guid uploadSessionId, CancellationToken cancellationToken);

    /// <summary>Inserts the version and its first mapping in the active unit of work.</summary>
    Task InsertCompletedUploadAsync(RecordingVersion version, StoredTimelineMapping mapping, string canonicalSpansJson, CancellationToken cancellationToken);

    Task<StoredCompletedUpload?> FindCompletedUploadAsync(Guid matchId, Guid recordingVersionId, Guid timelineMappingId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListDueUploadSessionIdsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Guarded pending-to-expired transition in the active unit of work; null when the guard did not match.</summary>
    Task<ExpiredUploadSession?> TryExpireUploadSessionAsync(Guid uploadSessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<UnreleasedUploadStorage>> ListUnreleasedExpiredUploadsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Guarded storage release marker in the active unit of work; false when already released or not expired.</summary>
    Task<bool> TryMarkStorageReleasedAsync(Guid uploadSessionId, CancellationToken cancellationToken);
}
