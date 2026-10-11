using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Analysis.Queries;

public sealed record GetAnalysisRunQuery(Guid RunId);

public sealed class GetAnalysisRunHandler(IUnitOfWork unitOfWork, IAnalysisRunQueries queries, IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<AnalysisRunStatusView>> HandleAsync(GetAnalysisRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("analysis-run", query.RunId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var status = await queries.GetStatusAsync(query.RunId, cancellationToken);
        return status is null ? OperationFailure.NotFound() : status;
    }
}
