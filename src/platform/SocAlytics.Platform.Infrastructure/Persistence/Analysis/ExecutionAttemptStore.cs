using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class ExecutionAttemptStore(IDbSession session, TimeProvider time) : IExecutionAttemptStore
{
    private const string AttemptSelect =
        """
        SELECT id AS Id, logical_job_id AS LogicalJobId, readiness_occurrence_id AS ReadinessOccurrenceId,
               attempt_number AS AttemptNumber, manager_id AS ManagerId, fencing_token AS FencingToken, state AS State,
               claimed_at AS ClaimedAt, lease_expires_at AS LeaseExpiresAt, last_heartbeat_at AS LastHeartbeatAt,
               closed_at AS ClosedAt, close_reason AS CloseReason
        FROM socalytics.analysis_execution_attempts
        """;

    private sealed record AttemptRow(
        Guid Id, Guid LogicalJobId, Guid ReadinessOccurrenceId, int AttemptNumber, Guid ManagerId, long FencingToken,
        string State, DateTime ClaimedAt, DateTime LeaseExpiresAt, DateTime LastHeartbeatAt, DateTime? ClosedAt, string? CloseReason);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static ExecutionAttemptRecord ToRecord(AttemptRow r) => new(
        r.Id, r.LogicalJobId, r.ReadinessOccurrenceId, r.AttemptNumber, r.ManagerId, r.FencingToken,
        Enum.Parse<ExecutionAttemptState>(r.State), Utc(r.ClaimedAt), Utc(r.LeaseExpiresAt), Utc(r.LastHeartbeatAt),
        r.ClosedAt is { } c ? Utc(c) : null, r.CloseReason);

    public async Task<ExecutionAttemptRecord> InsertAsync(NewExecutionAttempt attempt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<AttemptRow>(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_execution_attempts
                (id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state,
                 claimed_at, lease_expires_at, last_heartbeat_at)
            VALUES (@id, @jobId, @occurrenceId, @number, @managerId, nextval('socalytics.analysis_fencing_token_seq'), 'Active',
                    @claimedAt, @leaseExpiresAt, @claimedAt)
            RETURNING id AS Id, logical_job_id AS LogicalJobId, readiness_occurrence_id AS ReadinessOccurrenceId,
                      attempt_number AS AttemptNumber, manager_id AS ManagerId, fencing_token AS FencingToken, state AS State,
                      claimed_at AS ClaimedAt, lease_expires_at AS LeaseExpiresAt, last_heartbeat_at AS LastHeartbeatAt,
                      closed_at AS ClosedAt, close_reason AS CloseReason
            """,
            new
            {
                id = attempt.Id,
                jobId = attempt.LogicalJobId,
                occurrenceId = attempt.ReadinessOccurrenceId,
                number = attempt.AttemptNumber,
                managerId = attempt.ManagerId,
                claimedAt = attempt.ClaimedAt,
                leaseExpiresAt = attempt.LeaseExpiresAt,
            },
            transaction,
            cancellationToken: cancellationToken));
        return ToRecord(row);
    }

    public async Task InsertClaimKeyAsync(ClaimKeyRecord key, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_claim_keys
                (manager_id, idempotency_key, logical_job_id, readiness_occurrence_id, attempt_id, recorded_at)
            VALUES (@managerId, @key, @jobId, @occurrenceId, @attemptId, @now)
            """,
            new
            {
                managerId = key.ManagerId,
                key = key.IdempotencyKey,
                jobId = key.LogicalJobId,
                occurrenceId = key.ReadinessOccurrenceId,
                attemptId = key.AttemptId,
                now = time.GetUtcNow(),
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<ClaimKeyRecord?> FindClaimKeyAsync(Guid managerId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ClaimKeyRecord>(new CommandDefinition(
            """
            SELECT manager_id AS ManagerId, idempotency_key AS IdempotencyKey, logical_job_id AS LogicalJobId,
                   readiness_occurrence_id AS ReadinessOccurrenceId, attempt_id AS AttemptId
            FROM socalytics.analysis_claim_keys WHERE manager_id = @managerId AND idempotency_key = @idempotencyKey
            """,
            new { managerId, idempotencyKey }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<ExecutionAttemptRecord?> LockAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AttemptRow>(new CommandDefinition(
            AttemptSelect + " WHERE id = @attemptId FOR UPDATE", new { attemptId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<bool> TryCloseAsync(
        Guid attemptId, ExecutionAttemptState expectedState, ExecutionAttemptState newState, string? closeReason,
        CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.analysis_execution_attempts
            SET state = @newState, closed_at = @now, close_reason = @closeReason
            WHERE id = @attemptId AND state = @expectedState
            """,
            new { attemptId, expectedState = expectedState.ToString(), newState = newState.ToString(), closeReason, now = time.GetUtcNow() },
            transaction,
            cancellationToken: cancellationToken));
        return changed == 1;
    }

    public async Task<bool> TryRenewLeaseAsync(Guid attemptId, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.analysis_execution_attempts
            SET lease_expires_at = @leaseExpiresAt, last_heartbeat_at = @now
            WHERE id = @attemptId AND state = 'Active'
            """,
            new { attemptId, leaseExpiresAt, now = time.GetUtcNow() },
            transaction,
            cancellationToken: cancellationToken));
        return changed == 1;
    }

    public async Task<IReadOnlyList<ExpiredAttemptRef>> ListExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ExpiredAttemptRef>(new CommandDefinition(
            """
            SELECT a.id AS AttemptId, a.logical_job_id AS LogicalJobId, j.run_id AS RunId
            FROM socalytics.analysis_execution_attempts a
            JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id
            WHERE a.state = 'Active' AND a.lease_expires_at <= @now
            ORDER BY a.lease_expires_at
            LIMIT @batchSize
            """,
            new { now, batchSize }, session.Transaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
