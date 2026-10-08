using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.IdentityAccess;

public interface IAccessAuthorizer
{
    Task<AccessDecision> AuthorizeClubAsync(ClubPermission permission, CancellationToken cancellationToken);

    Task<AccessDecision> AuthorizeClubAsync(
        ClubPermission permission,
        AuditResource resource,
        CancellationToken cancellationToken);

    Task<AccessDecision> AuthorizeTeamResourceAsync(
        TeamOwnedResource resource,
        TeamPermission permission,
        CancellationToken cancellationToken);

    Task<TeamVisibility> GetVisibleTeamsAsync(CancellationToken cancellationToken);
}
