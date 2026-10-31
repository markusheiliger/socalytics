using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Analysis.Evidence;

// Member entry point: authorizes the run, resolves through the shared core, and records the member grant audit event.
public sealed class ResolveEvidenceMediaHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IEvidenceMediaResolver resolver)
{
    public const string StorageUnavailableCode = "evidence-storage-unavailable";

    public async Task<OperationResult<EvidenceResolution>> HandleAsync(
        ResolveEvidenceMediaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var violations = resolver.Validate(command);
        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        EvidenceMediaResolved resolved;
        try
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var decision = await authorizer.AuthorizeTeamResourceAsync(
                new(EvidenceAuditEvents.RunResourceType, command.RunId), TeamPermission.Read, cancellationToken);
            if (!decision.IsGranted)
            {
                return OperationFailure.NotFound();
            }

            var result = await resolver.ResolveAsync(new EvidenceRunScope(command.RunId), command, null, cancellationToken);
            if (!result.IsSuccess)
            {
                return result.Failure;
            }

            resolved = result.Value;
            await scope.CommitAsync(cancellationToken);
        }
        catch (ObjectStorageUnavailableException)
        {
            return OperationFailure.Unavailable(StorageUnavailableCode);
        }

        await resolver.InvalidateAsync(resolved.Invalidated, cancellationToken);
        return resolved.Resolution;
    }
}
