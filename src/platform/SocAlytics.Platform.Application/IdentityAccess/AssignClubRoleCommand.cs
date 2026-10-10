using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record AssignClubRoleCommand(Guid MemberId, ClubRole Role);
