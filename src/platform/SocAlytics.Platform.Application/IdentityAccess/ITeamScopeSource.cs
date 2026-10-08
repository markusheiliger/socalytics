namespace SocAlytics.Platform.Application.IdentityAccess;

public interface ITeamScopeSource
{
    string ResourceKind { get; }

    Task<TeamScope?> ResolveAsync(Guid id, CancellationToken cancellationToken);
}
