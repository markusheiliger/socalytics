using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class IndexedFactStore(IDbSession session) : IIndexedFactStore
{
    private const int BatchSize = 1000;

    public async Task InsertAsync(IReadOnlyList<IndexedFactRecord> facts, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        if (facts.Count == 0)
        {
            return;
        }

        var connection = await session.GetConnectionAsync(cancellationToken);
        for (var offset = 0; offset < facts.Count; offset += BatchSize)
        {
            var batch = facts.Skip(offset).Take(BatchSize).Select(f => new
            {
                resultId = f.AcceptedResultId,
                ordinal = f.Ordinal,
                run = f.RunId,
                node = f.NodeId,
                team = f.TeamId,
                match = f.MatchId,
                capability = f.CapabilityId,
                capabilityVersion = f.CapabilityVersion,
                schema = f.SchemaVersion,
                outputType = f.OutputType,
                kind = f.Kind,
                start = f.StartMs,
                end = f.EndMs,
                confidence = f.Confidence,
                payload = f.PayloadJson,
            }).ToList();

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO socalytics.analysis_indexed_facts
                    (accepted_result_id, ordinal, run_id, node_id, team_id, match_id, capability_id, capability_version,
                     schema_version, output_type, kind, start_ms, end_ms, confidence, payload)
                VALUES (@resultId, @ordinal, @run, @node, @team, @match, @capability, @capabilityVersion,
                        @schema, @outputType, @kind, @start, @end, @confidence, @payload::jsonb)
                """,
                batch,
                transaction,
                cancellationToken: cancellationToken));
        }
    }
}
