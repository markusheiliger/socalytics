namespace SocAlytics.Platform.Domain.IdentityAccess;

public sealed record MemberAccount(
    Guid Id,
    AccountName AccountName,
    MembershipStatus MembershipStatus,
    bool PasswordSet,
    DateTimeOffset? LockoutEnd,
    bool TwoFactorEnabled,
    bool PasswordChangeRequired,
    DateTimeOffset CreatedAt,
    long Version,
    IReadOnlySet<ClubRole> ClubRoles,
    IReadOnlyDictionary<Guid, TeamRole> TeamRoles)
{
    public bool IsActive => MembershipStatus == MembershipStatus.Active;

    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd is { } end && end > now;

    public bool HasClubRole(ClubRole required) => ClubRoles.Any(held => held.Includes(required));
}
