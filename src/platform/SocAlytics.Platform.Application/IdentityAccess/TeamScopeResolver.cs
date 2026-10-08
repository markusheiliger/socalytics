namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class TeamScopeResolver(IEnumerable<ITeamScopeSource> sources) : ITeamScopeResolver
{
    private readonly Dictionary<string, ITeamScopeSource> _sources =
        sources.ToDictionary(source => source.ResourceKind, StringComparer.Ordinal);

    public Task<TeamScope?> ResolveAsync(TeamOwnedResource resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Kind is not null && _sources.TryGetValue(resource.Kind, out var source)
            ? source.ResolveAsync(resource.Id, cancellationToken)
            : Task.FromResult<TeamScope?>(null);
    }
}
