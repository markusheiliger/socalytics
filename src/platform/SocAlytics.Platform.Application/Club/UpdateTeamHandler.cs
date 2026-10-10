using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class UpdateTeamHandler(
    IUnitOfWork unitOfWork,
    IClubHierarchyStore clubs,
    IAccessAuthorizer authorizer,
    IAuditTrail audit)
{
    public async Task<OperationResult<TeamView>> HandleAsync(UpdateTeamCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var violations = new List<FieldViolation>();
        if (command.SeasonIdSupplied)
        {
            violations.Add(new FieldViolation("seasonId", "immutable"));
        }

        DisplayName.TryCreate(command.Name, out var name, out var error);
        if (error is not null)
        {
            violations.Add(new FieldViolation("name", error));
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer,
            new AuditResource("team", command.TeamId.ToString()),
            cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var existing = await clubs.GetTeamAsync(command.TeamId, cancellationToken);
        if (existing is null)
        {
            return OperationFailure.NotFound();
        }

        var seasonState = await clubs.LockSeasonForShareAsync(existing.Team.SeasonId, cancellationToken);
        if (seasonState == SeasonState.Archived)
        {
            return OperationFailure.Conflict("season-archived");
        }

        var write = await clubs.UpdateTeamNameAsync(command.TeamId, name, command.ExpectedVersion, cancellationToken);
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
                EventType = "team.updated",
                Action = "update",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("team", command.TeamId.ToString()),
                TeamId = command.TeamId,
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new TeamView(existing.Team with { Name = name, Version = write.Version!.Value }, existing.SeasonState);
    }
}
