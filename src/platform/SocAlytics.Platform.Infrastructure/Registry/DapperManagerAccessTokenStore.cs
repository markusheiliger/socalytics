using Dapper;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.Registry;

internal sealed class DapperManagerAccessTokenStore(IDbSession session, IRegistrationStore registrations) : IManagerAccessTokenStore
{
    public async Task InsertAsync(ManagerAccessToken token, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.analyst_manager_access_token (token_hash, registration_id, jkt, scope, issued_at, expires_at) " +
            "VALUES (@TokenHash, @RegistrationId, @Jkt, @Scope, @IssuedAt, @ExpiresAt)",
            token, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<ManagerAccessTokenLookup?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TokenRow>(new CommandDefinition(
            "SELECT token_hash AS TokenHash, registration_id AS RegistrationId, jkt AS Jkt, scope AS Scope, " +
            "issued_at AS IssuedAt, expires_at AS ExpiresAt FROM socalytics.analyst_manager_access_token WHERE token_hash = @tokenHash",
            new { tokenHash }, session.Transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var registration = await registrations.FindByIdAsync(row.RegistrationId, cancellationToken);
        return registration is null
            ? null
            : new ManagerAccessTokenLookup(new ManagerAccessToken(row.TokenHash, row.RegistrationId, row.Jkt, row.Scope, new DateTimeOffset(DateTime.SpecifyKind(row.IssuedAt, DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc))), registration);
    }

    public async Task<int> DeleteForRegistrationAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM socalytics.analyst_manager_access_token WHERE registration_id = @registrationId",
            new { registrationId }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM socalytics.analyst_manager_access_token WHERE expires_at <= @now",
            new { now }, session.Transaction, cancellationToken: cancellationToken));
    }

    private sealed record TokenRow(byte[] TokenHash, Guid RegistrationId, string Jkt, string Scope, DateTime IssuedAt, DateTime ExpiresAt);
}
