using System.Text.Json;
using Dapper;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.Recordings;

internal sealed class RecordingSetLookup(IDbSession session) : IRecordingSetLookup
{
    private sealed record Row(
        Guid MatchId, Guid TeamId, DateTime FinalizedAt, int Position, Guid RecordingVersionId,
        string RecordingContentDigest, Guid TimelineMappingId, string TimelineMappingDigest, string Spans);

    public async Task<RecordingSetLineage?> GetAsync(Guid recordingSetVersionId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT s.match_id AS MatchId, s.team_id AS TeamId, s.finalized_at AS FinalizedAt, " +
            "m.position AS Position, m.recording_version_id AS RecordingVersionId, v.content_digest AS RecordingContentDigest, " +
            "m.timeline_mapping_id AS TimelineMappingId, t.mapping_digest AS TimelineMappingDigest, t.spans::text AS Spans " +
            "FROM socalytics.recording_set_versions s " +
            "JOIN socalytics.recording_set_members m ON m.recording_set_version_id = s.id " +
            "JOIN socalytics.recording_versions v ON v.id = m.recording_version_id " +
            "JOIN socalytics.recording_timeline_mappings t ON t.id = m.timeline_mapping_id " +
            "WHERE s.id = @recordingSetVersionId ORDER BY m.position",
            new { recordingSetVersionId },
            session.Transaction,
            cancellationToken: cancellationToken))).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var first = rows[0];
        return new RecordingSetLineage(
            recordingSetVersionId,
            first.MatchId,
            first.TeamId,
            new DateTimeOffset(DateTime.SpecifyKind(first.FinalizedAt, DateTimeKind.Utc)),
            [.. rows.Select(r => new RecordingSetLineageMember(
                r.Position, r.RecordingVersionId, r.RecordingContentDigest, r.TimelineMappingId, r.TimelineMappingDigest, ParseSpans(r.Spans)))]);
    }

    public async Task<RecordingSetLineage?> GetCurrentForMatchAsync(Guid matchId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var currentId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM socalytics.recording_set_versions WHERE match_id = @matchId " +
            "ORDER BY finalized_at DESC, id DESC LIMIT 1",
            new { matchId },
            session.Transaction,
            cancellationToken: cancellationToken));
        return currentId is null ? null : await GetAsync(currentId.Value, cancellationToken);
    }

    private static List<TimelineSpan> ParseSpans(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("spans").EnumerateArray().Select(span => new TimelineSpan(
            span.GetProperty("mediaStartMilliseconds").GetInt64(),
            span.GetProperty("mediaEndMilliseconds").GetInt64(),
            span.GetProperty("matchStartMilliseconds").GetInt64()))];
    }
}
