using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class ArchiveSeasonHandler(
    IUnitOfWork unitOfWork,
    IClubHierarchyStore clubs,
    IAccessAuthorizer authorizer,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<Season>> HandleAsync(ArchiveSeasonCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer,
            new AuditResource("season", command.SeasonId.ToString()),
            cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var season = await clubs.GetSeasonAsync(command.SeasonId, cancellationToken);
        if (season is null)
        {
            return OperationFailure.NotFound();
        }

        if (!season.CanArchive)
        {
            return OperationFailure.Conflict("invalid-state-transition");
        }

        var write = await clubs.TransitionSeasonAsync(season.Id, SeasonState.Active, SeasonState.Archived, time.GetUtcNow(), cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return write.Outcome == VersionedWriteOutcome.NotFound
                ? OperationFailure.NotFound()
                : OperationFailure.Conflict("invalid-state-transition");
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "season.archived",
                Action = "archive",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("season", season.Id.ToString()),
                Details = new Dictionary<string, string> { ["fromState"] = "active", ["toState"] = "archived" },
            },
            cancellationToken);
        var updated = await clubs.GetSeasonAsync(season.Id, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return updated!;
    }
}
