using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Recordings.Queries;

public sealed record GetMatchRecordingLineageQuery(Guid MatchId);

public sealed class GetMatchRecordingLineageHandler(
    IUnitOfWork unitOfWork,
    IRecordingLineageQueries lineage,
    IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<MatchRecordingLineageView>> HandleAsync(
        GetMatchRecordingLineageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", query.MatchId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        return await lineage.GetMatchLineageAsync(query.MatchId, decision.Scope!.TeamId, cancellationToken);
    }
}
