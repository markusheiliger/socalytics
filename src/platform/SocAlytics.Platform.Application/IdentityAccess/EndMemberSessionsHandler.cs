using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class EndMemberSessionsHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberAccountStore members,
    ISessionStore sessions,
    IAuditTrail audit,
    TimeProvider time)
{
    private const string EndReason = "ended-by-admin";

    public async Task<OperationResult<bool>> HandleAsync(EndMemberSessionsCommand command, CancellationToken cancellationToken)
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

        await members.RotateSecurityStampAsync(command.MemberId, cancellationToken);
        await sessions.EndAllForAccountAsync(command.MemberId, EndReason, null, time.GetUtcNow(), cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "sessions.ended",
                Action = "end-member-sessions",
                Outcome = AuditOutcome.Succeeded,
                Resource = resource,
                Details = new Dictionary<string, string> { ["endReason"] = EndReason },
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }
}
