using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record CreateMemberCommand(string? AccountName);

public sealed record MemberTeamAssignment(Guid TeamId, TeamRole Role);

public sealed record MemberDetails(
    Guid Id,
    string AccountName,
    MembershipStatus MembershipStatus,
    bool PasswordSet,
    bool LockedOut,
    DateTimeOffset? LockoutEndsAt,
    bool TwoFactorEnabled,
    IReadOnlySet<ClubRole> ClubRoles,
    IReadOnlyList<MemberTeamAssignment> TeamRoles,
    DateTimeOffset CreatedAt,
    long Version);

public sealed record MemberCreation(MemberDetails Member, IssuedCredential SetPasswordCredential);
