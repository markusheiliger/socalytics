using System.Globalization;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.Club;

public sealed class CreateMatchHandler(
    IUnitOfWork unitOfWork,
    IClubHierarchyStore clubs,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<Match>> HandleAsync(CreateMatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("team", command.TeamId), TeamPermission.Write, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.Kind == AccessDecisionKind.Forbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        var violations = new List<FieldViolation>();
        if (!DisplayName.TryCreate(command.OpponentName, out var opponent, out var nameError))
        {
            violations.Add(new FieldViolation("opponent.name", nameError));
        }

        DateTimeOffset? kickoff = null;
        if (command.KickoffAt is not null)
        {
            kickoff = DateTimeOffset.TryParse(command.KickoffAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue.AddTicks(1);
        }

        var homeAwayValid = HomeAwayWire.TryParse(command.HomeAway, out var homeAway);
        MatchDetails? details = null;
        if (kickoff == DateTimeOffset.MinValue.AddTicks(1))
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

        var teamScope = decision.Scope!;
        var seasonState = await clubs.LockSeasonForShareAsync(teamScope.SeasonId, cancellationToken);
        if (seasonState is null)
        {
            return OperationFailure.NotFound();
        }

        if (seasonState == SeasonState.Archived)
        {
            return OperationFailure.Conflict("season-archived");
        }

        var match = new Match(
            Guid.CreateVersion7(),
            command.TeamId,
            new MatchOpponent(opponent!),
            details!,
            time.GetUtcNow(),
            requestContext.MemberAccountId!.Value,
            1);
        await clubs.InsertMatchAsync(match, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "match.created",
                Action = "create",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("match", match.Id.ToString()),
                TeamId = match.TeamId,
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return match;
    }
}
