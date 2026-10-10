using Dapper;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Recordings;
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

    private sealed record VersionRow(
        Guid Id, Guid MatchId, Guid TeamId, Guid UploadSessionId, string ObjectKey, long TotalSizeBytes, long PartSizeBytes,
        int PartCount, string ContentDigest, string DisplayName, string? Description, string ContentType, string? StorageETag,
        Guid CreatedBy, DateTime CreatedAt);

    public async Task<RecordingVersion?> GetRecordingVersionAsync(
        Guid matchId, Guid recordingVersionId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
            "SELECT id AS Id, match_id AS MatchId, team_id AS TeamId, upload_session_id AS UploadSessionId, object_key AS ObjectKey, " +
            "total_size_bytes AS TotalSizeBytes, part_size_bytes AS PartSizeBytes, part_count AS PartCount, " +
            "content_digest AS ContentDigest, display_name AS DisplayName, description AS Description, content_type AS ContentType, " +
            "storage_etag AS StorageETag, created_by AS CreatedBy, created_at AS CreatedAt " +
            "FROM socalytics.recording_versions WHERE id = @recordingVersionId AND match_id = @matchId",
            new { recordingVersionId, matchId },
            session.Transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        if (!RecordingDescriptor.TryCreate(row.DisplayName, row.Description, row.ContentType, out var descriptor, out _))
        {
            throw new InvalidOperationException($"Recording version {row.Id} holds an invalid stored descriptor.");
        }

        return new RecordingVersion(
            row.Id, row.MatchId, row.TeamId, row.UploadSessionId, row.ObjectKey, row.TotalSizeBytes, row.PartSizeBytes,
            row.PartCount, CompositeContentDigest.Parse(row.ContentDigest), descriptor!, row.StorageETag, row.CreatedBy,
            new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));
    }

    private sealed record MappingRow(Guid Id, Guid RecordingVersionId, Guid MatchId, string Spans, string MappingDigest, DateTime CreatedAt);

    public async Task<StoredTimelineMapping?> GetTimelineMappingAsync(
        Guid matchId, Guid timelineMappingId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MappingRow>(new CommandDefinition(
            "SELECT id AS Id, recording_version_id AS RecordingVersionId, match_id AS MatchId, spans::text AS Spans, " +
            "mapping_digest AS MappingDigest, created_at AS CreatedAt " +
            "FROM socalytics.recording_timeline_mappings WHERE id = @timelineMappingId AND match_id = @matchId",
            new { timelineMappingId, matchId },
            session.Transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var spans = new List<TimelineSpan>();
        using (var document = System.Text.Json.JsonDocument.Parse(row.Spans))
        {
            foreach (var span in document.RootElement.GetProperty("spans").EnumerateArray())
            {
                spans.Add(new TimelineSpan(
                    span.GetProperty("mediaStartMilliseconds").GetInt64(),
                    span.GetProperty("mediaEndMilliseconds").GetInt64(),
                    span.GetProperty("matchStartMilliseconds").GetInt64()));
            }
        }

        return new StoredTimelineMapping(
            row.Id, row.RecordingVersionId, row.MatchId, spans, row.MappingDigest,
            new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));
    }
}
