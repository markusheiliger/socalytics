using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class ReactivateMemberHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberAccountStore members,
    IAuditTrail audit)
{
    public async Task<OperationResult<MemberDetails>> HandleAsync(ReactivateMemberCommand command, CancellationToken cancellationToken)
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

        if (account.Status != MembershipStatus.Deactivated)
        {
            return OperationFailure.Conflict("invalid-state-transition");
        }

        await members.ReactivateAccountAsync(command.MemberId, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "member.reactivated",
                Action = "reactivate-member",
                Outcome = AuditOutcome.Succeeded,
                Resource = resource,
            },
            cancellationToken);
        var member = await members.GetMemberAsync(command.MemberId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return member!;
    }
}
