using Dapper;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.Segments;

internal sealed class SegmentSourceLookup(IDbSession session, IRecordingLineageQueries lineage) : ISegmentSourceLookup
{
    private sealed record VersionRow(Guid MatchId, string ObjectKey);

    public async Task<SegmentSource?> FindAsync(
        Guid recordingVersionId, string timelineMappingDigest, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var version = await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
            "SELECT match_id AS MatchId, object_key AS ObjectKey FROM socalytics.recording_versions WHERE id = @recordingVersionId",
            new { recordingVersionId },
            session.Transaction,
            cancellationToken: cancellationToken));
        if (version is null)
        {
            return null;
        }

        var mappingId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM socalytics.recording_timeline_mappings " +
            "WHERE recording_version_id = @recordingVersionId AND mapping_digest = @timelineMappingDigest",
            new { recordingVersionId, timelineMappingDigest },
            session.Transaction,
            cancellationToken: cancellationToken));
        if (mappingId is null)
        {
            return null;
        }

        var mapping = await lineage.GetTimelineMappingAsync(version.MatchId, mappingId.Value, cancellationToken);
        return mapping is null ? null : new SegmentSource(version.MatchId, version.ObjectKey, mapping.Spans);
    }
}
