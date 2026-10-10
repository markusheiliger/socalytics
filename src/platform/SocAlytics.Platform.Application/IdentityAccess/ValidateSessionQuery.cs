namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record ValidateSessionQuery(byte[] TokenHash);

public sealed record ValidatedSession(
    Guid SessionId,
    Guid AccountId,
    bool PasswordChangeRequired,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt);
