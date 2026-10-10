using Dapper;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.Recordings;

internal sealed class RecordingStore(IDbSession session) : IRecordingStore
{
    private sealed record SessionRow(
        Guid Id,
        Guid MatchId,
        Guid TeamId,
        string State,
        string DisplayName,
        string? Description,
        string ContentType,
        long TotalSizeBytes,
        long PartSizeBytes,
        byte[] PartDigests,
        string ObjectKey,
        string MultipartUploadId,
        Guid CreatedBy,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        DateTime? CompletedAt,
        DateTime? ExpiredAt,
        DateTime? StorageReleasedAt,
        long Version,
        DateTime DatabaseNow);

    public async Task InsertUploadSessionAsync(UploadSession upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var digests = new byte[upload.Declaration.PartDigests.Count * Sha256Digest.ByteLength];
        for (var i = 0; i < upload.Declaration.PartDigests.Count; i++)
        {
            upload.Declaration.PartDigests[i].Bytes.CopyTo(digests.AsSpan(i * Sha256Digest.ByteLength));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.recording_upload_sessions " +
            "(id, match_id, team_id, state, display_name, description, content_type, total_size_bytes, part_size_bytes, " +
            "part_count, part_digests, content_digest, object_key, multipart_upload_id, created_by, created_at, expires_at) " +
            "VALUES (@id, @matchId, @teamId, 'pending', @displayName, @description, @contentType, @total, @partSize, " +
            "@partCount, @digests, @contentDigest, @objectKey, @uploadId, @createdBy, @createdAt, @expiresAt)",
            new
            {
                id = upload.Id,
                matchId = upload.MatchId,
                teamId = upload.TeamId,
                displayName = upload.Descriptor.DisplayName,
                description = upload.Descriptor.Description,
                contentType = upload.Descriptor.ContentType,
                total = upload.Declaration.TotalSizeBytes,
                partSize = upload.Declaration.PartSizeBytes,
                partCount = upload.Declaration.PartCount,
                digests,
                contentDigest = upload.Declaration.ExpectedDigest.ToString(),
                objectKey = upload.ObjectKey,
                uploadId = upload.MultipartUploadId,
                createdBy = upload.CreatedBy,
                createdAt = upload.CreatedAt,
                expiresAt = upload.ExpiresAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<StoredUploadSession?> FindUploadSessionAsync(Guid matchId, Guid uploadSessionId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            "SELECT id AS Id, match_id AS MatchId, team_id AS TeamId, state AS State, display_name AS DisplayName, " +
            "description AS Description, content_type AS ContentType, total_size_bytes AS TotalSizeBytes, " +
            "part_size_bytes AS PartSizeBytes, part_digests AS PartDigests, object_key AS ObjectKey, " +
            "multipart_upload_id AS MultipartUploadId, created_by AS CreatedBy, created_at AS CreatedAt, " +
            "expires_at AS ExpiresAt, completed_at AS CompletedAt, expired_at AS ExpiredAt, " +
            "storage_released_at AS StorageReleasedAt, version AS Version, now() AS DatabaseNow " +
            "FROM socalytics.recording_upload_sessions WHERE id = @uploadSessionId AND match_id = @matchId",
            new { uploadSessionId, matchId },
            session.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : new StoredUploadSession(Map(row), Utc(row.DatabaseNow));
    }

    public async Task<bool> TryCompleteUploadSessionAsync(Guid uploadSessionId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.recording_upload_sessions SET state = 'completed', completed_at = now() " +
            "WHERE id = @uploadSessionId AND state = 'pending' AND expires_at > now()",
            new { uploadSessionId },
            transaction,
            cancellationToken: cancellationToken));
        return rows == 1;
    }

    public async Task InsertCompletedUploadAsync(
        RecordingVersion version, StoredTimelineMapping mapping, string canonicalSpansJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(mapping);
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.recording_versions " +
            "(id, match_id, team_id, upload_session_id, object_key, total_size_bytes, part_size_bytes, part_count, content_digest, " +
            "display_name, description, content_type, storage_etag, created_by, created_at) " +
            "VALUES (@id, @matchId, @teamId, @uploadSessionId, @objectKey, @total, @partSize, @partCount, @digest, " +
            "@displayName, @description, @contentType, @etag, @createdBy, @createdAt)",
            new
            {
                id = version.Id,
                matchId = version.MatchId,
                teamId = version.TeamId,
                uploadSessionId = version.UploadSessionId,
                objectKey = version.ObjectKey,
                total = version.TotalSizeBytes,
                partSize = version.PartSizeBytes,
                partCount = version.PartCount,
                digest = version.ContentDigest.ToString(),
                displayName = version.Descriptor.DisplayName,
                description = version.Descriptor.Description,
                contentType = version.Descriptor.ContentType,
                etag = version.StorageETag,
                createdBy = version.CreatedBy,
                createdAt = version.CreatedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.recording_timeline_mappings " +
            "(id, recording_version_id, match_id, spans, mapping_digest, created_by, created_at) " +
            "VALUES (@id, @versionId, @matchId, CAST(@spans AS jsonb), @digest, @createdBy, @createdAt)",
            new
            {
                id = mapping.Id,
                versionId = version.Id,
                matchId = version.MatchId,
                spans = canonicalSpansJson,
                digest = mapping.Digest,
                createdBy = version.CreatedBy,
                createdAt = mapping.CreatedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed record CompletedRow(
        Guid VersionId, Guid MatchId, Guid TeamId, Guid UploadSessionId, string ObjectKey, long TotalSizeBytes,
        long PartSizeBytes, int PartCount, string ContentDigest, string DisplayName, string? Description, string ContentType,
        string? StorageETag, Guid CreatedBy, DateTime VersionCreatedAt, Guid MappingId, string Spans, string MappingDigest,
        DateTime MappingCreatedAt);

    public async Task<StoredCompletedUpload?> FindCompletedUploadAsync(
        Guid matchId, Guid recordingVersionId, Guid timelineMappingId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CompletedRow>(new CommandDefinition(
            "SELECT v.id AS VersionId, v.match_id AS MatchId, v.team_id AS TeamId, v.upload_session_id AS UploadSessionId, " +
            "v.object_key AS ObjectKey, v.total_size_bytes AS TotalSizeBytes, v.part_size_bytes AS PartSizeBytes, " +
            "v.part_count AS PartCount, v.content_digest AS ContentDigest, v.display_name AS DisplayName, " +
            "v.description AS Description, v.content_type AS ContentType, v.storage_etag AS StorageETag, " +
            "v.created_by AS CreatedBy, v.created_at AS VersionCreatedAt, m.id AS MappingId, m.spans::text AS Spans, " +
            "m.mapping_digest AS MappingDigest, m.created_at AS MappingCreatedAt " +
            "FROM socalytics.recording_versions v JOIN socalytics.recording_timeline_mappings m " +
            "ON m.recording_version_id = v.id AND m.match_id = v.match_id " +
            "WHERE v.id = @recordingVersionId AND v.match_id = @matchId AND m.id = @timelineMappingId",
            new { recordingVersionId, matchId, timelineMappingId },
            session.Transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        if (!RecordingDescriptor.TryCreate(row.DisplayName, row.Description, row.ContentType, out var descriptor, out _))
        {
            throw new InvalidOperationException($"Recording version {row.VersionId} holds an invalid stored descriptor.");
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

        var version = new RecordingVersion(
            row.VersionId, row.MatchId, row.TeamId, row.UploadSessionId, row.ObjectKey, row.TotalSizeBytes, row.PartSizeBytes,
            row.PartCount, CompositeContentDigest.Parse(row.ContentDigest), descriptor!, row.StorageETag, row.CreatedBy, Utc(row.VersionCreatedAt));
        return new StoredCompletedUpload(
            version, new StoredTimelineMapping(row.MappingId, row.VersionId, row.MatchId, spans, row.MappingDigest, Utc(row.MappingCreatedAt)));
    }

    private static UploadSession Map(SessionRow row)
    {
        var digests = new List<string>(row.PartDigests.Length / Sha256Digest.ByteLength);
        for (var offset = 0; offset < row.PartDigests.Length; offset += Sha256Digest.ByteLength)
        {
            digests.Add(Sha256Digest.FromBytes(row.PartDigests.AsSpan(offset, Sha256Digest.ByteLength)).ToString());
        }

        // Stored declarations were validated at start; rehydration must not depend on current option bounds.
        var bounds = new MultipartBounds(long.MaxValue, 1, long.MaxValue, int.MaxValue);
        if (!RecordingDescriptor.TryCreate(row.DisplayName, row.Description, row.ContentType, out var descriptor, out _)
            || !MultipartDeclaration.TryCreate(row.TotalSizeBytes, row.PartSizeBytes, digests, bounds, out var declaration, out _))
        {
            throw new InvalidOperationException($"Upload session {row.Id} holds an invalid stored declaration.");
        }

        var state = row.State switch
        {
            "pending" => UploadSessionState.Pending,
            "completed" => UploadSessionState.Completed,
            "expired" => UploadSessionState.Expired,
            _ => throw new InvalidOperationException($"Unknown upload session state '{row.State}'."),
        };
        return UploadSession.Rehydrate(
            row.Id, row.MatchId, row.TeamId, descriptor!, declaration!, row.ObjectKey, row.MultipartUploadId, row.CreatedBy,
            Utc(row.CreatedAt), Utc(row.ExpiresAt), state, Utc(row.CompletedAt), Utc(row.ExpiredAt), Utc(row.StorageReleasedAt), row.Version);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is null ? null : Utc(value.Value);
}
