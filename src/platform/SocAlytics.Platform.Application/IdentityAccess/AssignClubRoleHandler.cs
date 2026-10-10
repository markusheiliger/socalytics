using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class AssignClubRoleHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberAccountStore members,
    IRequestContext requestContext,
    IAuditTrail audit)
{
    public async Task<OperationResult<MemberDetails>> HandleAsync(AssignClubRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var resource = new AuditResource("member", command.MemberId.ToString());
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, resource, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var account = await members.LockAccountAsync(command.MemberId, cancellationToken);
        if (account is null)
        {
            return OperationFailure.NotFound();
        }

        if (account.Status != MembershipStatus.Active)
        {
            return OperationFailure.Conflict("membership-inactive");
        }

        var outcome = AuditOutcome.Unchanged;
        if (!account.ClubRoles.Contains(command.Role))
        {
            await members.AssignClubRoleAsync(command.MemberId, command.Role, requestContext.MemberAccountId, cancellationToken);
            outcome = AuditOutcome.Succeeded;
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "club-role.assigned",
                Action = "assign-club-role",
                Outcome = outcome,
                Resource = resource,
                Details = new Dictionary<string, string> { ["role"] = command.Role.ToWireValue() },
            },
            cancellationToken);

        var member = await members.GetMemberAsync(command.MemberId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return member!;
    }
}
