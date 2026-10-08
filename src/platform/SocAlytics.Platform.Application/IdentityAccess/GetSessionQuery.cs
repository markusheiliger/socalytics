using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record GetSessionQuery(byte[] TokenHash);

public sealed record SessionDetails(
    ValidatedSession Session,
    string AccountName,
    MembershipStatus MembershipStatus,
    IReadOnlySet<ClubRole> ClubRoles,
    IReadOnlyDictionary<Guid, TeamRole> TeamRoles);
