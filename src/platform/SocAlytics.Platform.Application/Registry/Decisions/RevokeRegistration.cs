using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Decisions;

public sealed class RevokeRegistrationHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IRegistrationStore store, IManagerAccessTokenStore tokens,
    IManagerStatusStore statuses, FenceManagerAttempts fence, IQueueCredentialStore credentials,
    IAuditTrail audit, IRequestContext context, TimeProvider time)
{
    public async Task<OperationResult<AnalystManagerRegistration>> HandleAsync(
        Guid registrationId, string? reason, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.ResourceType, registrationId.ToString()), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        if (AnalystManagerRegistration.ValidateReason(reason) is not null)
        {
            return OperationFailure.Validation(new FieldViolation("reason", "Required (1–500 chars)."));
        }

        var registration = await store.FindByIdAsync(registrationId, cancellationToken);
        if (registration is null)
        {
            return OperationFailure.NotFound();
        }

        var from = registration.State;
        var result = registration.Revoke(context.MemberAccountId ?? Guid.Empty, reason, time.GetUtcNow());
        if (!result.Succeeded)
        {
            return OperationFailure.Conflict(RegistrationTransitionResult.InvalidStateTransition);
        }

        var write = await store.UpdateAsync(registration, from, false, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return OperationFailure.Conflict(RegistrationTransitionResult.InvalidStateTransition);
        }

        await tokens.DeleteForRegistrationAsync(registration.Id, cancellationToken);
        await statuses.DeleteAsync(registration.Id, cancellationToken);
        var fenced = registration.ManagerId is { } managerId ? await fence.HandleAsync(managerId, cancellationToken) : 0;
        var revoked = await credentials.MarkRevokedAsync(registration.Id, time.GetUtcNow(), cancellationToken);
        await audit.RecordAsync(RegistryAuditEvents.Revoked(registration.Id, new Dictionary<string, string>
        {
            ["fromState"] = from.ToString(),
            ["toState"] = nameof(RegistrationState.Revoked),
            ["fencedAttempts"] = fenced.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["revokedCredentials"] = revoked.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return registration.WithVersion(write.Version ?? registration.Version);
    }
}
