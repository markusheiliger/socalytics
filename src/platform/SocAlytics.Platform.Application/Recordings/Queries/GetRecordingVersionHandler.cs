using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings.Queries;

public sealed record GetRecordingVersionQuery(Guid MatchId, Guid RecordingVersionId);

public sealed class GetRecordingVersionHandler(
    IUnitOfWork unitOfWork,
    IRecordingLineageQueries lineage,
    IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<RecordingVersion>> HandleAsync(
        GetRecordingVersionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", query.MatchId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var version = await lineage.GetRecordingVersionAsync(query.MatchId, query.RecordingVersionId, cancellationToken);
        return version is null ? OperationFailure.NotFound() : version;
    }
}
