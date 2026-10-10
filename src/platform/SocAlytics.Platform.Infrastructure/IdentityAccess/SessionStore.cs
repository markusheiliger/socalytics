using Dapper;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

internal sealed class SessionStore(IDbSession session, IOptions<IdentityAccessOptions> options) : ISessionStore
{
    public async Task<SessionCreated> CreateAsync(
        Guid accountId, byte[] tokenHash, string securityStamp, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var id = Guid.NewGuid();
        var idle = now + options.Value.Session.IdleTimeout;
        var absolute = now + options.Value.Session.AbsoluteLifetime;
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.member_session (id, member_account_id, token_hash, security_stamp, created_at, last_seen_at, idle_expires_at, absolute_expires_at) " +
            "VALUES (@id, @accountId, @tokenHash, @securityStamp, @now, @now, @idle, @absolute)",
            new { id, accountId, tokenHash, securityStamp, now, idle, absolute },
            transaction,
            cancellationToken: cancellationToken));
        return new SessionCreated(id, idle, absolute);
    }

    public async Task<SessionRecord?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            "SELECT s.id AS SessionId, s.member_account_id AS AccountId, s.security_stamp AS SessionSecurityStamp, " +
            "s.last_seen_at AS LastSeenAt, s.idle_expires_at AS IdleExpiresAt, s.absolute_expires_at AS AbsoluteExpiresAt, " +
            "s.ended_at AS EndedAt, a.account_name AS AccountName, a.membership_status AS MembershipStatus, " +
            "a.security_stamp AS AccountSecurityStamp, a.password_change_required AS PasswordChangeRequired " +
            "FROM socalytics.member_session s JOIN socalytics.member_account a ON a.id = s.member_account_id " +
            "WHERE s.token_hash = @tokenHash",
            new { tokenHash },
            session.Transaction,
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new SessionRecord(
                row.SessionId, row.AccountId, row.SessionSecurityStamp, row.LastSeenAt, row.IdleExpiresAt,
                row.AbsoluteExpiresAt, row.EndedAt, row.AccountName, row.MembershipStatus, row.AccountSecurityStamp,
                row.PasswordChangeRequired);
    }

    public async Task<DateTimeOffset?> SlideAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var idle = now + options.Value.Session.IdleTimeout;
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.member_session SET last_seen_at = @now, idle_expires_at = @idle " +
            "WHERE id = @sessionId AND ended_at IS NULL AND last_seen_at <= @threshold",
            new { sessionId, now, idle, threshold = now - TimeSpan.FromSeconds(60) },
            transaction,
            cancellationToken: cancellationToken));
        return rows > 0 ? idle : null;
    }

    public async Task EndAsync(Guid sessionId, string endReason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.member_session SET ended_at = @now, end_reason = @endReason WHERE id = @sessionId AND ended_at IS NULL",
            new { sessionId, endReason, now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task EndAllForAccountAsync(
        Guid accountId, string endReason, Guid? exceptSessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.member_session SET ended_at = @now, end_reason = @endReason " +
            "WHERE member_account_id = @accountId AND ended_at IS NULL AND (@exceptSessionId::uuid IS NULL OR id <> @exceptSessionId::uuid)",
            new { accountId, endReason, exceptSessionId, now },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task SetSecurityStampAsync(Guid sessionId, string stamp, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.member_session SET security_stamp = @stamp WHERE id = @sessionId",
            new { sessionId, stamp },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed class SessionRow
    {
        public Guid SessionId { get; init; }
        public Guid AccountId { get; init; }
        public string SessionSecurityStamp { get; init; } = "";
        public DateTimeOffset LastSeenAt { get; init; }
        public DateTimeOffset IdleExpiresAt { get; init; }
        public DateTimeOffset AbsoluteExpiresAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public string AccountName { get; init; } = "";
        public string MembershipStatus { get; init; } = "";
        public string AccountSecurityStamp { get; init; } = "";
        public bool PasswordChangeRequired { get; init; }
    }
}
