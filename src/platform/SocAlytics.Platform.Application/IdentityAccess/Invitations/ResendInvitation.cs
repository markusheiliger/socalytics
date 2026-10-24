using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.Invitations;

public sealed record ResendInvitationCommand(Guid InvitationId, long ExpectedVersion);

/// <param name="Invitation">The invitation with its new link; <c>null</c> when the resend limit refused the request.</param>
/// <param name="RetryAfter">How long to wait before the next resend; set only when the resend limit refused the request.</param>
public sealed record ResendInvitationResult(StoredMemberInvitation? Invitation, TimeSpan? RetryAfter);

public sealed class ResendInvitationHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberInvitationStore invitations,
    IEmailOutbox outbox,
    IAuditTrail audit,
    IOptions<InvitationOptions> options,
    TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public async Task<OperationResult<ResendInvitationResult>> HandleAsync(ResendInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource("invitation", command.InvitationId.ToString()), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var stored = await invitations.FindForUpdateAsync(command.InvitationId, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.NotFound();
        }

        var invitation = stored.Invitation;
        var masked = MaskedAddress(invitation.Address);
        if (invitation.Version != command.ExpectedVersion)
        {
            await RecordAsync(invitation.Id, masked, AuditOutcome.Refused, InvitationAuditEvents.ReasonVersionMismatch, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.VersionMismatch(invitation.Version);
        }

        if (!invitation.CanResend())
        {
            await RecordAsync(invitation.Id, masked, AuditOutcome.Refused, InvitationAuditEvents.ReasonInvitationNotOpen, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Conflict("invitation-not-open");
        }

        var now = time.GetUtcNow();
        var recent = await invitations.ListResendLinkTimesSinceAsync(invitation.Id, now - Window, cancellationToken);
        if (recent.Count >= options.Value.MaxResendsPerHour)
        {
            await RecordAsync(invitation.Id, masked, AuditOutcome.Refused, InvitationAuditEvents.ReasonRateLimited, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            // The oldest counted link leaves the window first, which frees one resend.
            var wait = recent[recent.Count - options.Value.MaxResendsPerHour] + Window - now;
            return new ResendInvitationResult(null, wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1));
        }

        await invitations.SupersedeOpenLinksAsync(invitation.Id, cancellationToken);
        var linkId = Guid.NewGuid();
        await invitations.CreatePendingLinkAsync(linkId, invitation.Id, now, cancellationToken);
        await outbox.QueueMemberInvitationAsync(stored.AddressHash, linkId, cancellationToken);
        var written = await invitations.MarkResentAsync(invitation.Id, command.ExpectedVersion, cancellationToken);
        if (written.Outcome != VersionedWriteOutcome.Applied)
        {
            return written.Outcome == VersionedWriteOutcome.NotFound
                ? OperationFailure.NotFound()
                : OperationFailure.VersionMismatch(written.Version);
        }

        await RecordAsync(invitation.Id, masked, AuditOutcome.Succeeded, null, cancellationToken);
        var resent = await invitations.FindForUpdateAsync(invitation.Id, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new ResendInvitationResult(resent, null);
    }

    internal static string MaskedAddress(string address) =>
        EmailAddress.TryCreate(address, out var parsed, out _) ? parsed.Masked : "***";

    private Task RecordAsync(Guid invitationId, string maskedAddress, AuditOutcome outcome, string? reason, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new AuditEvent
            {
                EventType = InvitationAuditEvents.Resent,
                Action = "resend",
                Outcome = outcome,
                Resource = new AuditResource("invitation", invitationId.ToString()),
                ReasonCode = reason,
                Details = new Dictionary<string, string> { ["maskedAddress"] = maskedAddress },
            },
            cancellationToken);
}
