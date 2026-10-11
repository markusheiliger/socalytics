using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.MachineAccess;

public sealed class BeginUnregistrationHandler(
    IUnitOfWork unitOfWork, IRegistrationStore registrations, IClubHierarchyStore clubs, IAuditTrail audit, TimeProvider time)
{
    public async Task<OperationResult<OwnRegistration>> HandleAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var registration = await registrations.FindByIdAsync(registrationId, cancellationToken);
        var club = await clubs.GetClubAsync(cancellationToken);
        if (registration?.ManagerId is null || registration.ActivatedAt is null || club is null
            || registration.State is not (RegistrationState.Active or RegistrationState.Unregistering))
        {
            return OperationFailure.Unauthenticated();
        }

        var from = registration.State;
        if (from == RegistrationState.Active)
        {
            if (!registration.BeginUnregistration(time.GetUtcNow()).Succeeded)
            {
                return OperationFailure.Conflict(RegistrationTransitionResult.InvalidStateTransition);
            }

            var write = await registrations.UpdateAsync(registration, from, false, cancellationToken);
            if (write.Outcome != VersionedWriteOutcome.Applied)
            {
                return OperationFailure.Conflict(RegistrationTransitionResult.InvalidStateTransition);
            }

            await audit.RecordAsync(RegistryAuditEvents.UnregistrationStarted(registration.Id, new Dictionary<string, string>
            {
                ["fromState"] = nameof(RegistrationState.Active),
                ["toState"] = nameof(RegistrationState.Unregistering),
            }), cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        return new OwnRegistration(
            registration.ManagerId.Value.ToString(), registration.StampId, "unregistering",
            club.DisplayName.Value, registration.KeyThumbprint, registration.ActivatedAt.Value);
    }
}
