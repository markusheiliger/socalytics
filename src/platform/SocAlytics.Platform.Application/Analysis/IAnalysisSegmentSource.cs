using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Analysis;

public interface IAnalysisSegmentSource
{
    IReadOnlyList<AnalysisSegmentReference> GetSegments(RecordingSetLineage lineage, int segmentDurationSeconds, Sha256Digest segmentationPolicyDigest);
}
