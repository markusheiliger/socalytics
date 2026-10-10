using System.Globalization;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class UpdateMatchHandler(
    IUnitOfWork unitOfWork,
    IClubHierarchyStore clubs,
    IAccessAuthorizer authorizer,
    IAuditTrail audit)
{
    public async Task<OperationResult<Match>> HandleAsync(UpdateMatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var violations = new List<FieldViolation>();
        foreach (var field in command.ImmutableFieldsSupplied)
        {
            violations.Add(new FieldViolation(field, "immutable"));
        }

        DateTimeOffset? kickoff = null;
        var kickoffInvalid = false;
        if (command.KickoffAt is not null)
        {
            if (DateTimeOffset.TryParse(command.KickoffAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                kickoff = parsed;
            }
            else
            {
                kickoffInvalid = true;
            }
        }

        var homeAwayValid = HomeAwayWire.TryParse(command.HomeAway, out var homeAway);
        MatchDetails? details = null;
        if (kickoffInvalid)
        {
            violations.Add(new FieldViolation("kickoffAt", "invalid"));
        }
        else if (!MatchDetails.TryCreate(kickoff, homeAwayValid ? homeAway : (HomeAway)(-1), command.Competition, out details, out var field, out var code))
        {
            violations.Add(new FieldViolation(field, code));
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", command.MatchId), TeamPermission.Write, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.Kind == AccessDecisionKind.Forbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var seasonState = await clubs.LockSeasonForShareAsync(decision.Scope!.SeasonId, cancellationToken);
        if (seasonState is null)
        {
            return OperationFailure.NotFound();
        }

        if (seasonState == SeasonState.Archived)
        {
            return OperationFailure.Conflict("season-archived");
        }

        var existing = await clubs.GetMatchAsync(command.MatchId, cancellationToken);
        if (existing is null)
        {
            return OperationFailure.NotFound();
        }

        var write = await clubs.UpdateMatchDetailsAsync(command.MatchId, details!, command.ExpectedVersion, cancellationToken);
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
                EventType = "match.updated",
                Action = "update",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("match", command.MatchId.ToString()),
                TeamId = existing.TeamId,
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return existing with { Details = details!, Version = write.Version!.Value };
    }
}
