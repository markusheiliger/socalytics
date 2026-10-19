using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.Email;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.Invitations;

public sealed record AcceptInvitationCommand(string? Token, string? Password);

public sealed class AcceptInvitationHandler(
    IUnitOfWork unitOfWork,
    IMemberInvitationStore invitations,
    IAccountCredentialService credentials,
    IMemberEmailAddressStore addresses,
    IMemberAccountStore members,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<bool>> HandleAsync(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var found = await new InvitationTokenValidator(invitations, time).ValidateAsync(command.Token, cancellationToken);
        if (found is null)
        {
            return await RefuseAsync(InvitationAuditEvents.ReasonInvitationInvalid, cancellationToken);
        }

        var stored = found.Invitation;
        var invitation = stored.Invitation;
        var passwordViolation = string.IsNullOrEmpty(command.Password)
            ? new FieldViolation("password", "required")
            : (await credentials.ValidatePasswordAsync(command.Password, cancellationToken)).Count > 0
                ? new FieldViolation("password", "password-policy")
                : null;
        if (passwordViolation is not null)
        {
            await RefuseAsync(InvitationAuditEvents.ReasonPasswordPolicy, cancellationToken);
            return OperationFailure.Validation(passwordViolation);
        }

        var now = time.GetUtcNow();
        var masked = EmailAddress.TryCreate(invitation.Address, out var address, out _) ? address.Masked : "***";
        Guid memberId;
        try
        {
            memberId = await credentials.CreateAccountAsync(invitation.AccountName, command.Password, false, invitation.InvitedBy, cancellationToken);
        }
        catch (UniqueViolationException)
        {
            return await RefuseAsync(InvitationAuditEvents.ReasonInvitationInvalid, cancellationToken);
        }

        if (!await addresses.InsertAsync(memberId, invitation.Address, stored.AddressNormalized, invitation.InvitedBy, now, cancellationToken))
        {
            return await RefuseAsync(InvitationAuditEvents.ReasonInvitationInvalid, cancellationToken);
        }

        var inserted = await addresses.FindAsync(memberId, cancellationToken);
        var verified = await addresses.MarkVerifiedAsync(memberId, now, inserted!.Version, cancellationToken);
        if (verified.Outcome != VersionedWriteOutcome.Applied)
        {
            return await RefuseAsync(InvitationAuditEvents.ReasonInvitationInvalid, cancellationToken);
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "member.created",
                Action = "create",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("member", memberId.ToString()),
            },
            cancellationToken);

        foreach (var role in stored.TeamRoles)
        {
            await members.UpsertTeamRoleAsync(memberId, role.TeamId, role.Role, invitation.InvitedBy, cancellationToken);
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = "team-role.assigned",
                    Action = "assign-team-role",
                    Outcome = AuditOutcome.Succeeded,
                    Resource = new AuditResource("member", memberId.ToString()),
                    TeamId = role.TeamId,
                    Details = new Dictionary<string, string> { ["role"] = role.Role.ToWireValue() },
                },
                cancellationToken);
        }

        if (!await invitations.MarkLinkUsedAsync(found.Link.Id, now, cancellationToken))
        {
            return await RefuseAsync(InvitationAuditEvents.ReasonInvitationInvalid, cancellationToken);
        }

        var accepted = await invitations.MarkAcceptedAsync(invitation.Id, memberId, now, invitation.Version, cancellationToken);
        if (accepted.Outcome != VersionedWriteOutcome.Applied)
        {
            return await RefuseAsync(InvitationAuditEvents.ReasonInvitationInvalid, cancellationToken);
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = InvitationAuditEvents.Accepted,
                Action = "accept",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("invitation", invitation.Id.ToString()),
                Details = new Dictionary<string, string>
                {
                    ["memberId"] = memberId.ToString(),
                    ["maskedAddress"] = masked,
                },
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }

    // The refusal is recorded in its own transaction; the unit of work ends uncommitted, so no partial acceptance persists and the link stays usable.
    private async Task<OperationFailure> RefuseAsync(string reason, CancellationToken cancellationToken)
    {
        await audit.RecordIndependentAsync(
            new AuditEvent
            {
                EventType = InvitationAuditEvents.AcceptanceRefused,
                Action = "accept",
                Outcome = AuditOutcome.Failed,
                Resource = new AuditResource("invitation", null),
                ReasonCode = reason,
            },
            cancellationToken);
        return reason == InvitationAuditEvents.ReasonInvitationInvalid
            ? InvitationTokenValidator.Invalid()
            : OperationFailure.Validation(new FieldViolation("password", reason));
    }
}
