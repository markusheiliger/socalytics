using Dapper;
using SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess.ExternalIdentity;

internal sealed class DapperPendingExternalSignInStore(IDbSession session) : IPendingExternalSignInStore
{
    public async Task CreateAsync(PendingExternalSignIn pending, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.pending_external_sign_in " +
            "(id, state_hash, browser_binding_hash, nonce, code_verifier, provider_id, link_credential_id, created_at, expires_at) " +
            "VALUES (@Id, @StateHash, @BrowserBindingHash, @Nonce, @CodeVerifier, @ProviderId, @LinkCredentialId, @CreatedAt, @ExpiresAt)",
            new
            {
                pending.Id,
                pending.StateHash,
                pending.BrowserBindingHash,
                pending.Nonce,
                pending.CodeVerifier,
                pending.ProviderId,
                pending.LinkCredentialId,
                CreatedAt = pending.CreatedAt.UtcDateTime,
                ExpiresAt = pending.ExpiresAt.UtcDateTime,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PendingExternalSignIn?> ConsumeAsync(byte[] stateHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<PendingRow>(new CommandDefinition(
            "UPDATE socalytics.pending_external_sign_in SET consumed_at = now() " +
            "WHERE state_hash = @stateHash AND consumed_at IS NULL AND expires_at > @now " +
            "RETURNING id AS Id, state_hash AS StateHash, browser_binding_hash AS BrowserBindingHash, nonce AS Nonce, " +
            "code_verifier AS CodeVerifier, provider_id AS ProviderId, link_credential_id AS LinkCredentialId, " +
            "created_at AS CreatedAt, expires_at AS ExpiresAt",
            new { stateHash, now = now.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new PendingExternalSignIn(
                row.Id, row.StateHash, row.BrowserBindingHash, row.Nonce, row.CodeVerifier, row.ProviderId,
                row.LinkCredentialId, row.CreatedAt, row.ExpiresAt);
    }

    public async Task<int> DeleteExpiredOrConsumedAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM socalytics.pending_external_sign_in WHERE id IN (" +
            "SELECT id FROM socalytics.pending_external_sign_in WHERE consumed_at IS NOT NULL OR expires_at <= @now " +
            "ORDER BY expires_at LIMIT @limit FOR UPDATE SKIP LOCKED)",
            new { now = now.UtcDateTime, limit = Math.Min(limit, 100) },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed class PendingRow
    {
        public Guid Id { get; set; }

        public byte[] StateHash { get; set; } = [];

        public byte[] BrowserBindingHash { get; set; } = [];

        public string Nonce { get; set; } = string.Empty;

        public string CodeVerifier { get; set; } = string.Empty;

        public string ProviderId { get; set; } = string.Empty;

        public Guid? LinkCredentialId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }
    }
}
