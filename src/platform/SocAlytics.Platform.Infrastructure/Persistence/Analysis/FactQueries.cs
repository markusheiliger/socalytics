using System.Text.Json;
using Dapper;
using SocAlytics.Platform.Application.Analysis.Facts;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class FactQueries(IDbSession session) : IFactQueries
{
    private const string NodeSelect =
        """
        SELECT n.id AS NodeId, n.node_key AS NodeKey, n.capability_id AS CapabilityId, n.capability_version AS CapabilityVersion,
               s.snapshot ->> 'schemaVersion' AS SchemaVersion,
               CASE n.execution_scope WHEN 'segment' THEN 'foundational-facts' ELSE 'soccer-concepts' END AS OutputType,
               n.execution_scope AS ExecutionScope,
               s.snapshot ->> 'analystProfileId' AS AnalystProfileId,
               s.snapshot ->> 'analystProfileDigest' AS AnalystProfileDigest,
               s.snapshot ->> 'imageDigest' AS ImageDigest,
               s.snapshot ->> 'modelDigest' AS ModelDigest,
               EXISTS (SELECT 1 FROM socalytics.analysis_logical_jobs j WHERE j.node_id = n.id) AS Required
        FROM socalytics.analysis_workflow_nodes n
        JOIN socalytics.analysis_node_snapshots s ON s.node_id = n.id
        """;

    private sealed record RunRow(
        Guid RunId, Guid MatchId, Guid TeamId, string State, string WorkflowId, string WorkflowVersion,
        long Version, Guid RecordingSetVersionId, string Snapshot);

    private sealed record NodeRow(
        Guid NodeId, string NodeKey, string CapabilityId, string CapabilityVersion, string SchemaVersion, string OutputType,
        string ExecutionScope, string AnalystProfileId, string AnalystProfileDigest, string ImageDigest, string? ModelDigest,
        bool Required);

    private sealed record FactRow(
        Guid AcceptedResultId, int Ordinal, string Kind, long StartMs, long EndMs, double Confidence, string PayloadJson);

    private sealed record JobRow(Guid NodeId, int? SegmentNumber, string State, bool HasAcceptedResult);

    private static FactNodeRecord ToRecord(NodeRow n) => new(
        n.NodeId, n.NodeKey, n.CapabilityId, n.CapabilityVersion, n.SchemaVersion, n.OutputType, n.ExecutionScope,
        n.AnalystProfileId, n.AnalystProfileDigest, n.ImageDigest, n.ModelDigest, n.Required);

    public async Task<FactRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var run = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            """
            SELECT r.id AS RunId, r.match_id AS MatchId, r.team_id AS TeamId, r.state AS State, r.workflow_id AS WorkflowId,
                   r.workflow_version AS WorkflowVersion, r.version AS Version,
                   r.recording_set_version_id AS RecordingSetVersionId, s.snapshot::text AS Snapshot
            FROM socalytics.analysis_runs r
            JOIN socalytics.analysis_run_snapshots s ON s.run_id = r.id
            WHERE r.id = @runId
            """,
            new { runId }, session.Transaction, cancellationToken: cancellationToken));
        if (run is null)
        {
            return null;
        }

        using var snapshot = JsonDocument.Parse(run.Snapshot);
        var durationMs = snapshot.RootElement.GetProperty("segmentationPolicy").GetProperty("durationSeconds").GetInt32() * 1000L;

        var spans = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT t.spans::text
            FROM socalytics.recording_set_members m
            JOIN socalytics.recording_timeline_mappings t ON t.id = m.timeline_mapping_id
            WHERE m.recording_set_version_id = @setId
            """,
            new { setId = run.RecordingSetVersionId }, session.Transaction, cancellationToken: cancellationToken))).ToList();
        long? coverageEnd = null;
        foreach (var json in spans)
        {
            using var document = JsonDocument.Parse(json);
            foreach (var span in document.RootElement.GetProperty("spans").EnumerateArray())
            {
                var length = span.GetProperty("mediaEndMilliseconds").GetInt64() - span.GetProperty("mediaStartMilliseconds").GetInt64();
                if (length > 0)
                {
                    coverageEnd = Math.Max(coverageEnd ?? 0, span.GetProperty("matchStartMilliseconds").GetInt64() + length);
                }
            }
        }

        var basis = await connection.QuerySingleAsync<string>(new CommandDefinition(
            """
            SELECT @runVersion::text || ':' ||
                   coalesce((SELECT md5(string_agg(id::text || '=' || version::text, ',' ORDER BY id))
                             FROM socalytics.analysis_logical_jobs WHERE run_id = @runId), '') || ':' ||
                   (SELECT count(*) FROM socalytics.analysis_accepted_results WHERE run_id = @runId)::text || ':' ||
                   (SELECT count(*) FROM socalytics.analysis_indexed_facts WHERE run_id = @runId)::text
            """,
            new { runId, runVersion = run.Version }, session.Transaction, cancellationToken: cancellationToken));

        return new FactRunRecord(
            run.RunId, run.MatchId, run.TeamId, run.State, run.WorkflowId, run.WorkflowVersion, durationMs, coverageEnd, basis);
    }

    public async Task<IReadOnlyList<FactNodeRecord>> ResolveNodesAsync(
        Guid runId, string capabilityId, string capabilityVersion, string schemaVersion, string? nodeKey,
        CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<NodeRow>(new CommandDefinition(
            NodeSelect + """

             WHERE n.run_id = @runId AND n.capability_id = @capabilityId AND n.capability_version = @capabilityVersion
               AND s.snapshot ->> 'schemaVersion' = @schemaVersion
               AND (@nodeKey::text IS NULL OR n.node_key = @nodeKey)
             ORDER BY n.ordinal
             """,
            new { runId, capabilityId, capabilityVersion, schemaVersion, nodeKey },
            session.Transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(ToRecord)];
    }

    public async Task<IReadOnlyList<FactNodeRecord>> ListNodesAsync(Guid runId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<NodeRow>(new CommandDefinition(
            NodeSelect + " WHERE n.run_id = @runId ORDER BY n.ordinal",
            new { runId }, session.Transaction, cancellationToken: cancellationToken));
        return [.. rows.Select(ToRecord)];
    }

    public async Task<FactPageResult> QueryPageAsync(FactPageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<FactRow>(new CommandDefinition(
            """
            SELECT accepted_result_id AS AcceptedResultId, ordinal AS Ordinal, kind AS Kind, start_ms AS StartMs,
                   end_ms AS EndMs, confidence AS Confidence, payload::text AS PayloadJson
            FROM socalytics.analysis_indexed_facts
            WHERE run_id = @runId AND node_id = @nodeId
              AND (@toMs::bigint IS NULL OR start_ms < @toMs)
              AND (@fromMs::bigint IS NULL OR end_ms > @fromMs)
              AND (@hasAfter = false OR (start_ms, end_ms, accepted_result_id, ordinal) > (@afterStart, @afterEnd, @afterResult, @afterOrdinal))
            ORDER BY start_ms, end_ms, accepted_result_id, ordinal
            LIMIT @limit
            """,
            new
            {
                runId = query.RunId,
                nodeId = query.NodeId,
                fromMs = query.FromMs,
                toMs = query.ToMs,
                hasAfter = query.After is not null,
                afterStart = query.After?.StartMs ?? 0L,
                afterEnd = query.After?.EndMs ?? 0L,
                afterResult = query.After?.AcceptedResultId ?? Guid.Empty,
                afterOrdinal = query.After?.Ordinal ?? 0,
                limit = query.PageSize + 1,
            },
            session.Transaction,
            cancellationToken: cancellationToken))).ToList();

        return new FactPageResult(
            [.. rows.Take(query.PageSize).Select(r => new FactItemRecord(
                r.AcceptedResultId, r.Ordinal, r.Kind, r.StartMs, r.EndMs, r.Confidence, r.PayloadJson))],
            rows.Count > query.PageSize);
    }

    public async Task<FactTimeRange?> GetRunFactAsync(
        Guid runId, Guid acceptedResultId, int ordinal, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<FactTimeRange>(new CommandDefinition(
            """
            SELECT start_ms AS StartMs, end_ms AS EndMs
            FROM socalytics.analysis_indexed_facts
            WHERE run_id = @runId AND accepted_result_id = @acceptedResultId AND ordinal = @ordinal
            """,
            new { runId, acceptedResultId, ordinal }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, long>> CountFactsByNodeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<(Guid NodeId, long Count)>(new CommandDefinition(
            "SELECT node_id AS NodeId, count(*) AS Count FROM socalytics.analysis_indexed_facts WHERE run_id = @runId GROUP BY node_id",
            new { runId }, session.Transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.NodeId, r => r.Count);
    }

    public async Task<FactCoverageInputs> GetCoverageInputsAsync(Guid runId, Guid? nodeId, CancellationToken cancellationToken)
    {
        var run = await GetRunAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException("The analysis run does not exist.");
        var connection = await session.GetConnectionAsync(cancellationToken);
        var jobs = await connection.QueryAsync<JobRow>(new CommandDefinition(
            """
            SELECT j.node_id AS NodeId, j.segment_number AS SegmentNumber, j.state AS State,
                   EXISTS (SELECT 1 FROM socalytics.analysis_accepted_results r WHERE r.logical_job_id = j.id) AS HasAcceptedResult
            FROM socalytics.analysis_logical_jobs j
            WHERE j.run_id = @runId AND (@nodeId::uuid IS NULL OR j.node_id = @nodeId)
            ORDER BY j.segment_number NULLS FIRST, j.id
            """,
            new { runId, nodeId },
            session.Transaction,
            cancellationToken: cancellationToken));

        return new FactCoverageInputs(
            [.. jobs.Select(j => new FactCoverageJobRecord(j.NodeId, j.SegmentNumber, j.State, j.HasAcceptedResult))],
            run.SegmentDurationMs,
            run.RecordingCoverageEndMs);
    }
}
