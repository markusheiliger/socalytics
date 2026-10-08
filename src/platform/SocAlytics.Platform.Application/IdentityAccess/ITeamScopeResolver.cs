namespace SocAlytics.Platform.Application.IdentityAccess;

public interface ITeamScopeResolver
{
    Task<TeamScope?> ResolveAsync(TeamOwnedResource resource, CancellationToken cancellationToken);
}
