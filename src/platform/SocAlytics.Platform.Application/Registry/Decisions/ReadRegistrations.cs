using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Decisions;

public sealed class GetRegistrationHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IRegistrationStore store)
{
    public async Task<OperationResult<AnalystManagerRegistration>> HandleAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.ResourceType, registrationId.ToString()), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var registration = await store.FindByIdAsync(registrationId, cancellationToken);
        return registration is null ? OperationFailure.NotFound() : registration;
    }
}

public sealed class ListRegistrationsHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IRegistrationStore store)
{
    public const int PageSize = 50;

    public async Task<OperationResult<RegistrationPage>> HandleAsync(
        string? state, string? continuationToken, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.ResourceType, null), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        RegistrationState? filter = null;
        if (state is not null)
        {
            if (!RegistrationStateExtensions.TryParseApiName(state, out var parsed))
            {
                return OperationFailure.Validation(new FieldViolation("state", "invalid-value"));
            }

            filter = parsed;
        }

        try
        {
            return await store.ListAsync(filter, PageSize, continuationToken, cancellationToken);
        }
        catch (ArgumentException)
        {
            return OperationFailure.Validation(new FieldViolation("continuationToken", "invalid"));
        }
    }
}
