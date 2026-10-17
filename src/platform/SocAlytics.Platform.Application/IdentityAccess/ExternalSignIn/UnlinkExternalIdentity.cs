using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;

public sealed record UnlinkExternalIdentityCommand(Guid MemberId, string ProviderId, long ExpectedVersion);

public sealed class UnlinkExternalIdentityHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberAccountStore members,
    IExternalIdentityStore links,
    ISessionStore sessions,
    IAuditTrail audit,
    TimeProvider time)
{
    private const string EndReason = "ended-by-admin";

    public async Task<OperationResult<bool>> HandleAsync(UnlinkExternalIdentityCommand command, CancellationToken cancellationToken)
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

        var link = (await links.ListByAccountAsync(command.MemberId, cancellationToken))
            .FirstOrDefault(l => string.Equals(l.ProviderId, command.ProviderId, StringComparison.Ordinal));
        if (link is null)
        {
            return OperationFailure.NotFound();
        }

        var member = await members.GetMemberAsync(command.MemberId, cancellationToken);
        if (member!.Version != command.ExpectedVersion)
        {
            return OperationFailure.VersionMismatch(member.Version);
        }

        if (!await links.DeleteAsync(link.Id, link.Version, cancellationToken))
        {
            return OperationFailure.NotFound();
        }

        await members.RotateSecurityStampAsync(command.MemberId, cancellationToken);
        await sessions.EndAllForAccountAsync(command.MemberId, EndReason, null, time.GetUtcNow(), cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "identity.unlinked",
                Action = "unlink-identity",
                Outcome = AuditOutcome.Succeeded,
                Resource = resource,
                Details = new Dictionary<string, string> { ["provider"] = command.ProviderId },
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }
}
