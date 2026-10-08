namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record SignInCommand(string AccountName, string Password, byte[]? PresentedTokenHash = null);

public sealed record SignedInSession(
    Guid SessionId,
    string RawToken,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    bool PasswordChangeRequired,
    Guid AccountId);
