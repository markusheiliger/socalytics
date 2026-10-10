namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record SessionCreated(Guid SessionId, DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt);

public sealed record SessionRecord(
    Guid SessionId,
    Guid AccountId,
    string SessionSecurityStamp,
    DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    DateTimeOffset? EndedAt,
    string AccountName,
    string MembershipStatus,
    string AccountSecurityStamp,
    bool PasswordChangeRequired);

public interface ISessionStore
{
    Task<SessionCreated> CreateAsync(Guid accountId, byte[] tokenHash, string securityStamp, DateTimeOffset now, CancellationToken cancellationToken);

    Task<SessionRecord?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>Slides <c>last_seen_at</c> and the idle expiry unless the session slid within the last 60 seconds; returns the new idle expiry, or null when it did not slide.</summary>
    Task<DateTimeOffset?> SlideAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);

    Task EndAsync(Guid sessionId, string endReason, DateTimeOffset now, CancellationToken cancellationToken);

    Task EndAllForAccountAsync(Guid accountId, string endReason, Guid? exceptSessionId, DateTimeOffset now, CancellationToken cancellationToken);

    Task SetSecurityStampAsync(Guid sessionId, string stamp, CancellationToken cancellationToken);
}
