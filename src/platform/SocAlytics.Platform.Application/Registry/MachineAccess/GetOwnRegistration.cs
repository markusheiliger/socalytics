using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.MachineAccess;

public sealed record OwnRegistration(
    string ManagerId, string StampId, string State, string ClubName, string KeyThumbprint, DateTimeOffset ActivatedAt);

public sealed class GetOwnRegistrationHandler(IUnitOfWork unitOfWork, IRegistrationStore registrations, IClubHierarchyStore clubs)
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

        return new OwnRegistration(
            registration.ManagerId.Value.ToString(), registration.StampId,
            registration.State == RegistrationState.Active ? "active" : "unregistering",
            club.DisplayName.Value, registration.KeyThumbprint, registration.ActivatedAt.Value);
    }
}
