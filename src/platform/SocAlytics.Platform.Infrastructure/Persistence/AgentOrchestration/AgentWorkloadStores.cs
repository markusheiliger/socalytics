using Dapper;
using SocAlytics.Platform.Application.AgentOrchestration.Workloads;

namespace SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration;

internal sealed class AgentWorkloadOperationStore(IDbSession session) : IAgentWorkloadOperationStore
{
    public async Task<AgentWorkloadOperationRecord?> FindAsync(
        string workloadClientId, string operationId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition(
            "SELECT workload_client_id AS WorkloadClientId, operation_id AS OperationId, kind AS Kind, " +
            "request_hash AS RequestHash, invocation_id AS InvocationId, attempt_id AS AttemptId, " +
            "outcome::text AS Outcome, recorded_at AS RecordedAt " +
            "FROM socalytics.agent_workload_operations " +
            "WHERE workload_client_id = @workloadClientId AND operation_id = @operationId",
            new { workloadClientId, operationId }, session.Transaction, cancellationToken: cancellationToken));
        return row?.ToRecord();
    }

    public async Task<bool> InsertAsync(AgentWorkloadOperationRecord record, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.agent_workload_operations " +
            "(workload_client_id, operation_id, kind, request_hash, invocation_id, attempt_id, outcome, recorded_at) " +
            "VALUES (@WorkloadClientId, @OperationId, @Kind, @RequestHash, @InvocationId, @AttemptId, @Outcome::jsonb, @RecordedAt) " +
            "ON CONFLICT (workload_client_id, operation_id) DO NOTHING",
            record, session.Transaction, cancellationToken: cancellationToken));
        return inserted == 1;
    }

    private sealed record OperationRow(
        string WorkloadClientId, string OperationId, string Kind, string RequestHash,
        Guid InvocationId, Guid AttemptId, string Outcome, DateTime RecordedAt)
    {
        public AgentWorkloadOperationRecord ToRecord() => new(
            WorkloadClientId, OperationId, Kind, RequestHash, InvocationId, AttemptId, Outcome,
            new DateTimeOffset(DateTime.SpecifyKind(RecordedAt, DateTimeKind.Utc)));
    }
}

internal sealed class AgentWorkloadTokenStore(IDbSession session) : IAgentWorkloadTokenStore
{
    public async Task InsertAsync(AgentWorkloadAccessToken token, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.agent_workload_access_tokens " +
            "(token_hash, workload_client_id, key_thumbprint, issued_at, expires_at) " +
            "VALUES (@TokenHash, @WorkloadClientId, @KeyThumbprint, @IssuedAt, @ExpiresAt)",
            token, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<AgentWorkloadAccessToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TokenRow>(new CommandDefinition(
            "SELECT token_hash AS TokenHash, workload_client_id AS WorkloadClientId, key_thumbprint AS KeyThumbprint, " +
            "issued_at AS IssuedAt, expires_at AS ExpiresAt " +
            "FROM socalytics.agent_workload_access_tokens WHERE token_hash = @tokenHash",
            new { tokenHash }, session.Transaction, cancellationToken: cancellationToken));
        return row?.ToToken();
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM socalytics.agent_workload_access_tokens WHERE expires_at <= @now",
            new { now }, session.Transaction, cancellationToken: cancellationToken));
    }

    private sealed record TokenRow(
        string TokenHash, string WorkloadClientId, string KeyThumbprint, DateTime IssuedAt, DateTime ExpiresAt)
    {
        public AgentWorkloadAccessToken ToToken() => new(
            TokenHash, WorkloadClientId, KeyThumbprint, Utc(IssuedAt), Utc(ExpiresAt));

        private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
