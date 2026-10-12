using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Infrastructure.Analysis;

internal sealed class LineageAnalysisSegmentSource : IAnalysisSegmentSource
{
    public IReadOnlyList<AnalysisSegmentReference> GetSegments(RecordingSetLineage lineage, int segmentDurationSeconds, Sha256Digest segmentationPolicyDigest)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(segmentationPolicyDigest);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(segmentDurationSeconds, 0);

        var window = segmentDurationSeconds * 1000L;
        var result = new List<AnalysisSegmentReference>();
        foreach (var member in lineage.Members.OrderBy(m => m.Position))
        {
            if (!Sha256Digest.TryParse(member.TimelineMappingDigest, out var mappingDigest))
            {
                throw new InvalidOperationException("The timeline mapping digest of a lineage member is malformed.");
            }

            var numbers = new SortedSet<int>();
            foreach (var span in member.Spans)
            {
                var length = span.MediaEndMilliseconds - span.MediaStartMilliseconds;
                if (length <= 0)
                {
                    continue;
                }

                var start = span.MatchStartMilliseconds;
                var end = start + length;
                var first = (int)(Math.Max(start, 0) / window) + 1;
                var last = (int)((end - 1) / window) + 1;
                for (var n = first; n <= last; n++)
                {
                    numbers.Add(n);
                }
            }

            foreach (var n in numbers)
            {
                result.Add(new AnalysisSegmentReference(member.RecordingVersionId, mappingDigest, segmentationPolicyDigest, n));
            }
        }

        return result;
    }
}
