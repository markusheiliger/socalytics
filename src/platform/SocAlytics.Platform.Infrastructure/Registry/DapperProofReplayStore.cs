using Dapper;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.Registry;

internal sealed class DapperProofReplayStore(IDbSession session) : IProofReplayStore
{
    public async Task<bool> TryRecordAsync(string kind, byte[] jtiHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.analyst_manager_proof_replay (kind, jti_hash, expires_at) VALUES (@kind, @jtiHash, @expiresAt) " +
            "ON CONFLICT DO NOTHING",
            new { kind, jtiHash, expiresAt },
            session.Transaction,
            cancellationToken: cancellationToken));
        return rows == 1;
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM socalytics.analyst_manager_proof_replay WHERE expires_at <= @now",
            new { now },
            session.Transaction,
            cancellationToken: cancellationToken));
    }
}
