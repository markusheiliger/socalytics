using Dapper;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration;

internal sealed class AgentAttemptStore(IDbSession session) : IAgentAttemptStore
{
    private const string Columns =
        """
        id AS Id, invocation_id AS InvocationId, attempt_number AS AttemptNumber, workload_client_id AS WorkloadClientId,
        fencing_token AS FencingToken, state AS State, claimed_at AS ClaimedAt, lease_expires_at AS LeaseExpiresAt,
        deadline_at AS DeadlineAt, ended_at AS EndedAt, end_reason AS EndReason, version AS Version
        """;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid InvocationId { get; set; }
        public int AttemptNumber { get; set; }
        public string WorkloadClientId { get; set; } = "";
        public long FencingToken { get; set; }
        public string State { get; set; } = "";
        public DateTime ClaimedAt { get; set; }
        public DateTime LeaseExpiresAt { get; set; }
        public DateTime DeadlineAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public string? EndReason { get; set; }
        public long Version { get; set; }
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static AgentAttemptRecord ToRecord(Row r)
    {
        _ = AgentPersistedNames.TryParseAttemptState(r.State, out var state);
        return new AgentAttemptRecord(
            r.Id, r.InvocationId, r.AttemptNumber, r.WorkloadClientId, r.FencingToken, state, Utc(r.ClaimedAt),
            Utc(r.LeaseExpiresAt), Utc(r.DeadlineAt), r.EndedAt is { } e ? Utc(e) : null, r.EndReason, r.Version);
    }

    // Callers hold the invocation lock, so the maximum cannot move between the read and the insert.
    public async Task<AgentAttemptRecord> InsertAsync(NewAgentAttempt attempt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<Row>(new CommandDefinition(
            $"""
            INSERT INTO socalytics.agent_attempts
                (id, invocation_id, attempt_number, workload_client_id, fencing_token, state, claimed_at, lease_expires_at,
                 deadline_at)
            SELECT @Id, @InvocationId, @AttemptNumber, @WorkloadClientId, COALESCE(MAX(fencing_token), 0) + 1, 'claimed',
                   @ClaimedAt, @LeaseExpiresAt, @DeadlineAt
            FROM socalytics.agent_attempts WHERE invocation_id = @InvocationId
            RETURNING {Columns}
            """,
            attempt, transaction, cancellationToken: cancellationToken));
        return ToRecord(row);
    }

    public async Task<AgentAttemptRecord?> LockAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.agent_attempts WHERE id = @attemptId FOR UPDATE",
            new { attemptId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<AgentAttemptRecord?> FindAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.agent_attempts WHERE id = @attemptId",
            new { attemptId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<bool> TryTransitionAsync(
        Guid attemptId, AgentAttemptState expectedState, AgentAttemptState newState, string? endReason,
        DateTimeOffset? endedAt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.agent_attempts
            SET state = @newState, end_reason = @endReason, ended_at = @endedAt
            WHERE id = @attemptId AND state = @expectedState
            """,
            new
            {
                attemptId,
                expectedState = AgentPersistedNames.ToPersistedName(expectedState),
                newState = AgentPersistedNames.ToPersistedName(newState),
                endReason,
                endedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
        return changed == 1;
    }

    public async Task<bool> TryRenewLeaseAsync(Guid attemptId, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.agent_attempts SET lease_expires_at = @leaseExpiresAt WHERE id = @attemptId AND state = 'active'",
            new { attemptId, leaseExpiresAt }, transaction, cancellationToken: cancellationToken));
        return changed == 1;
    }

    public async Task<IReadOnlyList<AgentExpiredAttemptRef>> ListExpiredAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AgentExpiredAttemptRef>(new CommandDefinition(
            """
            SELECT id AS AttemptId, invocation_id AS InvocationId,
                   CASE WHEN deadline_at <= @now THEN 'deadline-reached' ELSE 'lease-expired' END AS Reason
            FROM socalytics.agent_attempts
            WHERE state = 'active' AND (lease_expires_at <= @now OR deadline_at <= @now)
            ORDER BY LEAST(lease_expires_at, deadline_at), id
            LIMIT @batchSize
            """,
            new { now, batchSize }, session.Transaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<AgentActiveAttemptRef>> ListActiveForScopeCheckAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AgentActiveAttemptRef>(new CommandDefinition(
            """
            SELECT a.id AS AttemptId, a.invocation_id AS InvocationId, i.owner_member_id AS OwnerMemberId, i.team_id AS TeamId
            FROM socalytics.agent_attempts a
            JOIN socalytics.agent_invocations i ON i.id = a.invocation_id
            WHERE a.state = 'active'
            ORDER BY a.claimed_at, a.id
            """,
            transaction: session.Transaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
