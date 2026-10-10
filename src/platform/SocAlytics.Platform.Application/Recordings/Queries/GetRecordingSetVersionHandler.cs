using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Recordings.Queries;

public sealed record GetRecordingSetVersionQuery(Guid MatchId, Guid RecordingSetVersionId);

public sealed class GetRecordingSetVersionHandler(
    IUnitOfWork unitOfWork,
    IRecordingLineageQueries lineage,
    IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<RecordingSetVersionView>> HandleAsync(
        GetRecordingSetVersionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", query.MatchId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var set = await lineage.GetSetVersionAsync(query.MatchId, query.RecordingSetVersionId, cancellationToken);
        return set is null ? OperationFailure.NotFound() : set;
    }
}
