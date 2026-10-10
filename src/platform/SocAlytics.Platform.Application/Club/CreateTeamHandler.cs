using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class CreateTeamHandler(
    IUnitOfWork unitOfWork,
    IClubHierarchyStore clubs,
    IAccessAuthorizer authorizer,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<TeamView>> HandleAsync(CreateTeamCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, new AuditResource("team", null), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        if (!DisplayName.TryCreate(command.Name, out var name, out var error))
        {
            return OperationFailure.Validation(new FieldViolation("name", error));
        }

        var seasonState = await clubs.LockSeasonForShareAsync(command.SeasonId, cancellationToken);
        if (seasonState is null)
        {
            return OperationFailure.NotFound();
        }

        if (seasonState == SeasonState.Archived)
        {
            return OperationFailure.Conflict("season-archived");
        }

        var team = new Team(Guid.CreateVersion7(), command.SeasonId, name, time.GetUtcNow(), 1);
        await clubs.InsertTeamAsync(team, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "team.created",
                Action = "create",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("team", team.Id.ToString()),
                TeamId = team.Id,
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new TeamView(team, seasonState.Value);
    }
}
