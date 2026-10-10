using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings;

public sealed record RecordingSetLineage(
    Guid RecordingSetVersionId,
    Guid MatchId,
    Guid TeamId,
    DateTimeOffset FinalizedAt,
    IReadOnlyList<RecordingSetLineageMember> Members);

public sealed record RecordingSetLineageMember(
    int Position,
    Guid RecordingVersionId,
    string RecordingContentDigest,
    Guid TimelineMappingId,
    string TimelineMappingDigest,
    IReadOnlyList<TimelineSpan> Spans);
