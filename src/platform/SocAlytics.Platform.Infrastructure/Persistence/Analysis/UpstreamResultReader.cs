using System.Text;
using System.Text.Json;
using Dapper;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class UpstreamResultReader(IDbSession session) : IUpstreamResultReader
{
    private sealed record JobRow(Guid RunId, string ExecutionScope, Guid? RecordingVersionId, int? SegmentNumber);

    private sealed record NodeRow(Guid Id, string ExecutionScope);

    private sealed record UpstreamJobRow(Guid Id, Guid? RecordingVersionId, int? SegmentNumber, string State);

    private sealed record ResultRow(
        Guid Id, Guid LogicalJobId, string ManifestReference, string ManifestChecksum, string ContractVersion,
        string OutputType, long ValidityStartMs, long ValidityEndMs, int FactCount, string Artifacts, string UpstreamLineage);

    private sealed record Position(long WindowStartMs, Guid AcceptedResultId);

    public async Task<UpstreamResultsPage?> ReadAsync(UpstreamReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var after = ParseToken(request.ContinuationToken);
        var connection = await session.GetConnectionAsync(cancellationToken);

        var job = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            """
            SELECT run_id AS RunId, execution_scope AS ExecutionScope, recording_version_id AS RecordingVersionId,
                   segment_number AS SegmentNumber
            FROM socalytics.analysis_logical_jobs WHERE id = @jobId
            """,
            new { jobId = request.JobId }, session.Transaction, cancellationToken: cancellationToken));
        var inputsJson = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT inputs::text FROM socalytics.analysis_logical_job_inputs WHERE logical_job_id = @jobId",
            new { jobId = request.JobId }, session.Transaction, cancellationToken: cancellationToken));
        if (job is null || inputsJson is null)
        {
            return null;
        }

        var (status, resultIds) = FindEntry(inputsJson, request.UpstreamNodeKey);
        if (status is null)
        {
            return null;
        }

        var node = await connection.QuerySingleOrDefaultAsync<NodeRow>(new CommandDefinition(
            """
            SELECT id AS Id, execution_scope AS ExecutionScope
            FROM socalytics.analysis_workflow_nodes WHERE run_id = @runId AND node_key = @key
            """,
            new { runId = job.RunId, key = request.UpstreamNodeKey }, session.Transaction, cancellationToken: cancellationToken));
        if (node is null)
        {
            return null;
        }

        var snapshotJson = await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT snapshot::text FROM socalytics.analysis_run_snapshots WHERE run_id = @runId",
            new { runId = job.RunId }, session.Transaction, cancellationToken: cancellationToken));
        using var snapshot = JsonDocument.Parse(snapshotJson);
        var durationMs = snapshot.RootElement.GetProperty("segmentationPolicy").GetProperty("durationSeconds").GetInt32() * 1000L;

        var upstreamJobs = (await connection.QueryAsync<UpstreamJobRow>(new CommandDefinition(
            """
            SELECT id AS Id, recording_version_id AS RecordingVersionId, segment_number AS SegmentNumber, state AS State
            FROM socalytics.analysis_logical_jobs WHERE node_id = @nodeId
            """,
            new { nodeId = node.Id }, session.Transaction, cancellationToken: cancellationToken))).ToList();

        var rows = resultIds.Count == 0
            ? []
            : (await connection.QueryAsync<ResultRow>(new CommandDefinition(
                """
                SELECT r.id AS Id, r.logical_job_id AS LogicalJobId, r.manifest_reference AS ManifestReference,
                       r.manifest_checksum AS ManifestChecksum, m.contract_version AS ContractVersion,
                       m.output_type AS OutputType, m.validity_start_ms AS ValidityStartMs,
                       m.validity_end_ms AS ValidityEndMs, m.fact_count AS FactCount,
                       m.artifacts::text AS Artifacts, m.upstream_lineage::text AS UpstreamLineage
                FROM socalytics.analysis_accepted_results r
                JOIN socalytics.analysis_accepted_result_manifests m ON m.accepted_result_id = r.id
                WHERE r.id = ANY(@ids)
                """,
                new { ids = resultIds.ToArray() }, session.Transaction, cancellationToken: cancellationToken))).ToList();

        var segmentScoped = node.ExecutionScope == "segment";
        var sameSegmentOnly = segmentScoped && job.ExecutionScope == "segment";
        var targetJobs = upstreamJobs
            .Where(u => !sameSegmentOnly || (u.RecordingVersionId == job.RecordingVersionId && u.SegmentNumber == job.SegmentNumber))
            .Where(u => !segmentScoped || InSelection(u, request, durationMs))
            .OrderBy(u => u.SegmentNumber)
            .ThenBy(u => u.RecordingVersionId)
            .ToList();

        var jobsWithResult = rows.Select(r => r.LogicalJobId).ToHashSet();
        var coverage = CoverageBuilder.Build(
            status,
            targetJobs.Select(u => new CoverageTarget(
                SegmentIdOf(u),
                jobsWithResult.Contains(u.Id),
                Enum.Parse<LogicalJobState>(u.State))));

        var jobsById = upstreamJobs.ToDictionary(u => u.Id);
        var selected = new HashSet<Guid>(targetJobs.Select(u => u.Id));
        var items = rows
            .Where(r => jobsById.TryGetValue(r.LogicalJobId, out var u) && (!segmentScoped || selected.Contains(u.Id)))
            .Select(r => ToItem(r, jobsById[r.LogicalJobId], segmentScoped, durationMs))
            .OrderBy(i => i.WindowStartMs ?? 0)
            .ThenBy(i => i.AcceptedResultId)
            .Where(i => after is null || (i.WindowStartMs ?? 0, i.AcceptedResultId).CompareTo((after.WindowStartMs, after.AcceptedResultId)) > 0)
            .Take(request.PageSize + 1)
            .ToList();

        string? next = null;
        if (items.Count > request.PageSize)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            next = WriteToken(new Position(last.WindowStartMs ?? 0, last.AcceptedResultId));
        }

        return new UpstreamResultsPage(status, items, coverage, next);
    }

    private static (string? Status, List<Guid> ResultIds) FindEntry(string inputsJson, string upstreamNodeKey)
    {
        using var doc = JsonDocument.Parse(inputsJson);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.GetProperty("upstreamNodeKey").GetString() == upstreamNodeKey)
            {
                return (
                    e.GetProperty("status").GetString(),
                    e.GetProperty("acceptedResultIds").EnumerateArray().Select(x => x.GetGuid()).ToList());
            }
        }

        return (null, []);
    }

    private static string? SegmentIdOf(UpstreamJobRow job) =>
        job.SegmentNumber is { } n ? $"{job.RecordingVersionId}-{n}" : null;

    private static bool InSelection(UpstreamJobRow job, UpstreamReadRequest request, long durationMs)
    {
        if (request.SegmentIds is { Count: > 0 } ids)
        {
            return ids.Contains(SegmentIdOf(job), StringComparer.Ordinal);
        }

        if (request.MatchStartMs is { } start && request.MatchEndMs is { } end && job.SegmentNumber is { } n)
        {
            var windowStart = (n - 1) * durationMs;
            return windowStart < end && windowStart + durationMs > start;
        }

        return true;
    }

    private static UpstreamResultItem ToItem(ResultRow r, UpstreamJobRow job, bool segmentScoped, long durationMs)
    {
        var manifest = new AcceptedResultManifestRecord(
            r.Id, r.ContractVersion, r.OutputType, r.ValidityStartMs, r.ValidityEndMs, r.FactCount, r.Artifacts, r.UpstreamLineage);
        if (!segmentScoped || job.SegmentNumber is not { } n)
        {
            return new UpstreamResultItem(r.Id, null, null, null, r.ManifestReference, r.ManifestChecksum, manifest);
        }

        var start = (n - 1) * durationMs;
        return new UpstreamResultItem(r.Id, SegmentIdOf(job), start, start + durationMs, r.ManifestReference, r.ManifestChecksum, manifest);
    }

    private static string WriteToken(Position position) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{position.WindowStartMs}|{position.AcceptedResultId:N}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Position? ParseToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            if (parts.Length == 2 && long.TryParse(parts[0], out var start) && Guid.TryParseExact(parts[1], "N", out var id))
            {
                return new Position(start, id);
            }
        }
        catch (FormatException)
        {
        }

        throw new InvalidContinuationTokenException();
    }
}
