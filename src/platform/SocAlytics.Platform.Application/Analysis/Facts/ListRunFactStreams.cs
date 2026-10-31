using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Analysis.Facts;

public sealed record ListRunFactStreamsQuery(Guid RunId);

public sealed record FactStreamCoverageCounts(int Accepted, int Missing, int Failed, int Blocked, int Skipped);

public sealed record FactStreamEntry(
    FactNodeRecord Node, long FactCount, FactStreamCoverageCounts Coverage);

public sealed record RunFactStreams(FactRunRecord Run, IReadOnlyList<FactStreamEntry> Streams, string ETagBasis);

public sealed class ListRunFactStreamsHandler(IUnitOfWork unitOfWork, IRunFactStreamReader reader, IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<RunFactStreams>> HandleAsync(ListRunFactStreamsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("analysis-run", query.RunId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        return await reader.ReadAsync(new AuthorizedRunScope(query.RunId), cancellationToken);
    }
}
