using Dapper;
using SocAlytics.Platform.Application.Analysis.Results;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class AcceptedResultManifestStore(IDbSession session) : IAcceptedResultManifestStore
{
    private sealed record Row(
        Guid AcceptedResultId, string ContractVersion, string OutputType, long ValidityStartMs, long ValidityEndMs,
        int FactCount, string Artifacts, string UpstreamLineage);

    public async Task InsertAsync(AcceptedResultManifestRecord manifest, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_accepted_result_manifests
                (accepted_result_id, contract_version, output_type, validity_start_ms, validity_end_ms, fact_count,
                 artifacts, upstream_lineage)
            VALUES (@id, @version, @outputType, @start, @end, @facts, @artifacts::jsonb, @lineage::jsonb)
            """,
            new
            {
                id = manifest.AcceptedResultId,
                version = manifest.ContractVersion,
                outputType = manifest.OutputType,
                start = manifest.ValidityStartMs,
                end = manifest.ValidityEndMs,
                facts = manifest.FactCount,
                artifacts = manifest.ArtifactsJson,
                lineage = manifest.UpstreamLineageJson,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<AcceptedResultManifestRecord?> GetAsync(Guid acceptedResultId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var r = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT accepted_result_id AS AcceptedResultId, contract_version AS ContractVersion, output_type AS OutputType,
                   validity_start_ms AS ValidityStartMs, validity_end_ms AS ValidityEndMs, fact_count AS FactCount,
                   artifacts::text AS Artifacts, upstream_lineage::text AS UpstreamLineage
            FROM socalytics.analysis_accepted_result_manifests
            WHERE accepted_result_id = @acceptedResultId
            """,
            new { acceptedResultId }, session.Transaction, cancellationToken: cancellationToken));
        return r is null
            ? null
            : new AcceptedResultManifestRecord(
                r.AcceptedResultId, r.ContractVersion, r.OutputType, r.ValidityStartMs, r.ValidityEndMs, r.FactCount,
                r.Artifacts, r.UpstreamLineage);
    }
}
