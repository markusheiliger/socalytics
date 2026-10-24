using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Segments;

// The stored source recording and the mapped spans of one segment identity's recording version and mapping digest.
public sealed record SegmentSource(Guid MatchId, string SourceObjectKey, IReadOnlyList<TimelineSpan> Spans);

public interface ISegmentSourceLookup
{
    // Null when the recording version or an immutable mapping with that digest does not exist.
    Task<SegmentSource?> FindAsync(Guid recordingVersionId, string timelineMappingDigest, CancellationToken cancellationToken);
}
