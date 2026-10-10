using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record RevokeClubRoleCommand(Guid MemberId, ClubRole Role);
