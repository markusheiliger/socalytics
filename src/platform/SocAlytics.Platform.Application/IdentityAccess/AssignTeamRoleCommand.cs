using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record AssignTeamRoleCommand(Guid MemberId, Guid TeamId, TeamRole Role);
