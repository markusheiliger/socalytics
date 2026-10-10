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
}
