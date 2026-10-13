using Dapper;
using SocAlytics.Platform.Application.Registry.QueueCredentials;

namespace SocAlytics.Platform.Infrastructure.Persistence.Registry;

internal sealed class QueueCredentialStore(IDbSession session) : IQueueCredentialStore
{
    private const string Columns =
        "queue_credential_id AS QueueCredentialId, registration_id AS RegistrationId, user_public_key AS UserPublicKey, " +
        "issued_at AS IssuedAt, expires_at AS ExpiresAt, revoked_at AS RevokedAt, revocation_pushed_at AS RevocationPushedAt";

    public async Task<bool> RecordIssuanceAsync(IssuedQueueCredential credential, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.analyst_manager_queue_credential " +
            "(queue_credential_id, registration_id, user_public_key, issued_at, expires_at, revoked_at, revocation_pushed_at) " +
            "VALUES (@QueueCredentialId, @RegistrationId, @UserPublicKey, @IssuedAt, @ExpiresAt, @RevokedAt, @RevocationPushedAt) " +
            "ON CONFLICT (user_public_key) DO NOTHING",
            credential, session.Transaction, cancellationToken: cancellationToken));
        return inserted == 1;
    }

    public async Task<IReadOnlyList<IssuedQueueCredential>> ListUnexpiredAsync(
        Guid registrationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<CredentialRow>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.analyst_manager_queue_credential " +
            "WHERE registration_id = @registrationId AND expires_at > @now ORDER BY issued_at, queue_credential_id",
            new { registrationId, now }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToCredential()).ToList();
    }

    public async Task<int> MarkRevokedAsync(Guid registrationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.analyst_manager_queue_credential SET revoked_at = @now " +
            "WHERE registration_id = @registrationId AND expires_at > @now AND revoked_at IS NULL",
            new { registrationId, now }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<IssuedQueueCredential>> ListRevokedUnexpiredAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<CredentialRow>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.analyst_manager_queue_credential " +
            "WHERE revoked_at IS NOT NULL AND expires_at > @now ORDER BY revoked_at, queue_credential_id",
            new { now }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToCredential()).ToList();
    }

    public async Task<int> MarkPushedAsync(
        IReadOnlyCollection<Guid> queueCredentialIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (queueCredentialIds.Count == 0)
        {
            return 0;
        }

        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.analyst_manager_queue_credential SET revocation_pushed_at = @now " +
            "WHERE queue_credential_id = ANY(@ids) AND revoked_at IS NOT NULL AND revocation_pushed_at IS NULL",
            new { ids = queueCredentialIds.ToArray(), now }, session.Transaction, cancellationToken: cancellationToken));
    }

    private sealed record CredentialRow(
        Guid QueueCredentialId, Guid RegistrationId, string UserPublicKey,
        DateTime IssuedAt, DateTime ExpiresAt, DateTime? RevokedAt, DateTime? RevocationPushedAt)
    {
        public IssuedQueueCredential ToCredential() => new(
            QueueCredentialId, RegistrationId, UserPublicKey, Utc(IssuedAt), Utc(ExpiresAt),
            RevokedAt is { } r ? Utc(r) : null, RevocationPushedAt is { } p ? Utc(p) : null);

        private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
