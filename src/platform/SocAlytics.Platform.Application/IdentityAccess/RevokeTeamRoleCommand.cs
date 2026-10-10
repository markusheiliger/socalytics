namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record RevokeTeamRoleCommand(Guid MemberId, Guid TeamId);
