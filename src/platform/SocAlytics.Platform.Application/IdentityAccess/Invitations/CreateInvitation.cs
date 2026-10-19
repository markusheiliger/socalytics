using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.Invitations;

public sealed record InvitationTeamRoleInput(Guid? TeamId, string? Role);

public sealed record CreateInvitationCommand(string? Address, string? AccountName, IReadOnlyList<InvitationTeamRoleInput>? TeamRoles);

public sealed class CreateInvitationHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IMemberInvitationStore invitations,
    IMemberAccountStore members,
    IEmailOutbox outbox,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<MemberInvitation>> HandleAsync(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, new AuditResource("invitation", null), cancellationToken);
        if (!decision.IsGranted || requestContext.MemberAccountId is not { } callerId)
        {
            return OperationFailure.Forbidden();
        }

        var violations = new List<FieldViolation>();
        EmailAddress.TryCreate(command.Address, out var address, out var addressError);
        if (address is null)
        {
            violations.Add(new FieldViolation("address", addressError == "required" ? "required" : "invalid"));
        }

        AccountName.TryCreate(command.AccountName, out var name, out var nameError);
        if (name is null)
        {
            violations.Add(new FieldViolation("accountName", nameError ?? "invalid"));
        }

        var roles = new List<InvitationTeamRoleRecord>();
        var seenTeams = new HashSet<Guid>();
        var input = command.TeamRoles ?? [];
        for (var i = 0; i < input.Count; i++)
        {
            var item = input[i];
            var valid = true;
            if (item.TeamId is not { } teamId || teamId == Guid.Empty)
            {
                violations.Add(new FieldViolation($"teamRoles[{i}].teamId", "required"));
                valid = false;
            }
            else if (!seenTeams.Add(teamId))
            {
                violations.Add(new FieldViolation($"teamRoles[{i}].teamId", "duplicate"));
                valid = false;
            }
            else if (!await members.TeamExistsAsync(teamId, cancellationToken))
            {
                violations.Add(new FieldViolation($"teamRoles[{i}].teamId", "not-found"));
                valid = false;
            }

            if (!TeamRoleRules.TryParse(item.Role, out var role))
            {
                violations.Add(new FieldViolation($"teamRoles[{i}].role", item.Role is null ? "required" : "invalid-value"));
                valid = false;
            }

            if (valid)
            {
                roles.Add(new InvitationTeamRoleRecord(item.TeamId!.Value, role));
            }
        }

        if (violations.Count > 0 || address is null || name is null)
        {
            return OperationFailure.Validation([.. violations]);
        }

        if (await invitations.IsAddressInUseAsync(address.Normalized, cancellationToken))
        {
            return await RefuseAsync(address, InvitationAuditEvents.ReasonEmailAddressInUse, scope, independent: false, cancellationToken);
        }

        if (await invitations.IsAccountNameTakenAsync(name.Normalized, cancellationToken))
        {
            return await RefuseAsync(address, InvitationAuditEvents.ReasonAccountNameTaken, scope, independent: false, cancellationToken);
        }

        var now = time.GetUtcNow();
        var id = Guid.NewGuid();
        bool inserted;
        try
        {
            inserted = await invitations.InsertAsync(
                new NewMemberInvitation(id, address.Value, address.Normalized, address.Hash, name, callerId, now, roles),
                cancellationToken);
        }
        catch (UniqueViolationException)
        {
            return await RefuseAsync(address, await ReasonOfConflictAsync(address, name, cancellationToken), scope, independent: true, cancellationToken);
        }

        if (!inserted)
        {
            return await RefuseAsync(address, await ReasonOfConflictAsync(address, name, cancellationToken), scope, independent: false, cancellationToken);
        }

        var linkId = Guid.NewGuid();
        await invitations.CreatePendingLinkAsync(linkId, id, now, cancellationToken);
        await outbox.QueueMemberInvitationAsync(address.Hash, linkId, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = InvitationAuditEvents.Created,
                Action = "create",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("invitation", id.ToString()),
                Details = new Dictionary<string, string>
                {
                    ["maskedAddress"] = address.Masked,
                    ["teamCount"] = roles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
            cancellationToken);

        var stored = await invitations.FindForUpdateAsync(id, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return stored!.Invitation;
    }

    private async Task<string> ReasonOfConflictAsync(EmailAddress address, AccountName name, CancellationToken cancellationToken) =>
        await invitations.IsAddressInUseAsync(address.Normalized, cancellationToken)
            ? InvitationAuditEvents.ReasonEmailAddressInUse
            : InvitationAuditEvents.ReasonAccountNameTaken;

    private async Task<OperationFailure> RefuseAsync(
        EmailAddress address,
        string reason,
        IUnitOfWorkScope scope,
        bool independent,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AuditEvent
        {
            EventType = InvitationAuditEvents.Created,
            Action = "create",
            Outcome = AuditOutcome.Refused,
            Resource = new AuditResource("invitation", null),
            ReasonCode = reason,
            Details = new Dictionary<string, string> { ["maskedAddress"] = address.Masked },
        };
        if (independent)
        {
            await audit.RecordIndependentAsync(auditEvent, cancellationToken);
        }
        else
        {
            await audit.RecordAsync(auditEvent, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        return OperationFailure.Conflict(reason);
    }
}
