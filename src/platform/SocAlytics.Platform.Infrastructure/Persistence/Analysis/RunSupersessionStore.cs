using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class RunSupersessionStore(IDbSession session) : IRunSupersessionStore
{
    private sealed record EntryRow(
        Guid RunId, Guid TeamId, string State, DateTime FinalizedAt, Guid VersionId, DateTime CreatedAt, Guid? SupersededByRunId);

    private static DateTimeOffset Utc(DateTime v) => new(DateTime.SpecifyKind(v, DateTimeKind.Utc));

    public async Task<IReadOnlyList<MatchRunEntry>> ListMatchRunsAsync(Guid matchId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EntryRow>(new CommandDefinition(
            """
            SELECT r.id AS RunId, r.team_id AS TeamId, r.state AS State,
                   r.recording_set_finalized_at AS FinalizedAt, r.recording_set_version_id AS VersionId,
                   r.created_at AS CreatedAt, s.superseding_run_id AS SupersededByRunId
            FROM socalytics.analysis_runs r
            LEFT JOIN socalytics.analysis_run_supersessions s ON s.run_id = r.id
            WHERE r.match_id = @matchId
            ORDER BY r.id
            """,
            new { matchId }, session.Transaction, cancellationToken: cancellationToken));
        return rows
            .Select(r => new MatchRunEntry(
                r.RunId, r.TeamId, Enum.Parse<AnalysisRunState>(r.State),
                new RecordingSetVersionKey(Utc(r.FinalizedAt), r.VersionId), Utc(r.CreatedAt), r.SupersededByRunId))
            .ToList();
    }

    public async Task InsertAsync(NewRunSupersession supersession, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_run_supersessions (run_id, match_id, superseding_run_id, superseded_at, previous_state)
            VALUES (@RunId, @MatchId, @SupersedingRunId, @SupersededAt, @PreviousState)
            """,
            new
            {
                supersession.RunId,
                supersession.MatchId,
                supersession.SupersedingRunId,
                supersession.SupersededAt,
                PreviousState = supersession.PreviousState.ToString(),
            },
            transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> ListMatchesNeedingSupersessionAsync(Guid? after, int batch, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT m.match_id
            FROM (SELECT DISTINCT match_id FROM socalytics.analysis_runs WHERE @after::uuid IS NULL OR match_id > @after::uuid) m
            WHERE EXISTS (
                SELECT 1
                FROM socalytics.analysis_runs r
                CROSS JOIN LATERAL (
                    SELECT c.recording_set_finalized_at, c.recording_set_version_id
                    FROM socalytics.analysis_runs c
                    WHERE c.match_id = m.match_id
                    ORDER BY c.recording_set_finalized_at DESC, c.recording_set_version_id DESC, c.created_at DESC, c.id DESC
                    LIMIT 1
                ) cur
                WHERE r.match_id = m.match_id
                  AND (r.recording_set_finalized_at, r.recording_set_version_id) < (cur.recording_set_finalized_at, cur.recording_set_version_id)
                  AND NOT EXISTS (SELECT 1 FROM socalytics.analysis_run_supersessions s WHERE s.run_id = r.id))
            ORDER BY m.match_id
            LIMIT @batch
            """,
            new { after, batch }, session.Transaction, cancellationToken: cancellationToken));
        return ids.ToList();
    }
}
