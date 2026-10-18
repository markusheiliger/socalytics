using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class AnalysisRunStore(IDbSession session, TimeProvider time) : IAnalysisRunStore
{
    private const string RunSelect =
        """
        SELECT id AS Id, team_id AS TeamId, match_id AS MatchId, recording_set_version_id AS RecordingSetVersionId,
               workflow_id AS WorkflowId, workflow_version AS WorkflowVersion,
               to_jsonb(requested_capabilities)::text AS RequestedCapabilities, completion_policy AS CompletionPolicy,
               state AS State, failure_reason AS FailureReason, created_at AS CreatedAt, running_at AS RunningAt,
               terminated_at AS TerminatedAt, updated_at AS UpdatedAt, version AS Version
        FROM socalytics.analysis_runs
        """;

    private sealed record RunRow(
        Guid Id, Guid TeamId, Guid MatchId, Guid RecordingSetVersionId, string WorkflowId, string WorkflowVersion,
        string RequestedCapabilities, string CompletionPolicy, string State, string? FailureReason,
        DateTime CreatedAt, DateTime? RunningAt, DateTime? TerminatedAt, DateTime UpdatedAt, long Version);

    private sealed record NodeRow(
        Guid Id, string NodeKey, string CapabilityId, string CapabilityVersion, string Tier, string ExecutionScope,
        int Ordinal, string State, Guid? BlockedByNodeId, DateTime UpdatedAt, string Snapshot);

    private sealed record EdgeRow(Guid UpstreamNodeId, Guid DependentNodeId, string Kind, string? Predicate);

    private sealed record SnapshotRow(string Snapshot, string SnapshotDigest);

    private static DateTimeOffset Utc(DateTime v) => new(DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? v) => v is null ? null : Utc(v.Value);

    private static bool IsTerminal(AnalysisRunState s) =>
        s is AnalysisRunState.PartiallyCompleted or AnalysisRunState.Completed or AnalysisRunState.Failed or AnalysisRunState.Cancelled;

    private static AnalysisRunRecord ToRecord(RunRow r) => new(
        r.Id, r.TeamId, r.MatchId, r.RecordingSetVersionId, r.WorkflowId, r.WorkflowVersion, System.Text.Json.JsonSerializer.Deserialize<string[]>(r.RequestedCapabilities)!,
        r.CompletionPolicy, Enum.Parse<AnalysisRunState>(r.State), r.FailureReason, Utc(r.CreatedAt), Utc(r.RunningAt),
        Utc(r.TerminatedAt), Utc(r.UpdatedAt), r.Version);

    public async Task<Guid> InsertAsync(NewAnalysisRun run, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_runs
                (id, team_id, match_id, recording_set_version_id, workflow_id, workflow_version, requested_capabilities,
                 completion_policy, state, failure_reason, created_at, running_at, terminated_at, updated_at)
            VALUES (@id, @teamId, @matchId, @setId, @workflowId, @workflowVersion, @capabilities,
                    @policy, @state, @failure, @now, @runningAt, @terminatedAt, @now)
            """,
            new
            {
                id,
                teamId = run.TeamId,
                matchId = run.MatchId,
                setId = run.RecordingSetVersionId,
                workflowId = run.WorkflowId,
                workflowVersion = run.WorkflowVersion,
                capabilities = run.RequestedCapabilities.ToArray(),
                policy = run.CompletionPolicy,
                state = run.State.ToString(),
                failure = run.FailureReason,
                now,
                runningAt = run.State == AnalysisRunState.Running ? now : (DateTimeOffset?)null,
                terminatedAt = IsTerminal(run.State) ? now : (DateTimeOffset?)null,
            },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest) VALUES (@id, @snapshot::jsonb, @digest)",
            new { id, snapshot = run.Snapshot.SnapshotJson, digest = run.Snapshot.SnapshotDigest },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var node in run.Nodes)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO socalytics.analysis_workflow_nodes
                    (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, blocked_by_node_id, updated_at)
                VALUES (@nodeId, @id, @key, @capabilityId, @capabilityVersion, @tier, @scope, @ordinal, @state, @blocker, @now)
                """,
                new
                {
                    nodeId = node.Id,
                    id,
                    key = node.NodeKey,
                    capabilityId = node.CapabilityId,
                    capabilityVersion = node.CapabilityVersion,
                    tier = AnalysisPersistedNames.ToPersistedName(node.Tier),
                    scope = AnalysisPersistedNames.ToPersistedName(node.ExecutionScope),
                    ordinal = node.Ordinal,
                    state = node.State.ToString(),
                    blocker = node.BlockedByNodeId,
                    now,
                },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot) VALUES (@nodeId, @id, @snapshot::jsonb)",
                new { nodeId = node.Id, id, snapshot = node.SnapshotJson },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var edge in run.Edges)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO socalytics.analysis_dependency_edges (run_id, upstream_node_id, dependent_node_id, kind, predicate)
                VALUES (@id, @upstream, @dependent, @kind, @predicate::jsonb)
                """,
                new
                {
                    id,
                    upstream = edge.UpstreamNodeId,
                    dependent = edge.DependentNodeId,
                    kind = AnalysisPersistedNames.ToPersistedName(edge.Kind),
                    predicate = edge.PredicateJson,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        return id;
    }

    public async Task<Guid?> FindRunIdAsync(Guid recordingSetVersionId, string workflowId, string workflowVersion, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM socalytics.analysis_runs WHERE recording_set_version_id = @recordingSetVersionId AND workflow_id = @workflowId AND workflow_version = @workflowVersion",
            new { recordingSetVersionId, workflowId, workflowVersion }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<AnalysisRunRecord?> LockRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            RunSelect + " WHERE id = @runId FOR UPDATE", new { runId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<bool> TryTransitionAsync(
        Guid runId, AnalysisRunState expectedState, AnalysisRunState newState, string? failureReason, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var now = time.GetUtcNow();
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.analysis_runs
            SET state = @newState,
                failure_reason = COALESCE(@failureReason, failure_reason),
                running_at = CASE WHEN @newState = 'Running' THEN COALESCE(running_at, @now) ELSE running_at END,
                terminated_at = CASE WHEN @terminal THEN COALESCE(terminated_at, @now) ELSE terminated_at END,
                updated_at = @now
            WHERE id = @runId AND state = @expectedState
            """,
            new
            {
                runId,
                expectedState = expectedState.ToString(),
                newState = newState.ToString(),
                failureReason,
                terminal = IsTerminal(newState),
                now,
            },
            transaction,
            cancellationToken: cancellationToken));
        return changed == 1;
    }

    public async Task<bool> TryTransitionNodeAsync(
        Guid nodeId, WorkflowNodeState expectedState, WorkflowNodeState newState, Guid? blockedByNodeId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.analysis_workflow_nodes
            SET state = @newState, blocked_by_node_id = COALESCE(@blockedByNodeId, blocked_by_node_id), updated_at = @now
            WHERE id = @nodeId AND state = @expectedState
            """,
            new { nodeId, expectedState = expectedState.ToString(), newState = newState.ToString(), blockedByNodeId, now = time.GetUtcNow() },
            transaction,
            cancellationToken: cancellationToken));
        return changed == 1;
    }

    public async Task<bool> TryCancelAsync(
        Guid runId, AnalysisRunState expectedState, Guid cancelledByAccountId, string? cancellationReason, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var now = time.GetUtcNow();
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE socalytics.analysis_runs
            SET state = 'Cancelled',
                terminated_at = @now,
                cancelled_by_account_id = @cancelledByAccountId,
                cancellation_reason = @cancellationReason,
                updated_at = @now
            WHERE id = @runId AND state = @expectedState
            """,
            new { runId, expectedState = expectedState.ToString(), cancelledByAccountId, cancellationReason, now },
            transaction,
            cancellationToken: cancellationToken));
        return changed == 1;
    }

    public async Task<int> CancelUnfinishedNodesAsync(Guid runId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            WITH locked AS (
                SELECT id FROM socalytics.analysis_workflow_nodes
                WHERE run_id = @runId AND state IN ('Waiting', 'Active')
                ORDER BY id FOR UPDATE)
            UPDATE socalytics.analysis_workflow_nodes n
            SET state = 'Cancelled', updated_at = @now
            FROM locked WHERE n.id = locked.id
            """,
            new { runId, now = time.GetUtcNow() },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<int> CountCancelledNodesAsync(Guid runId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM socalytics.analysis_workflow_nodes WHERE run_id = @runId AND state = 'Cancelled'",
            new { runId }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<AnalysisRunHierarchy?> LoadAsync(Guid runId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var run = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            RunSelect + " WHERE id = @runId", new { runId }, session.Transaction, cancellationToken: cancellationToken));
        if (run is null)
        {
            return null;
        }

        var snapshot = await connection.QuerySingleAsync<SnapshotRow>(new CommandDefinition(
            "SELECT snapshot::text AS Snapshot, snapshot_digest AS SnapshotDigest FROM socalytics.analysis_run_snapshots WHERE run_id = @runId",
            new { runId }, session.Transaction, cancellationToken: cancellationToken));
        var nodes = await connection.QueryAsync<NodeRow>(new CommandDefinition(
            """
            SELECT n.id AS Id, n.node_key AS NodeKey, n.capability_id AS CapabilityId, n.capability_version AS CapabilityVersion,
                   n.tier AS Tier, n.execution_scope AS ExecutionScope, n.ordinal AS Ordinal, n.state AS State,
                   n.blocked_by_node_id AS BlockedByNodeId, n.updated_at AS UpdatedAt, s.snapshot::text AS Snapshot
            FROM socalytics.analysis_workflow_nodes n
            JOIN socalytics.analysis_node_snapshots s ON s.node_id = n.id
            WHERE n.run_id = @runId ORDER BY n.ordinal
            """,
            new { runId }, session.Transaction, cancellationToken: cancellationToken));
        var edges = await connection.QueryAsync<EdgeRow>(new CommandDefinition(
            """
            SELECT upstream_node_id AS UpstreamNodeId, dependent_node_id AS DependentNodeId, kind AS Kind, predicate::text AS Predicate
            FROM socalytics.analysis_dependency_edges WHERE run_id = @runId ORDER BY upstream_node_id, dependent_node_id
            """,
            new { runId }, session.Transaction, cancellationToken: cancellationToken));

        return new AnalysisRunHierarchy(
            ToRecord(run),
            new AnalysisRunSnapshotRecord(snapshot.Snapshot, snapshot.SnapshotDigest),
            nodes.Select(n => new WorkflowNodeRecord(
                n.Id, n.NodeKey, n.CapabilityId, n.CapabilityVersion,
                ParseTier(n.Tier), ParseScope(n.ExecutionScope), n.Ordinal,
                Enum.Parse<WorkflowNodeState>(n.State), n.BlockedByNodeId, Utc(n.UpdatedAt), n.Snapshot)).ToList(),
            edges.Select(e => new DependencyEdgeRecord(
                e.UpstreamNodeId, e.DependentNodeId, ParseKind(e.Kind), e.Predicate)).ToList());
    }

    private static CapabilityTier ParseTier(string v) =>
        AnalysisPersistedNames.TryParseCapabilityTier(v, out var t) ? t : throw new InvalidOperationException($"Unknown tier '{v}'.");

    private static ExecutionScope ParseScope(string v) =>
        AnalysisPersistedNames.TryParseExecutionScope(v, out var s) ? s : throw new InvalidOperationException($"Unknown scope '{v}'.");

    private static DependencyKind ParseKind(string v) =>
        AnalysisPersistedNames.TryParseDependencyKind(v, out var k) ? k : throw new InvalidOperationException($"Unknown kind '{v}'.");
}
