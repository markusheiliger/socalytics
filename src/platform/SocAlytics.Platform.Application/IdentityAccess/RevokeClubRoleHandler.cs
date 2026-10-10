using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class RevokeClubRoleHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAccountCredentialService credentials,
    IMemberAccountStore members,
    IAuditTrail audit)
{
    public async Task<OperationResult<MemberDetails>> HandleAsync(RevokeClubRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var resource = new AuditResource("member", command.MemberId.ToString());
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, resource, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var revokesAdmin = command.Role == ClubRole.ClubAdmin;
        if (revokesAdmin)
        {
            await members.LockClubAdminInvariantAsync(cancellationToken);
        }

        var account = await members.LockAccountAsync(command.MemberId, cancellationToken);
        if (account is null)
        {
            return OperationFailure.NotFound();
        }

        var outcome = AuditOutcome.Unchanged;
        IReadOnlyList<RevokedCredential> revoked = [];
        if (account.ClubRoles.Contains(command.Role))
        {
            if (revokesAdmin
                && account.Status == MembershipStatus.Active
                && await members.CountOtherActiveClubAdminsAsync(command.MemberId, cancellationToken) == 0)
            {
                return OperationFailure.Conflict("last-club-admin");
            }

            await members.RemoveClubRoleAsync(command.MemberId, command.Role, cancellationToken);
            outcome = AuditOutcome.Succeeded;
            if (revokesAdmin)
            {
                revoked = await credentials.RevokeOpenCredentialsIssuedByAsync(command.MemberId, "issuer-lost-authority", cancellationToken);
            }
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "club-role.revoked",
                Action = "revoke-club-role",
                Outcome = outcome,
                Resource = resource,
                Details = new Dictionary<string, string> { ["role"] = command.Role.ToWireValue() },
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
