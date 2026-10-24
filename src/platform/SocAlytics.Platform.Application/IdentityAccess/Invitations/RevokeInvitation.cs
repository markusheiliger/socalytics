using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;

namespace SocAlytics.Platform.Application.IdentityAccess.Invitations;

public sealed record RevokeInvitationCommand(Guid InvitationId, long ExpectedVersion);

public sealed class RevokeInvitationHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IMemberInvitationStore invitations,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<StoredMemberInvitation>> HandleAsync(RevokeInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource("invitation", command.InvitationId.ToString()), cancellationToken);
        if (!decision.IsGranted || requestContext.MemberAccountId is not { } callerId)
        {
            return OperationFailure.Forbidden();
        }

        var stored = await invitations.FindForUpdateAsync(command.InvitationId, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.NotFound();
        }

        var invitation = stored.Invitation;
        var masked = ResendInvitationHandler.MaskedAddress(invitation.Address);
        if (invitation.Version != command.ExpectedVersion)
        {
            await RecordAsync(invitation.Id, masked, AuditOutcome.Refused, InvitationAuditEvents.ReasonVersionMismatch, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.VersionMismatch(invitation.Version);
        }

        if (!invitation.CanRevoke())
        {
            await RecordAsync(invitation.Id, masked, AuditOutcome.Refused, InvitationAuditEvents.ReasonInvitationNotOpen, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Conflict("invitation-not-open");
        }

        var written = await invitations.MarkRevokedAsync(invitation.Id, callerId, time.GetUtcNow(), command.ExpectedVersion, cancellationToken);
        if (written.Outcome != VersionedWriteOutcome.Applied)
        {
            return written.Outcome == VersionedWriteOutcome.NotFound
                ? OperationFailure.NotFound()
                : OperationFailure.VersionMismatch(written.Version);
        }

        await invitations.SupersedeOpenLinksAsync(invitation.Id, cancellationToken);
        await RecordAsync(invitation.Id, masked, AuditOutcome.Succeeded, null, cancellationToken);
        var revoked = await invitations.FindForUpdateAsync(invitation.Id, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return revoked!;
    }

    private Task RecordAsync(Guid invitationId, string maskedAddress, AuditOutcome outcome, string? reason, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new AuditEvent
            {
                EventType = InvitationAuditEvents.Revoked,
                Action = "revoke",
                Outcome = outcome,
                Resource = new AuditResource("invitation", invitationId.ToString()),
                ReasonCode = reason,
                Details = new Dictionary<string, string> { ["maskedAddress"] = maskedAddress },
            },
            cancellationToken);
}
