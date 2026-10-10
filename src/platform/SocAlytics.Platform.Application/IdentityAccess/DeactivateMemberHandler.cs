using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class DeactivateMemberHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAccountCredentialService credentials,
    IMemberAccountStore members,
    ISessionStore sessions,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<MemberDetails>> HandleAsync(DeactivateMemberCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var resource = new AuditResource("member", command.MemberId.ToString());
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, resource, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        await members.LockClubAdminInvariantAsync(cancellationToken);
        var account = await members.LockAccountAsync(command.MemberId, cancellationToken);
        if (account is null)
        {
            return OperationFailure.NotFound();
        }

        if (account.Status != MembershipStatus.Active)
        {
            return OperationFailure.Conflict("invalid-state-transition");
        }

        var heldAdmin = account.ClubRoles.Contains(ClubRole.ClubAdmin);
        if (heldAdmin && await members.CountOtherActiveClubAdminsAsync(command.MemberId, cancellationToken) == 0)
        {
            return OperationFailure.Conflict("last-club-admin");
        }

        await members.DeactivateAccountAsync(command.MemberId, cancellationToken);
        await sessions.EndAllForAccountAsync(command.MemberId, "deactivated", null, time.GetUtcNow(), cancellationToken);
        var revoked = new List<RevokedCredential>(
            await credentials.RevokeOpenCredentialsForAccountAsync(command.MemberId, "target-deactivated", cancellationToken));
        if (heldAdmin)
        {
            revoked.AddRange(await credentials.RevokeOpenCredentialsIssuedByAsync(command.MemberId, "issuer-lost-authority", cancellationToken));
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "member.deactivated",
                Action = "deactivate-member",
                Outcome = AuditOutcome.Succeeded,
                Resource = resource,
            },
            cancellationToken);
        foreach (var credential in revoked)
        {
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = "credential.revoked",
                    Action = "revoke-credential",
                    Outcome = AuditOutcome.Succeeded,
                    Resource = new AuditResource("credential", credential.CredentialId.ToString()),
                    ReasonCode = credential.Reason,
                    Details = new Dictionary<string, string>
                    {
                        ["purpose"] = credential.Purpose.ToWireValue(),
                        ["targetAccountId"] = credential.AccountId.ToString(),
                    },
                },
                cancellationToken);
        }

        var member = await members.GetMemberAsync(command.MemberId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return member!;
    }
}
