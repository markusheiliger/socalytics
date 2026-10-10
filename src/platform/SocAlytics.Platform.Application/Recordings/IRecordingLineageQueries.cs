namespace SocAlytics.Platform.Application.Recordings;

public sealed record RecordingSetMemberView(
    int Position,
    Guid RecordingVersionId,
    string RecordingContentDigest,
    Guid TimelineMappingId,
    string TimelineMappingDigest);

public sealed record RecordingSetVersionView(
    Guid Id,
    Guid MatchId,
    Guid TeamId,
    DateTimeOffset FinalizedAt,
    IReadOnlyList<RecordingSetMemberView> Members);

public sealed record MatchRecordingLineageView(
    Guid MatchId,
    Guid TeamId,
    IReadOnlyList<SocAlytics.Platform.Domain.Recordings.RecordingVersion> RecordingVersions,
    IReadOnlyList<StoredTimelineMapping> TimelineMappings,
    IReadOnlyList<RecordingSetVersionView> RecordingSetVersions);

public interface IRecordingLineageQueries
{
    /// <summary>All recording versions, timeline mappings, and finalized set versions of the Match.</summary>
    Task<MatchRecordingLineageView> GetMatchLineageAsync(Guid matchId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>The set version of the Match with ordered members and digests; null when it does not exist in that Match.</summary>
    Task<RecordingSetVersionView?> GetSetVersionAsync(Guid matchId, Guid recordingSetVersionId, CancellationToken cancellationToken);

    /// <summary>The recording version of the Match; null when it does not exist in that Match.</summary>
    Task<SocAlytics.Platform.Domain.Recordings.RecordingVersion?> GetRecordingVersionAsync(Guid matchId, Guid recordingVersionId, CancellationToken cancellationToken);

    /// <summary>The timeline mapping of the Match; null when it does not exist in that Match.</summary>
    Task<StoredTimelineMapping?> GetTimelineMappingAsync(Guid matchId, Guid timelineMappingId, CancellationToken cancellationToken);
}
