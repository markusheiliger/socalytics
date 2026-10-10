using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class AssignTeamRoleHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberAccountStore members,
    IRequestContext requestContext,
    IAuditTrail audit)
{
    public async Task<OperationResult<MemberDetails>> HandleAsync(AssignTeamRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var resource = new AuditResource("member", command.MemberId.ToString());
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, resource, cancellationToken);
        if (!decision.IsGranted || requestContext.MemberAccountId is not { } actor)
        {
            return OperationFailure.Forbidden();
        }

        var account = await members.LockAccountAsync(command.MemberId, cancellationToken);
        if (account is null || !await members.TeamExistsAsync(command.TeamId, cancellationToken))
        {
            return OperationFailure.NotFound();
        }

        if (account.Status != MembershipStatus.Active)
        {
            return OperationFailure.Conflict("membership-inactive");
        }

        var change = await members.UpsertTeamRoleAsync(command.MemberId, command.TeamId, command.Role, actor, cancellationToken);
        var details = new Dictionary<string, string> { ["role"] = command.Role.ToWireValue() };
        if (change.Previous is { } previous)
        {
            details["previousRole"] = previous.ToWireValue();
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "team-role.assigned",
                Action = "assign-team-role",
                Outcome = change.Changed ? AuditOutcome.Succeeded : AuditOutcome.Unchanged,
                Resource = resource,
                TeamId = command.TeamId,
                Details = details,
            },
            cancellationToken);

        var member = await members.GetMemberAsync(command.MemberId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return member!;
    }
}
