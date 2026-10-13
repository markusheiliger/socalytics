using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.MachineAccess;

public sealed record UnregistrationResult(string ManagerId, string State);

public sealed class CompleteUnregistrationHandler(
    IUnitOfWork unitOfWork, IRegistrationStore registrations, IManagerAccessTokenStore tokens,
    IManagerStatusStore statuses, FenceManagerAttempts fence, IQueueCredentialStore credentials,
    IAuditTrail audit, TimeProvider time)
{
    public async Task<OperationResult<UnregistrationResult>> HandleAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var registration = await registrations.FindByIdAsync(registrationId, cancellationToken);
        if (registration?.ManagerId is null || registration.State is not (RegistrationState.Active or RegistrationState.Unregistering))
        {
            return OperationFailure.Unauthenticated();
        }

        var from = registration.State;
        if (!registration.CompleteUnregistration(time.GetUtcNow()).Succeeded)
        {
            return OperationFailure.Conflict(RegistrationTransitionResult.InvalidStateTransition);
        }

        var write = await registrations.UpdateAsync(registration, from, false, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return OperationFailure.Conflict(RegistrationTransitionResult.InvalidStateTransition);
        }

        await tokens.DeleteForRegistrationAsync(registration.Id, cancellationToken);
        await statuses.DeleteAsync(registration.Id, cancellationToken);
        var fenced = await fence.HandleAsync(registration.ManagerId.Value, cancellationToken);
        var revoked = await credentials.MarkRevokedAsync(registration.Id, time.GetUtcNow(), cancellationToken);
        await audit.RecordAsync(RegistryAuditEvents.Unregistered(registration.Id, new Dictionary<string, string>
        {
            ["fromState"] = nameof(RegistrationState.Unregistering),
            ["toState"] = nameof(RegistrationState.Unregistered),
            ["fencedAttempts"] = fenced.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["revokedCredentials"] = revoked.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new UnregistrationResult(registration.ManagerId.Value.ToString(), "unregistered");
    }
}
