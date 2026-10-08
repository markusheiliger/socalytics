using Dapper;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Infrastructure.Persistence;

internal static class VersionedWrites
{
    // guardedWrite must be an UPDATE ... WHERE id = @Id AND version = @ExpectedVersion RETURNING version;
    // currentVersionProbe must be SELECT version ... WHERE id = @Id. Both must carry session.Transaction.
    public static async Task<VersionedWriteResult> ExecuteAsync(
        IDbSession session,
        CommandDefinition guardedWrite,
        CommandDefinition currentVersionProbe)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.RequireTransaction();
        var connection = await session.GetConnectionAsync(guardedWrite.CancellationToken);

        var applied = await connection.QuerySingleOrDefaultAsync<long?>(guardedWrite);
        if (applied is { } version)
        {
            return new VersionedWriteResult(VersionedWriteOutcome.Applied, version);
        }

        var current = await connection.QuerySingleOrDefaultAsync<long?>(currentVersionProbe);
        return current is { } persisted
            ? new VersionedWriteResult(VersionedWriteOutcome.ConcurrencyConflict, persisted)
            : new VersionedWriteResult(VersionedWriteOutcome.NotFound, null);
    }
}
