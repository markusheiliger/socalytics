using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Analysis.Facts;

public sealed record ListRunFactsQuery(
    Guid RunId,
    string? CapabilityId,
    string? CapabilityVersion,
    string? SchemaVersion,
    string? NodeKey,
    string? FromMs,
    string? ToMs,
    string? PageSize,
    string? ContinuationToken);

public sealed record RunFactsPage(
    FactRunRecord Run,
    FactNodeRecord Stream,
    long? FromMs,
    long? ToMs,
    IReadOnlyList<FactItemRecord> Items,
    IReadOnlyList<FactCoverageWindow> Coverage,
    string? ContinuationToken,
    string ETagBasis);

public sealed class ListRunFactsHandler(IUnitOfWork unitOfWork, IRunFactReader reader, IAccessAuthorizer authorizer)
{
    public const string CapabilityNotInRun = "analysis-capability-not-in-run";
    public const string CapabilityAmbiguous = "analysis-capability-ambiguous";

    public async Task<OperationResult<RunFactsPage>> HandleAsync(ListRunFactsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var selection = reader.Validate(query);
        if (!selection.IsSuccess)
        {
            return selection.Failure;
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("analysis-run", query.RunId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        return await reader.ReadAsync(new AuthorizedRunScope(query.RunId), selection.Value, cancellationToken);
    }
}
