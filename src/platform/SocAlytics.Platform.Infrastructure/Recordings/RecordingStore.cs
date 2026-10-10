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
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? CompletedAt,
        DateTimeOffset? ExpiredAt,
        DateTimeOffset? StorageReleasedAt,
        long Version,
        DateTimeOffset DatabaseNow);

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
        return row is null ? null : new StoredUploadSession(Map(row), row.DatabaseNow);
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
            row.CreatedAt, row.ExpiresAt, state, row.CompletedAt, row.ExpiredAt, row.StorageReleasedAt, row.Version);
    }
}
