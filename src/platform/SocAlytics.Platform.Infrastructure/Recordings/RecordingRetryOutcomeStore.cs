using Dapper;
using Npgsql;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.Recordings;

internal sealed class RecordingRetryOutcomeStore(IDbSession session) : IRecordingRetryOutcomeStore
{
    private sealed record Row(string RequestDigest, short StatusCode, string Result);

    public async Task<RecordingRetryOutcome?> FindAsync(
        RecordingRetryOperation operation, Guid matchId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT request_digest AS RequestDigest, status_code AS StatusCode, result::text AS Result " +
            "FROM socalytics.recording_retry_outcomes WHERE operation = @operation AND match_id = @matchId AND idempotency_key = @key",
            new { operation = operation.ToWireValue(), matchId, key = idempotencyKey },
            session.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : new RecordingRetryOutcome(row.RequestDigest, row.StatusCode, row.Result);
    }

    public async Task InsertAsync(
        RecordingRetryOperation operation,
        Guid matchId,
        string idempotencyKey,
        string requestDigest,
        int statusCode,
        string resultJson,
        Guid createdBy,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO socalytics.recording_retry_outcomes " +
                "(operation, match_id, idempotency_key, request_digest, status_code, result, created_by, created_at) " +
                "VALUES (@operation, @matchId, @key, @digest, @status, @result::jsonb, @createdBy, @createdAt)",
                new
                {
                    operation = operation.ToWireValue(),
                    matchId,
                    key = idempotencyKey,
                    digest = requestDigest,
                    status = (short)statusCode,
                    result = resultJson,
                    createdBy,
                    createdAt,
                },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DuplicateRetryKeyException(ex);
        }
    }
}
