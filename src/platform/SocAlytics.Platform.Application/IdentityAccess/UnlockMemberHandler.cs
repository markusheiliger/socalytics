using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class UnlockMemberHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberAccountStore members,
    IAuditTrail audit)
{
    public async Task<OperationResult<MemberDetails>> HandleAsync(UnlockMemberCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var resource = new AuditResource("member", command.MemberId.ToString());
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, resource, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        if (await members.LockAccountAsync(command.MemberId, cancellationToken) is null)
        {
            return OperationFailure.NotFound();
        }

        var wasLocked = await members.UnlockAccountAsync(command.MemberId, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "account.unlocked",
                Action = "unlock-member",
                Outcome = wasLocked ? AuditOutcome.Succeeded : AuditOutcome.Unchanged,
                Resource = resource,
            },
            cancellationToken);
        var member = await members.GetMemberAsync(command.MemberId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return member!;
    }
}
