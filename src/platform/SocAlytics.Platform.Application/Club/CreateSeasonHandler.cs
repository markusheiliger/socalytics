using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class CreateSeasonHandler(
    IUnitOfWork unitOfWork,
    IClubHierarchyStore clubs,
    IAccessAuthorizer authorizer,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<Season>> HandleAsync(CreateSeasonCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, new AuditResource("season", null), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        if (!DisplayName.TryCreate(command.Name, out var name, out var error))
        {
            return OperationFailure.Validation(new FieldViolation("name", error));
        }

        var season = new Season(Guid.CreateVersion7(), name, SeasonState.Draft, time.GetUtcNow(), null, null, 1);
        await clubs.InsertSeasonAsync(season, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "season.created",
                Action = "create",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("season", season.Id.ToString()),
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return season;
    }
}
