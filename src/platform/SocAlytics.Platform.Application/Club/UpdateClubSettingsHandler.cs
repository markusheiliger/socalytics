using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using ClubEntity = SocAlytics.Platform.Domain.Club.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class UpdateClubSettingsHandler(
    IUnitOfWork unitOfWork,
    IClubHierarchyStore clubs,
    IAccessAuthorizer authorizer,
    IAuditTrail audit)
{
    public async Task<OperationResult<ClubEntity>> HandleAsync(UpdateClubSettingsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        if (!DisplayName.TryCreate(command.DisplayName, out var name, out var error))
        {
            return OperationFailure.Validation(new FieldViolation("displayName", error));
        }

        var club = await clubs.GetClubAsync(cancellationToken);
        if (club is null)
        {
            return OperationFailure.NotFound();
        }

        var write = await clubs.UpdateClubDisplayNameAsync(name, command.ExpectedVersion, cancellationToken);
        switch (write.Outcome)
        {
            case VersionedWriteOutcome.ConcurrencyConflict:
                return OperationFailure.VersionMismatch(write.Version);
            case VersionedWriteOutcome.NotFound:
                return OperationFailure.NotFound();
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "club.settings-updated",
                Action = "update",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("club", club.Id.ToString()),
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return club with { DisplayName = name, Version = write.Version!.Value };
    }
}
