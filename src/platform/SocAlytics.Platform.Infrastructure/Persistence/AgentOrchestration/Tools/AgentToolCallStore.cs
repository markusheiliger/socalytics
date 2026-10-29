using Dapper;
using SocAlytics.Platform.Application.AgentOrchestration.Tools;

namespace SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration.Tools;

internal sealed class AgentToolCallStore(IDbSession session) : IAgentToolCallStore
{
    public async Task<bool> TryReserveAsync(NewAgentToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var connection = await session.GetConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.agent_tool_calls " +
            "(attempt_id, call_id, invocation_id, tool_id, arguments_digest, is_clip_resolution, reserved_at) " +
            "VALUES (@AttemptId, @CallId, @InvocationId, @ToolId, @ArgumentsDigest, @IsClipResolution, @ReservedAt) " +
            "ON CONFLICT (attempt_id, call_id) DO NOTHING",
            call, session.Transaction, cancellationToken: cancellationToken));
        return inserted == 1;
    }

    public async Task<AgentToolCallCounts> CountAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<CountRow>(new CommandDefinition(
            "SELECT count(*)::int AS Calls, (count(*) FILTER (WHERE is_clip_resolution))::int AS ClipResolutions " +
            "FROM socalytics.agent_tool_calls WHERE attempt_id = @attemptId",
            new { attemptId }, session.Transaction, cancellationToken: cancellationToken));
        return new AgentToolCallCounts(row.Calls, row.ClipResolutions);
    }

    public async Task<IReadOnlyList<AgentToolCallRecord>> ListWithoutLineageAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<CallRow>(new CommandDefinition(
            "SELECT c.attempt_id AS AttemptId, c.call_id AS CallId, c.invocation_id AS InvocationId, " +
            "c.tool_id AS ToolId, c.arguments_digest AS ArgumentsDigest, c.is_clip_resolution AS IsClipResolution, " +
            "c.reserved_at AS ReservedAt " +
            "FROM socalytics.agent_tool_calls c " +
            "JOIN socalytics.agent_attempts a ON a.id = c.attempt_id " +
            "WHERE (a.state NOT IN ('claimed', 'active') OR a.deadline_at <= @now) " +
            "AND NOT EXISTS (SELECT 1 FROM socalytics.agent_lineage_entries l " +
            "WHERE l.attempt_id = c.attempt_id AND l.kind = 'tool-call' AND l.payload ->> 'callId' = c.call_id) " +
            "ORDER BY c.reserved_at, c.attempt_id, c.call_id LIMIT @batchSize",
            new { now, batchSize }, session.Transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(r => r.ToRecord())];
    }

    private sealed record CountRow(int Calls, int ClipResolutions);

    private sealed record CallRow(
        Guid AttemptId, string CallId, Guid InvocationId, string ToolId, string ArgumentsDigest,
        bool IsClipResolution, DateTime ReservedAt)
    {
        public AgentToolCallRecord ToRecord() => new(
            AttemptId, CallId, InvocationId, ToolId, ArgumentsDigest, IsClipResolution,
            new DateTimeOffset(DateTime.SpecifyKind(ReservedAt, DateTimeKind.Utc)));
    }
}
