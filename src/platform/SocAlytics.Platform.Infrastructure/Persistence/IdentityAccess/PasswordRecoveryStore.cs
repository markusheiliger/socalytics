using Dapper;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;

namespace SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess;

internal sealed class PasswordRecoveryStore(IDbSession session) : IPasswordRecoveryStore
{
    private const string Columns =
        "id AS Id, account_id AS AccountId, address_hash AS AddressHash, security_stamp AS SecurityStamp, state AS State, " +
        "token_hash AS TokenHash, requested_at AS RequestedAt, issued_at AS IssuedAt, expires_at AS ExpiresAt, used_at AS UsedAt";

    public async Task<IReadOnlyList<PasswordRecoveryCandidate>> ListCandidatesAsync(
        string addressNormalized, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<CandidateRow>(new CommandDefinition(
            "SELECT a.id AS AccountId, a.account_name AS AccountName, a.membership_status AS MembershipStatus, " +
            "(a.password_hash IS NOT NULL) AS HasPassword, a.security_stamp AS SecurityStamp, e.state AS AddressState, " +
            "encode(sha256(convert_to(e.address_normalized, 'UTF8')), 'hex') AS AddressHash " +
            "FROM socalytics.member_email_addresses e " +
            "JOIN socalytics.member_account a ON a.id = e.account_id " +
            "WHERE e.address_normalized = @addressNormalized ORDER BY a.account_name, a.id",
            new { addressNormalized }, session.Transaction, cancellationToken: cancellationToken));
        return rows
            .Select(r => new PasswordRecoveryCandidate(
                r.AccountId, r.AccountName, r.MembershipStatus, r.HasPassword, r.SecurityStamp, r.AddressState, r.AddressHash))
            .ToList();
    }

    public async Task CreatePendingAsync(
        Guid id, Guid accountId, string addressHash, string securityStamp, DateTimeOffset requestedAt,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "INSERT INTO socalytics.password_recoveries (id, account_id, address_hash, security_stamp, state, requested_at) " +
            "VALUES (@id, @accountId, @addressHash, @securityStamp, 'pending', @requestedAt)",
            new { id, accountId, addressHash, securityStamp, requestedAt }, cancellationToken);

    public Task<int> SupersedeOpenAsync(Guid accountId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE socalytics.password_recoveries SET state = 'superseded' WHERE account_id = @accountId AND state IN ('pending', 'issued')",
            new { accountId }, cancellationToken);

    public Task<StoredPasswordRecovery?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        QueryOne("id = @id", new { id }, cancellationToken);

    public Task<StoredPasswordRecovery?> FindByTokenHashForUpdateAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        session.RequireTransaction();
        return QueryOne("token_hash = @tokenHash FOR UPDATE", new { tokenHash }, cancellationToken);
    }

    public async Task<bool> IssueAsync(
        Guid id, byte[] tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.password_recoveries SET state = 'issued', token_hash = @tokenHash, issued_at = @issuedAt, expires_at = @expiresAt " +
            "WHERE id = @id AND state IN ('pending', 'issued')",
            new { id, tokenHash, issuedAt, expiresAt }, cancellationToken) > 0;

    public async Task<bool> MarkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.password_recoveries SET state = 'used', used_at = @usedAt WHERE id = @id AND state = 'issued'",
            new { id, usedAt }, cancellationToken) > 0;

    public async Task<bool> MarkInvalidatedAsync(Guid id, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.password_recoveries SET state = 'invalidated' WHERE id = @id AND state IN ('pending', 'issued')",
            new { id }, cancellationToken) > 0;

    public async Task<bool> MarkExpiredAsync(Guid id, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.password_recoveries SET state = 'expired' WHERE id = @id AND state = 'issued'",
            new { id }, cancellationToken) > 0;

    private async Task<int> ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
    }

    private async Task<StoredPasswordRecovery?> QueryOne(string where, object parameters, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.password_recoveries WHERE {where}",
            parameters, session.Transaction, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new StoredPasswordRecovery(
                row.Id, row.AccountId, row.AddressHash, row.SecurityStamp, row.State, row.TokenHash,
                EmailRows.Utc(row.RequestedAt), EmailRows.Utc(row.IssuedAt), EmailRows.Utc(row.ExpiresAt), EmailRows.Utc(row.UsedAt));
    }

    private sealed record CandidateRow(
        Guid AccountId, string AccountName, string MembershipStatus, bool HasPassword, string SecurityStamp,
        string AddressState, string AddressHash);

    private sealed record Row(
        Guid Id, Guid AccountId, string AddressHash, string SecurityStamp, string State, byte[]? TokenHash,
        DateTime RequestedAt, DateTime? IssuedAt, DateTime? ExpiresAt, DateTime? UsedAt);
}
