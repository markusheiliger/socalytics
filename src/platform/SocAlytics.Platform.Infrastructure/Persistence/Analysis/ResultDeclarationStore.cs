using Dapper;
using SocAlytics.Platform.Application.Analysis.Results;

namespace SocAlytics.Platform.Infrastructure.Persistence.Analysis;

internal sealed class ResultDeclarationStore(IDbSession session) : IResultDeclarationStore
{
    private sealed record Row(Guid AttemptId, string Name, long SizeBytes, string Digest, DateTime DeclaredAt);

    private static ResultObjectDeclaration ToRecord(Row r) => new(
        r.AttemptId, r.Name, r.SizeBytes, r.Digest, new DateTimeOffset(DateTime.SpecifyKind(r.DeclaredAt, DateTimeKind.Utc)));

    public async Task<ResultDeclarationOutcome> DeclareAsync(ResultObjectDeclaration declaration, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.analysis_result_object_declarations (attempt_id, name, size_bytes, digest, declared_at)
            VALUES (@attempt, @name, @size, @digest, @declaredAt)
            ON CONFLICT (attempt_id, name) DO NOTHING
            """,
            new
            {
                attempt = declaration.AttemptId,
                name = declaration.Name,
                size = declaration.SizeBytes,
                digest = declaration.Digest,
                declaredAt = declaration.DeclaredAt,
            },
            transaction,
            cancellationToken: cancellationToken));
        if (inserted == 1)
        {
            return ResultDeclarationOutcome.Declared;
        }

        var existing = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT digest FROM socalytics.analysis_result_object_declarations WHERE attempt_id = @attempt AND name = @name",
            new { attempt = declaration.AttemptId, name = declaration.Name }, transaction, cancellationToken: cancellationToken));
        return existing == declaration.Digest ? ResultDeclarationOutcome.AlreadyDeclared : ResultDeclarationOutcome.DigestConflict;
    }

    public async Task<IReadOnlyList<ResultObjectDeclaration>> ListByAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT attempt_id AS AttemptId, name AS Name, size_bytes AS SizeBytes, digest AS Digest, declared_at AS DeclaredAt
            FROM socalytics.analysis_result_object_declarations
            WHERE attempt_id = @attemptId
            ORDER BY name
            """,
            new { attemptId }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToRecord).ToList();
    }
}
