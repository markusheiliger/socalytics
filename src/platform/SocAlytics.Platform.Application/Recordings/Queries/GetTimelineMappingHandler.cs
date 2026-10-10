using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Recordings.Queries;

public sealed record GetTimelineMappingQuery(Guid MatchId, Guid TimelineMappingId);

public sealed class GetTimelineMappingHandler(
    IUnitOfWork unitOfWork,
    IRecordingLineageQueries lineage,
    IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<StoredTimelineMapping>> HandleAsync(
        GetTimelineMappingQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", query.MatchId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var mapping = await lineage.GetTimelineMappingAsync(query.MatchId, query.TimelineMappingId, cancellationToken);
        return mapping is null ? OperationFailure.NotFound() : mapping;
    }
}
