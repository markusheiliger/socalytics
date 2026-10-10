using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class GetMatchHandler(IUnitOfWork unitOfWork, IClubHierarchyStore clubs, IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<Match>> HandleAsync(GetMatchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", query.MatchId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var match = await clubs.GetMatchAsync(query.MatchId, cancellationToken);
        return match is null ? OperationFailure.NotFound() : match;
    }
}
