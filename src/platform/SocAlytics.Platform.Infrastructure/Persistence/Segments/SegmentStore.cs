using Dapper;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Infrastructure.Persistence.Segments;

internal sealed class SegmentStore(IDbSession session, TimeProvider time) : ISegmentStore
{
    private const string Columns =
        """
        ms.id AS Id, ms.recording_version_id AS RecordingVersionId, ms.timeline_mapping_digest AS TimelineMappingDigest,
        ms.segmentation_policy_digest AS SegmentationPolicyDigest, ms.segment_number AS SegmentNumber,
        ms.policy_version AS PolicyVersion, ms.duration_seconds AS DurationSeconds, ms.state AS State,
        ms.demanded AS Demanded, ms.candidate_id AS CandidateId, ms.lease_owner AS LeaseOwner,
        ms.lease_expires_at AS LeaseExpiresAt, ms.attempts_used AS AttemptsUsed, ms.next_attempt_at AS NextAttemptAt,
        ms.failure_reason AS FailureReason, ms.media_object_key AS MediaObjectKey,
        ms.manifest_object_key AS ManifestObjectKey, ms.media_digest AS MediaDigest,
        ms.media_size_bytes AS MediaSizeBytes, ms.manifest_checksum AS ManifestChecksum, ms.created_at AS CreatedAt,
        ms.updated_at AS UpdatedAt, ms.available_at AS AvailableAt, ms.purged_at AS PurgedAt, ms.version AS Version
        """;

    private const string IdentityPredicate =
        """
        ms.recording_version_id = @recordingVersionId AND ms.timeline_mapping_digest = @mappingDigest
        AND ms.segmentation_policy_digest = @policyDigest AND ms.segment_number = @segmentNumber
        """;

    private const string ClearedAssociation =
        """
        media_object_key = NULL, manifest_object_key = NULL, media_digest = NULL, media_size_bytes = NULL,
        manifest_checksum = NULL
        """;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid RecordingVersionId { get; set; }
        public string TimelineMappingDigest { get; set; } = "";
        public string SegmentationPolicyDigest { get; set; } = "";
        public int SegmentNumber { get; set; }
        public int PolicyVersion { get; set; }
        public int DurationSeconds { get; set; }
        public string State { get; set; } = "";
        public bool Demanded { get; set; }
        public Guid? CandidateId { get; set; }
        public string? LeaseOwner { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public int AttemptsUsed { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public string? FailureReason { get; set; }
        public string? MediaObjectKey { get; set; }
        public string? ManifestObjectKey { get; set; }
        public string? MediaDigest { get; set; }
        public long? MediaSizeBytes { get; set; }
        public string? ManifestChecksum { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? AvailableAt { get; set; }
        public DateTime? PurgedAt { get; set; }
        public long Version { get; set; }
        public Guid? ReclaimedCandidateId { get; set; }
        public bool InUse { get; set; }
    }

    private sealed class JobRow
    {
        public Guid JobId { get; set; }
        public Guid RunId { get; set; }
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private static MaterializedSegmentRecord ToRecord(Row r)
    {
        if (!MaterializedSegmentStates.TryParse(r.State, out var state))
        {
            throw new InvalidOperationException($"Unknown segment state '{r.State}'.");
        }

        var identity = new MaterializedSegmentIdentity(
            r.RecordingVersionId,
            Sha256Digest.Parse(r.TimelineMappingDigest),
            Sha256Digest.Parse(r.SegmentationPolicyDigest),
            r.SegmentNumber);
        var association = r.MediaObjectKey is null
            ? null
            : new SegmentAssociation(
                r.MediaObjectKey, r.ManifestObjectKey!, r.MediaDigest!, r.MediaSizeBytes!.Value, r.ManifestChecksum!);

        return new MaterializedSegmentRecord(
            r.Id, identity, r.PolicyVersion, r.DurationSeconds, state, r.Demanded, r.CandidateId, r.LeaseOwner,
            Utc(r.LeaseExpiresAt), r.AttemptsUsed, Utc(r.NextAttemptAt), r.FailureReason, association,
            Utc(r.CreatedAt), Utc(r.UpdatedAt), Utc(r.AvailableAt), Utc(r.PurgedAt), r.Version);
    }

    private static DynamicParameters IdentityArgs(MaterializedSegmentIdentity identity)
    {
        var args = new DynamicParameters();
        args.Add("recordingVersionId", identity.RecordingVersionId);
        args.Add("mappingDigest", identity.TimelineMappingDigest.ToString());
        args.Add("policyDigest", identity.SegmentationPolicyDigest.ToString());
        args.Add("segmentNumber", identity.SegmentNumber);
        return args;
    }

    private async Task<int> ExecuteAsync(string sql, object args, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(sql, args, transaction, cancellationToken: cancellationToken));
    }

    private static void RequireKnownReason(string failureReason)
    {
        if (!SegmentFailureReasons.IsKnown(failureReason))
        {
            throw new ArgumentException("The failure reason must be a sanitized segment failure reason.", nameof(failureReason));
        }
    }

    public async Task<MaterializedSegmentRecord> DemandAsync(
        MaterializedSegmentIdentity identity,
        int policyVersion,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var args = IdentityArgs(identity);
        args.Add("id", Guid.CreateVersion7());
        args.Add("policyVersion", policyVersion);
        args.Add("durationSeconds", durationSeconds);
        args.Add("now", time.GetUtcNow());

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.materialized_segments
                (id, recording_version_id, timeline_mapping_digest, segmentation_policy_digest, segment_number,
                 policy_version, duration_seconds, state, demanded, created_at, updated_at)
            VALUES (@id, @recordingVersionId, @mappingDigest, @policyDigest, @segmentNumber,
                    @policyVersion, @durationSeconds, 'absent', true, @now, @now)
            ON CONFLICT ON CONSTRAINT uq_materialized_segments_identity DO NOTHING
            """,
            args, transaction, cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE socalytics.materialized_segments ms
            SET state = 'absent', demanded = true, attempts_used = 0, next_attempt_at = NULL, failure_reason = NULL,
                updated_at = @now
            WHERE {IdentityPredicate} AND ms.state = 'failed'
            """,
            args, transaction, cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE socalytics.materialized_segments ms
            SET demanded = true, updated_at = @now
            WHERE {IdentityPredicate} AND ms.state = 'absent' AND NOT ms.demanded
            """,
            args, transaction, cancellationToken: cancellationToken));

        var row = await connection.QuerySingleAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.materialized_segments ms WHERE {IdentityPredicate}",
            args, transaction, cancellationToken: cancellationToken));
        return ToRecord(row);
    }

    public async Task<IReadOnlyList<ClaimedSegment>> ClaimDueAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, 0);
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var now = time.GetUtcNow();

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
            WITH due AS (
                SELECT id, candidate_id
                FROM socalytics.materialized_segments
                WHERE (state = 'absent' AND demanded AND (next_attempt_at IS NULL OR next_attempt_at <= @now))
                   OR (state = 'materializing' AND lease_expires_at <= @now)
                ORDER BY created_at, id
                LIMIT @limit
                FOR UPDATE SKIP LOCKED)
            UPDATE socalytics.materialized_segments ms
            SET state = 'materializing', candidate_id = gen_random_uuid(), lease_owner = @leaseOwner,
                lease_expires_at = @leaseExpiresAt, attempts_used = ms.attempts_used + 1, next_attempt_at = NULL,
                updated_at = @now
            FROM due
            WHERE ms.id = due.id
            RETURNING {Columns}, due.candidate_id AS ReclaimedCandidateId
            """,
            new { now, limit, leaseOwner, leaseExpiresAt = now + leaseDuration },
            transaction, cancellationToken: cancellationToken));

        return rows
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new ClaimedSegment(ToRecord(r), r.ReclaimedCandidateId))
            .ToList();
    }

    public async Task<bool> TryPublishAsync(
        Guid segmentId,
        Guid candidateId,
        SegmentAssociation association,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            """
            UPDATE socalytics.materialized_segments
            SET state = 'available', candidate_id = NULL, lease_owner = NULL, lease_expires_at = NULL,
                next_attempt_at = NULL, failure_reason = NULL, media_object_key = @mediaObjectKey,
                manifest_object_key = @manifestObjectKey, media_digest = @mediaDigest, media_size_bytes = @mediaSizeBytes,
                manifest_checksum = @manifestChecksum, available_at = @now, updated_at = @now
            WHERE id = @segmentId AND state = 'materializing' AND candidate_id = @candidateId
            """,
            new
            {
                segmentId,
                candidateId,
                mediaObjectKey = association.MediaObjectKey,
                manifestObjectKey = association.ManifestObjectKey,
                mediaDigest = association.MediaDigest,
                mediaSizeBytes = association.MediaSizeBytes,
                manifestChecksum = association.ManifestChecksum,
                now = time.GetUtcNow(),
            },
            cancellationToken) == 1;

    public async Task<bool> TryRecordTransientFailureAsync(
        Guid segmentId,
        Guid candidateId,
        DateTimeOffset retryAt,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            """
            UPDATE socalytics.materialized_segments
            SET state = 'absent', demanded = true, candidate_id = NULL, lease_owner = NULL, lease_expires_at = NULL,
                next_attempt_at = @retryAt, updated_at = @now
            WHERE id = @segmentId AND state = 'materializing' AND candidate_id = @candidateId
            """,
            new { segmentId, candidateId, retryAt = retryAt.ToUniversalTime(), now = time.GetUtcNow() },
            cancellationToken) == 1;

    public async Task<bool> TryFailAsync(
        Guid segmentId,
        Guid candidateId,
        string failureReason,
        CancellationToken cancellationToken)
    {
        RequireKnownReason(failureReason);
        return await ExecuteAsync(
            """
            UPDATE socalytics.materialized_segments
            SET state = 'failed', candidate_id = NULL, lease_owner = NULL, lease_expires_at = NULL,
                next_attempt_at = NULL, failure_reason = @failureReason, updated_at = @now
            WHERE id = @segmentId AND state = 'materializing' AND candidate_id = @candidateId
            """,
            new { segmentId, candidateId, failureReason, now = time.GetUtcNow() },
            cancellationToken) == 1;
    }

    public async Task<SegmentAssociation?> TryInvalidateAvailableAsync(
        Guid segmentId,
        string expectedMediaDigest,
        string failureReason,
        CancellationToken cancellationToken)
    {
        RequireKnownReason(failureReason);
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"""
            WITH old AS (
                SELECT id, media_object_key, manifest_object_key, media_digest, media_size_bytes, manifest_checksum
                FROM socalytics.materialized_segments
                WHERE id = @segmentId AND state = 'available' AND media_digest = @expectedMediaDigest
                FOR UPDATE)
            UPDATE socalytics.materialized_segments ms
            SET state = 'failed', failure_reason = @failureReason, {ClearedAssociation}, updated_at = @now
            FROM old
            WHERE ms.id = old.id
            RETURNING old.media_object_key AS MediaObjectKey, old.manifest_object_key AS ManifestObjectKey,
                      old.media_digest AS MediaDigest, old.media_size_bytes AS MediaSizeBytes,
                      old.manifest_checksum AS ManifestChecksum
            """,
            new { segmentId, expectedMediaDigest, failureReason, now = time.GetUtcNow() },
            transaction, cancellationToken: cancellationToken));

        return row is null
            ? null
            : new SegmentAssociation(
                row.MediaObjectKey!, row.ManifestObjectKey!, row.MediaDigest!, row.MediaSizeBytes!.Value, row.ManifestChecksum!);
    }

    public async Task<MaterializedSegmentRecord?> FindAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.materialized_segments ms WHERE ms.id = @segmentId",
            new { segmentId }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<MaterializedSegmentRecord?> FindAsync(
        MaterializedSegmentIdentity identity,
        CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.materialized_segments ms WHERE {IdentityPredicate}",
            IdentityArgs(identity), session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<IReadOnlyList<MaterializedSegmentRecord>> ListByRecordingVersionsAsync(
        IReadOnlyCollection<Guid> recordingVersionIds,
        CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT {Columns} FROM socalytics.materialized_segments ms
            WHERE ms.recording_version_id = ANY(@ids)
            ORDER BY ms.recording_version_id, ms.segment_number, ms.id
            """,
            new { ids = recordingVersionIds.ToArray() }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<WaitingSegmentJob>> ListWaitingJobsAsync(
        MaterializedSegmentIdentity identity,
        CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<JobRow>(new CommandDefinition(
            """
            SELECT j.id AS JobId, j.run_id AS RunId
            FROM socalytics.analysis_logical_jobs j
            JOIN socalytics.analysis_runs r ON r.id = j.run_id
            WHERE j.execution_scope = 'segment' AND j.state = 'Waiting' AND r.state IN ('Requested', 'Running')
              AND j.recording_version_id = @recordingVersionId AND j.timeline_mapping_digest = @mappingDigest
              AND j.segmentation_policy_digest = @policyDigest AND j.segment_number = @segmentNumber
            ORDER BY j.id
            """,
            IdentityArgs(identity), session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(r => new WaitingSegmentJob(r.JobId, r.RunId)).ToList();
    }

    public async Task<IReadOnlyList<SegmentPurgeCandidate>> LockForPurgeAsync(
        IReadOnlyCollection<Guid> recordingVersionIds,
        CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT {Columns},
                   (ms.state = 'materializing' OR EXISTS (
                       SELECT 1
                       FROM socalytics.analysis_logical_jobs j
                       JOIN socalytics.analysis_runs r ON r.id = j.run_id
                       WHERE j.execution_scope = 'segment' AND j.state IN ('Waiting', 'Ready', 'Claimed')
                         AND r.state IN ('Requested', 'Running')
                         AND j.recording_version_id = ms.recording_version_id
                         AND j.timeline_mapping_digest = ms.timeline_mapping_digest
                         AND j.segmentation_policy_digest = ms.segmentation_policy_digest
                         AND j.segment_number = ms.segment_number)) AS InUse
            FROM socalytics.materialized_segments ms
            WHERE ms.recording_version_id = ANY(@ids)
            ORDER BY ms.id
            FOR UPDATE OF ms
            """,
            new { ids = recordingVersionIds.ToArray() }, transaction, cancellationToken: cancellationToken));
        return rows.Select(r => new SegmentPurgeCandidate(ToRecord(r), r.InUse)).ToList();
    }

    public async Task<bool> TryPurgeAsync(Guid segmentId, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"""
            UPDATE socalytics.materialized_segments
            SET state = 'absent', demanded = false, attempts_used = 0, next_attempt_at = NULL, failure_reason = NULL,
                {ClearedAssociation}, purged_at = @now, updated_at = @now
            WHERE id = @segmentId AND state IN ('available', 'failed')
            """,
            new { segmentId, now = time.GetUtcNow() },
            cancellationToken) == 1;
}
