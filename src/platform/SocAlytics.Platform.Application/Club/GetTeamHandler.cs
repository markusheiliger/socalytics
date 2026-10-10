using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Club;

public sealed class GetTeamHandler(IUnitOfWork unitOfWork, IClubHierarchyStore clubs, IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<TeamView>> HandleAsync(GetTeamQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("team", query.TeamId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var team = await clubs.GetTeamAsync(query.TeamId, cancellationToken);
        return team is null ? OperationFailure.NotFound() : team;
    }
}
