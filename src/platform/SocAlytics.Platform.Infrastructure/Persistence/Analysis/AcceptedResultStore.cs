using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class AcceptedResultStore(IDbSession session) : IAcceptedResultStore
{
    private const string Select =
        """
        SELECT id AS Id, logical_job_id AS LogicalJobId, run_id AS RunId, node_id AS NodeId, attempt_id AS AttemptId,
               manifest_reference AS ManifestReference, manifest_checksum AS ManifestChecksum,
               capability_id AS CapabilityId, capability_version AS CapabilityVersion, schema_version AS SchemaVersion,
               analyst_profile_digest AS AnalystProfileDigest, image_digest AS ImageDigest, model_digest AS ModelDigest,
               policy_versions::text AS PolicyVersions, executed_runtime::text AS ExecutedRuntime,
               upstream_result_ids AS UpstreamResultIds, accepted_at AS AcceptedAt
        FROM socalytics.analysis_accepted_results
        """;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid LogicalJobId { get; set; }
        public Guid RunId { get; set; }
        public Guid NodeId { get; set; }
        public Guid AttemptId { get; set; }
        public string ManifestReference { get; set; } = "";
        public string ManifestChecksum { get; set; } = "";
        public string CapabilityId { get; set; } = "";
        public string CapabilityVersion { get; set; } = "";
        public string SchemaVersion { get; set; } = "";
        public string AnalystProfileDigest { get; set; } = "";
        public string ImageDigest { get; set; } = "";
        public string? ModelDigest { get; set; }
        public string PolicyVersions { get; set; } = "";
        public string ExecutedRuntime { get; set; } = "";
        public Guid[] UpstreamResultIds { get; set; } = [];
        public DateTime AcceptedAt { get; set; }
    }

    private static AcceptedResultRecord ToRecord(Row r) => new(
        r.Id, r.LogicalJobId, r.RunId, r.NodeId, r.AttemptId, r.ManifestReference, r.ManifestChecksum,
        r.CapabilityId, r.CapabilityVersion, r.SchemaVersion, r.AnalystProfileDigest, r.ImageDigest, r.ModelDigest,
        r.PolicyVersions, r.ExecutedRuntime, r.UpstreamResultIds,
        new DateTimeOffset(DateTime.SpecifyKind(r.AcceptedAt, DateTimeKind.Utc)));

    public async Task InsertAsync(AcceptedResultRecord result, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_accepted_results
                (id, logical_job_id, run_id, node_id, attempt_id, manifest_reference, manifest_checksum, capability_id,
                 capability_version, schema_version, analyst_profile_digest, image_digest, model_digest,
                 policy_versions, executed_runtime, upstream_result_ids, accepted_at)
            VALUES (@id, @job, @run, @node, @attempt, @reference, @checksum, @capability, @capabilityVersion, @schema,
                    @profile, @image, @model, @policies::jsonb, @runtime::jsonb, @upstream, @acceptedAt)
            """,
            new
            {
                id = result.Id,
                job = result.LogicalJobId,
                run = result.RunId,
                node = result.NodeId,
                attempt = result.AttemptId,
                reference = result.ManifestReference,
                checksum = result.ManifestChecksum,
                capability = result.CapabilityId,
                capabilityVersion = result.CapabilityVersion,
                schema = result.SchemaVersion,
                profile = result.AnalystProfileDigest,
                image = result.ImageDigest,
                model = result.ModelDigest,
                policies = result.PolicyVersionsJson,
                runtime = result.ExecutedRuntimeJson,
                upstream = result.UpstreamResultIds.ToArray(),
                acceptedAt = result.AcceptedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<AcceptedResultRecord?> GetByJobAsync(Guid logicalJobId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Select + " WHERE logical_job_id = @logicalJobId", new { logicalJobId },
            session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<IReadOnlyList<AcceptedResultRecord>> ListByRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Select + " WHERE run_id = @runId ORDER BY accepted_at, id", new { runId },
            session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToRecord).ToList();
    }
}
