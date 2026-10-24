using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class AnalysisRunRequestStore(IDbSession session) : IAnalysisRunRequestStore
{
    private const string Select =
        """
        SELECT idempotency_key AS IdempotencyKey, match_id AS MatchId, requested_by AS RequestedBy, request_id AS RequestId,
               outcome AS Outcome, rejection_category AS RejectionCategory, recording_set_version_id AS RecordingSetVersionId,
               run_id AS RunId, current_run_id AS CurrentRunId, created_at AS CreatedAt
        FROM socalytics.analysis_run_requests
        """;

    private sealed record Row(
        string IdempotencyKey, Guid MatchId, Guid RequestedBy, Guid RequestId, string Outcome, string? RejectionCategory,
        Guid? RecordingSetVersionId, Guid? RunId, Guid? CurrentRunId, DateTime CreatedAt);

    private static AnalysisRunRequestRecord ToRecord(Row r) => new(
        r.IdempotencyKey, r.MatchId, r.RequestedBy, r.RequestId, r.Outcome, r.RejectionCategory, r.RecordingSetVersionId,
        r.RunId, r.CurrentRunId, new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)));

    public async Task<AnalysisRunRequestRecord?> FindAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Select + " WHERE idempotency_key = @idempotencyKey", new { idempotencyKey }, session.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<AnalysisRunRequestInsertResult> TryInsertAsync(AnalysisRunRequestRecord record, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_run_requests
                (idempotency_key, match_id, requested_by, request_id, outcome, rejection_category,
                 recording_set_version_id, run_id, current_run_id, created_at)
            VALUES (@key, @match, @requestedBy, @requestId, @outcome, @category, @set, @run, @currentRun, @createdAt)
            ON CONFLICT (idempotency_key) DO NOTHING
            """,
            new
            {
                key = record.IdempotencyKey,
                match = record.MatchId,
                requestedBy = record.RequestedBy,
                requestId = record.RequestId,
                outcome = record.Outcome,
                category = record.RejectionCategory,
                set = record.RecordingSetVersionId,
                run = record.RunId,
                currentRun = record.CurrentRunId,
                createdAt = record.CreatedAt,
            },
            transaction,
            cancellationToken: cancellationToken)) == 1;
        if (inserted)
        {
            return new AnalysisRunRequestInsertResult(true, record);
        }

        var row = await connection.QuerySingleAsync<Row>(new CommandDefinition(
            Select + " WHERE idempotency_key = @key", new { key = record.IdempotencyKey }, transaction,
            cancellationToken: cancellationToken));
        return new AnalysisRunRequestInsertResult(false, ToRecord(row));
    }
}
