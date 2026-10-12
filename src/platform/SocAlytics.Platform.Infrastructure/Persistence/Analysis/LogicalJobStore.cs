using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class LogicalJobStore(IDbSession session, TimeProvider time) : ILogicalJobStore
{
    private const string JobSelect =
        """
        SELECT id AS Id, run_id AS RunId, node_id AS NodeId, execution_scope AS ExecutionScope,
               recording_version_id AS RecordingVersionId, timeline_mapping_digest AS TimelineMappingDigest,
               segmentation_policy_digest AS SegmentationPolicyDigest, segment_number AS SegmentNumber,
               state AS State, attempts_used AS AttemptsUsed,
               current_readiness_occurrence_id AS CurrentReadinessOccurrenceId, current_attempt_id AS CurrentAttemptId,
               failure_reason AS FailureReason, updated_at AS UpdatedAt, version AS Version
        FROM socalytics.analysis_logical_jobs
        """;

    private sealed record JobRow(
        Guid Id, Guid RunId, Guid NodeId, string ExecutionScope, Guid? RecordingVersionId, string? TimelineMappingDigest,
        string? SegmentationPolicyDigest, int? SegmentNumber, string State, int AttemptsUsed,
        Guid? CurrentReadinessOccurrenceId, Guid? CurrentAttemptId, string? FailureReason, DateTime UpdatedAt, long Version);

    private sealed record SnapshotRow(
        int MaxAttempts, int LeaseSeconds, int StaleTimeoutSeconds, int ExecutionTimeoutSeconds,
        string AnalystProfileDigest, string ImageDigest, string? ModelDigest, string Resources, string SchemaVersion);

    private static LogicalJobRecord ToRecord(JobRow r) => new(
        r.Id, r.RunId, r.NodeId,
        AnalysisPersistedNames.TryParseExecutionScope(r.ExecutionScope, out var scope)
            ? scope
            : throw new InvalidOperationException($"Unknown scope '{r.ExecutionScope}'."),
        r.RecordingVersionId, r.TimelineMappingDigest, r.SegmentationPolicyDigest, r.SegmentNumber,
        Enum.Parse<LogicalJobState>(r.State), r.AttemptsUsed, r.CurrentReadinessOccurrenceId, r.CurrentAttemptId,
        r.FailureReason, new DateTimeOffset(DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc)), r.Version);

    public async Task InsertAsync(IReadOnlyList<NewLogicalJob> jobs, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var job in jobs)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO socalytics.analysis_logical_jobs
                    (id, run_id, node_id, execution_scope, recording_version_id, timeline_mapping_digest,
                     segmentation_policy_digest, segment_number, state, updated_at)
                VALUES (@id, @runId, @nodeId, @scope, @recordingVersionId, @mappingDigest, @policyDigest, @segmentNumber, @state, @now)
                """,
                new
                {
                    id = job.Id,
                    runId = job.RunId,
                    nodeId = job.NodeId,
                    scope = AnalysisPersistedNames.ToPersistedName(job.ExecutionScope),
                    recordingVersionId = job.RecordingVersionId,
                    mappingDigest = job.TimelineMappingDigest,
                    policyDigest = job.SegmentationPolicyDigest,
                    segmentNumber = job.SegmentNumber,
                    state = job.State.ToString(),
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
            var s = job.Snapshot;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO socalytics.analysis_logical_job_snapshots
                    (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds,
                     analyst_profile_digest, image_digest, model_digest, resources, schema_version)
                VALUES (@id, @maxAttempts, @lease, @stale, @execution, @profile, @image, @model, @resources::jsonb, @schema)
                """,
                new
                {
                    id = job.Id,
                    maxAttempts = s.MaxAttempts,
                    lease = s.LeaseSeconds,
                    stale = s.StaleTimeoutSeconds,
                    execution = s.ExecutionTimeoutSeconds,
                    profile = s.AnalystProfileDigest,
                    image = s.ImageDigest,
                    model = s.ModelDigest,
                    resources = s.ResourcesJson,
                    schema = s.SchemaVersion,
                },
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<IReadOnlyList<LogicalJobRecord>> LoadByRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<JobRow>(new CommandDefinition(
            JobSelect + " WHERE run_id = @runId ORDER BY id", new { runId }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToRecord).ToList();
    }

    public async Task<LogicalJobRecord?> LockJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            JobSelect + " WHERE id = @jobId FOR UPDATE", new { jobId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<LogicalJobSnapshotRecord?> LoadSnapshotAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var r = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(
            """
            SELECT max_attempts AS MaxAttempts, lease_seconds AS LeaseSeconds, stale_timeout_seconds AS StaleTimeoutSeconds,
                   execution_timeout_seconds AS ExecutionTimeoutSeconds, analyst_profile_digest AS AnalystProfileDigest,
                   image_digest AS ImageDigest, model_digest AS ModelDigest, resources::text AS Resources,
                   schema_version AS SchemaVersion
            FROM socalytics.analysis_logical_job_snapshots WHERE logical_job_id = @jobId
            """,
            new { jobId }, session.Transaction, cancellationToken: cancellationToken));
        return r is null
            ? null
            : new LogicalJobSnapshotRecord(
                r.MaxAttempts, r.LeaseSeconds, r.StaleTimeoutSeconds, r.ExecutionTimeoutSeconds,
                r.AnalystProfileDigest, r.ImageDigest, r.ModelDigest, r.Resources, r.SchemaVersion);
    }

    public async Task<bool> TryTransitionAsync(
        Guid jobId, LogicalJobState expectedState, LogicalJobState newState, string? failureReason, int? attemptsUsed,
        CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.analysis_logical_jobs
            SET state = @newState,
                failure_reason = COALESCE(@failureReason, failure_reason),
                attempts_used = COALESCE(@attemptsUsed, attempts_used),
                updated_at = @now
            WHERE id = @jobId AND state = @expectedState
            """,
            new
            {
                jobId,
                expectedState = expectedState.ToString(),
                newState = newState.ToString(),
                failureReason,
                attemptsUsed,
                now = time.GetUtcNow(),
            },
            transaction,
            cancellationToken: cancellationToken));
        return changed == 1;
    }

    public async Task<bool> TryInsertInputsAsync(Guid jobId, string inputsJson, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_logical_job_inputs (logical_job_id, inputs, recorded_at)
            VALUES (@jobId, @inputs::jsonb, @now)
            ON CONFLICT (logical_job_id) DO NOTHING
            """,
            new { jobId, inputs = inputsJson, now = time.GetUtcNow() },
            transaction,
            cancellationToken: cancellationToken));
        return inserted == 1;
    }

    public async Task<string?> LoadInputsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT inputs::text FROM socalytics.analysis_logical_job_inputs WHERE logical_job_id = @jobId",
            new { jobId }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<int> InsertReadinessOccurrenceAsync(Guid jobId, Guid occurrenceId, string cause, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at)
            SELECT @occurrenceId, @jobId, COALESCE(MAX(sequence), 0) + 1, @cause, @now
            FROM socalytics.analysis_readiness_occurrences WHERE logical_job_id = @jobId
            RETURNING sequence
            """,
            new { jobId, occurrenceId, cause, now = time.GetUtcNow() },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task SetCurrentOccurrenceAsync(Guid jobId, Guid? occurrenceId, Guid? attemptId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.analysis_logical_jobs
            SET current_readiness_occurrence_id = @occurrenceId, current_attempt_id = @attemptId, updated_at = @now
            WHERE id = @jobId
            """,
            new { jobId, occurrenceId, attemptId, now = time.GetUtcNow() },
            transaction,
            cancellationToken: cancellationToken));
    }
}
