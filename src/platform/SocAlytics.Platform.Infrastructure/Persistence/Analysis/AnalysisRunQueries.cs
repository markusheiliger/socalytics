using System.Text.Json;
using Dapper;
using SocAlytics.Platform.Application.Analysis.Queries;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class AnalysisRunQueries(IDbSession session) : IAnalysisRunQueries
{
    private const string SummarySelect =
        """
        SELECT r.id AS RunId, r.team_id AS TeamId, r.match_id AS MatchId, r.recording_set_version_id AS RecordingSetVersionId,
               r.workflow_id AS WorkflowId, r.workflow_version AS WorkflowVersion, r.state AS State, r.failure_reason AS FailureReason,
               r.created_at AS CreatedAt, r.running_at AS RunningAt, r.terminated_at AS TerminatedAt, r.version AS Version,
               r.cancelled_by_account_id AS CancelledByMemberId, a.account_name AS CancelledByAccountName,
               r.cancellation_reason AS CancellationReason, r.cancellation_cause AS CancellationCause,
               NOT EXISTS (
                   SELECT 1 FROM socalytics.analysis_runs o
                   WHERE o.match_id = r.match_id
                     AND (o.recording_set_finalized_at, o.recording_set_version_id, o.created_at, o.id)
                       > (r.recording_set_finalized_at, r.recording_set_version_id, r.created_at, r.id)) AS IsCurrent,
               s.superseding_run_id AS SupersedingRunId, sr.recording_set_version_id AS SupersedingVersionId,
               sr.recording_set_finalized_at AS SupersedingFinalizedAt, s.superseded_at AS SupersededAt
        FROM socalytics.analysis_runs r
        LEFT JOIN socalytics.member_account a ON a.id = r.cancelled_by_account_id
        LEFT JOIN socalytics.analysis_run_supersessions s ON s.run_id = r.id
        LEFT JOIN socalytics.analysis_runs sr ON sr.id = s.superseding_run_id
        """;

    private sealed record SummaryRow(
        Guid RunId, Guid TeamId, Guid MatchId, Guid RecordingSetVersionId, string WorkflowId, string WorkflowVersion,
        string State, string? FailureReason, DateTime CreatedAt, DateTime? RunningAt, DateTime? TerminatedAt, long Version,
        Guid? CancelledByMemberId, string? CancelledByAccountName, string? CancellationReason, string? CancellationCause,
        bool IsCurrent, Guid? SupersedingRunId, Guid? SupersedingVersionId, DateTime? SupersedingFinalizedAt, DateTime? SupersededAt);

    private sealed record NodeRow(
        Guid NodeId, string NodeKey, string CapabilityId, string CapabilityVersion, string Tier, string ExecutionScope,
        string State, string? BlockedByNodeKey, int Waiting, int Ready, int Claimed, int Completed, int Failed, int Blocked,
        int Cancelled);

    private sealed record InputRow(Guid NodeId, string Inputs);

    private sealed class CoverageCounts
    {
        public string Mode = "complete";
        public decimal? Fraction;
        public int Accepted, Failed, Blocked, Skipped, Missing;
    }

    private static DateTimeOffset Utc(DateTime v) => new(DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? v) => v is null ? null : Utc(v.Value);

    private static AnalysisRunSummaryView ToView(SummaryRow r) => new(
        r.RunId, r.TeamId, r.MatchId, r.RecordingSetVersionId, r.WorkflowId, r.WorkflowVersion, r.State, r.FailureReason,
        Utc(r.CreatedAt), Utc(r.RunningAt), Utc(r.TerminatedAt), r.Version,
        r.CancelledByMemberId is { } memberId ? new AnalysisRunCancelledByView(memberId, r.CancelledByAccountName ?? string.Empty) : null,
        r.CancellationReason,
        r.IsCurrent,
        r.SupersedingRunId is { } supersedingRunId
            ? new AnalysisRunSupersededByView(supersedingRunId, r.SupersedingVersionId!.Value, Utc(r.SupersedingFinalizedAt!.Value))
            : null,
        Utc(r.SupersededAt),
        r.CancellationCause);

    public async Task<AnalysisRunStatusView?> GetStatusAsync(Guid runId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var run = await connection.QuerySingleOrDefaultAsync<SummaryRow>(
            new CommandDefinition(SummarySelect + " WHERE r.id = @runId", new { runId }, session.Transaction, cancellationToken: cancellationToken));
        if (run is null)
        {
            return null;
        }

        var capabilities = (await connection.QuerySingleAsync<string[]>(
            new CommandDefinition(
                "SELECT requested_capabilities FROM socalytics.analysis_runs WHERE id = @runId",
                new { runId }, session.Transaction, cancellationToken: cancellationToken))).ToList();

        var nodes = (await connection.QueryAsync<NodeRow>(
            new CommandDefinition(
                """
                SELECT n.id AS NodeId, n.node_key AS NodeKey, n.capability_id AS CapabilityId, n.capability_version AS CapabilityVersion,
                       n.tier AS Tier, n.execution_scope AS ExecutionScope, n.state AS State, b.node_key AS BlockedByNodeKey,
                       count(*) FILTER (WHERE j.state = 'Waiting')::int AS Waiting,
                       count(*) FILTER (WHERE j.state = 'Ready')::int AS Ready,
                       count(*) FILTER (WHERE j.state = 'Claimed')::int AS Claimed,
                       count(*) FILTER (WHERE j.state = 'Completed')::int AS Completed,
                       count(*) FILTER (WHERE j.state = 'Failed')::int AS Failed,
                       count(*) FILTER (WHERE j.state = 'Blocked')::int AS Blocked,
                       count(*) FILTER (WHERE j.state = 'Cancelled')::int AS Cancelled
                FROM socalytics.analysis_workflow_nodes n
                LEFT JOIN socalytics.analysis_workflow_nodes b ON b.id = n.blocked_by_node_id
                LEFT JOIN socalytics.analysis_logical_jobs j ON j.node_id = n.id
                WHERE n.run_id = @runId
                GROUP BY n.id, b.node_key
                ORDER BY n.ordinal
                """,
                new { runId }, session.Transaction, cancellationToken: cancellationToken))).ToList();

        var inputRows = await connection.QueryAsync<InputRow>(
            new CommandDefinition(
                """
                SELECT j.node_id AS NodeId, i.inputs::text AS Inputs
                FROM socalytics.analysis_logical_job_inputs i
                JOIN socalytics.analysis_logical_jobs j ON j.id = i.logical_job_id
                WHERE j.run_id = @runId
                """,
                new { runId }, session.Transaction, cancellationToken: cancellationToken));
        var coverageByNode = SumInputCoverage(inputRows);

        var basis = await connection.QuerySingleAsync<string>(
            new CommandDefinition(
                """
                SELECT @runVersion::text || ':' || @nodeStates || ':' ||
                       coalesce(md5(string_agg(id::text || '=' || version::text, ',' ORDER BY id)), '')
                FROM socalytics.analysis_logical_jobs WHERE run_id = @runId
                """,
                new { runId, runVersion = run.Version, nodeStates = string.Join(',', nodes.Select(n => n.State + n.BlockedByNodeKey)) },
                session.Transaction, cancellationToken: cancellationToken));

        return new AnalysisRunStatusView(
            ToView(run),
            capabilities,
            [.. nodes.Select(n => new AnalysisNodeStatusView(
                n.NodeId, n.NodeKey, n.CapabilityId, n.CapabilityVersion, n.Tier, n.ExecutionScope, n.State, n.BlockedByNodeKey,
                new AnalysisJobCountsView(n.Waiting, n.Ready, n.Claimed, n.Completed, n.Failed, n.Blocked, n.Cancelled),
                coverageByNode.GetValueOrDefault(n.NodeId)))],
            basis);
    }

    // Partial entries count their coverage units; other entries count by snapshot status.
    private static Dictionary<Guid, IReadOnlyList<AnalysisNodeInputCoverageView>> SumInputCoverage(IEnumerable<InputRow> rows)
    {
        var perNode = new Dictionary<Guid, SortedDictionary<string, CoverageCounts>>();
        foreach (var row in rows)
        {
            if (!perNode.TryGetValue(row.NodeId, out var edges))
            {
                perNode[row.NodeId] = edges = new SortedDictionary<string, CoverageCounts>(StringComparer.Ordinal);
            }

            using var doc = JsonDocument.Parse(row.Inputs);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var key = e.GetProperty("upstreamNodeKey").GetString()!;
                if (!edges.TryGetValue(key, out var counts))
                {
                    edges[key] = counts = new CoverageCounts();
                }

                var partial = e.TryGetProperty("coverageMode", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == "partial";
                if (partial)
                {
                    counts.Mode = "partial";
                }

                if (e.TryGetProperty("minimumAcceptedFraction", out var f) && f.ValueKind == JsonValueKind.Number)
                {
                    counts.Fraction = f.GetDecimal();
                }

                if (partial && e.TryGetProperty("coverage", out var c) && c.ValueKind == JsonValueKind.Array)
                {
                    foreach (var unit in c.EnumerateArray())
                    {
                        switch (unit.GetProperty("state").GetString())
                        {
                            case "accepted": counts.Accepted++; break;
                            case "failed": counts.Failed++; break;
                            default: counts.Blocked++; break;
                        }
                    }

                    continue;
                }

                switch (e.GetProperty("status").GetString())
                {
                    case "accepted": counts.Accepted += Math.Max(1, e.GetProperty("acceptedResultIds").GetArrayLength()); break;
                    case "not-applicable": counts.Skipped++; break;
                    case "absent": counts.Missing++; break;
                    case "failed": counts.Failed++; break;
                }
            }
        }

        return perNode
            .Where(n => n.Value.Count > 0)
            .ToDictionary(
                n => n.Key,
                n => (IReadOnlyList<AnalysisNodeInputCoverageView>)[.. n.Value.Select(kv => new AnalysisNodeInputCoverageView(
                    kv.Key, kv.Value.Mode, kv.Value.Fraction, kv.Value.Accepted, kv.Value.Failed, kv.Value.Blocked, kv.Value.Skipped, kv.Value.Missing))]);
    }

    public async Task<AnalysisRunSummaryPageResult> ListForMatchAsync(
        Guid matchId, AnalysisRunPageKey? after, int pageSize, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<SummaryRow>(
            new CommandDefinition(
                SummarySelect + """

                 WHERE r.match_id = @matchId
                   AND (@hasAfter = false OR (r.created_at, r.id) < (@afterCreated, @afterId))
                 ORDER BY r.created_at DESC, r.id DESC
                 LIMIT @limit
                 """,
                new
                {
                    matchId,
                    hasAfter = after is not null,
                    afterCreated = after?.CreatedAt ?? DateTimeOffset.MinValue,
                    afterId = after?.Id ?? Guid.Empty,
                    limit = pageSize + 1,
                },
                session.Transaction,
                cancellationToken: cancellationToken))).ToList();

        return new AnalysisRunSummaryPageResult([.. rows.Take(pageSize).Select(ToView)], rows.Count > pageSize);
    }
}
