using Dapper;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.Recordings;

internal sealed class RecordingLineageQueries(IDbSession session) : IRecordingLineageQueries
{
    private sealed record Row(
        Guid Id, Guid MatchId, Guid TeamId, DateTime FinalizedAt, int Position, Guid RecordingVersionId,
        string RecordingContentDigest, Guid TimelineMappingId, string TimelineMappingDigest);

    public async Task<RecordingSetVersionView?> GetSetVersionAsync(
        Guid matchId, Guid recordingSetVersionId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT s.id AS Id, s.match_id AS MatchId, s.team_id AS TeamId, s.finalized_at AS FinalizedAt, " +
            "m.position AS Position, m.recording_version_id AS RecordingVersionId, v.content_digest AS RecordingContentDigest, " +
            "m.timeline_mapping_id AS TimelineMappingId, t.mapping_digest AS TimelineMappingDigest " +
            "FROM socalytics.recording_set_versions s " +
            "JOIN socalytics.recording_set_members m ON m.recording_set_version_id = s.id " +
            "JOIN socalytics.recording_versions v ON v.id = m.recording_version_id " +
            "JOIN socalytics.recording_timeline_mappings t ON t.id = m.timeline_mapping_id " +
            "WHERE s.id = @recordingSetVersionId AND s.match_id = @matchId ORDER BY m.position",
            new { recordingSetVersionId, matchId },
            session.Transaction,
            cancellationToken: cancellationToken))).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var first = rows[0];
        return new RecordingSetVersionView(
            first.Id,
            first.MatchId,
            first.TeamId,
            new DateTimeOffset(DateTime.SpecifyKind(first.FinalizedAt, DateTimeKind.Utc)),
            [.. rows.Select(r => new RecordingSetMemberView(
                r.Position, r.RecordingVersionId, r.RecordingContentDigest, r.TimelineMappingId, r.TimelineMappingDigest))]);
    }
}
