using Dapper;
using SocAlytics.Platform.Application.Analysis.Stores;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class CompletionOutcomeStore(IDbSession session) : ICompletionOutcomeStore
{
    private sealed record Row(
        Guid LogicalJobId, string IdempotencyKey, Guid AttemptId, string RequestFingerprint, string Outcome,
        Guid? AcceptedResultId, DateTime RecordedAt);

    public async Task InsertAsync(CompletionOutcomeRecord outcome, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_completion_outcomes
                (logical_job_id, idempotency_key, attempt_id, request_fingerprint, outcome, accepted_result_id, recorded_at)
            VALUES (@job, @key, @attempt, @fingerprint, @outcome, @result, @recordedAt)
            """,
            new
            {
                job = outcome.LogicalJobId,
                key = outcome.IdempotencyKey,
                attempt = outcome.AttemptId,
                fingerprint = outcome.RequestFingerprint,
                outcome = outcome.Outcome,
                result = outcome.AcceptedResultId,
                recordedAt = outcome.RecordedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<CompletionOutcomeRecord?> GetAsync(Guid logicalJobId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var r = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT logical_job_id AS LogicalJobId, idempotency_key AS IdempotencyKey, attempt_id AS AttemptId,
                   request_fingerprint AS RequestFingerprint, outcome AS Outcome,
                   accepted_result_id AS AcceptedResultId, recorded_at AS RecordedAt
            FROM socalytics.analysis_completion_outcomes
            WHERE logical_job_id = @logicalJobId AND idempotency_key = @idempotencyKey
            """,
            new { logicalJobId, idempotencyKey }, session.Transaction, cancellationToken: cancellationToken));
        return r is null
            ? null
            : new CompletionOutcomeRecord(
                r.LogicalJobId, r.IdempotencyKey, r.AttemptId, r.RequestFingerprint, r.Outcome, r.AcceptedResultId,
                new DateTimeOffset(DateTime.SpecifyKind(r.RecordedAt, DateTimeKind.Utc)));
    }
}
