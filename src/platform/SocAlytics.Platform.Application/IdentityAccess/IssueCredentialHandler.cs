using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class IssueCredentialHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAccountCredentialService credentials,
    IMemberAccountStore members,
    IRequestContext requestContext,
    IAuditTrail audit)
{
    public async Task<OperationResult<IssuedCredential>> HandleAsync(IssueCredentialCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var resource = new AuditResource("member", command.MemberId.ToString());
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, resource, cancellationToken);
        if (!decision.IsGranted || requestContext.MemberAccountId is not { } callerId)
        {
            return OperationFailure.Forbidden();
        }

        var account = await members.LockAccountAsync(command.MemberId, cancellationToken);
        if (account is null)
        {
            return OperationFailure.NotFound();
        }

        if (account.Status != MembershipStatus.Active)
        {
            return OperationFailure.Conflict("membership-inactive");
        }

        var member = await members.GetMemberAsync(command.MemberId, cancellationToken);
        var applicable = command.Purpose == CredentialPurpose.SetPassword ? !member!.PasswordSet : member!.PasswordSet;
        if (!applicable)
        {
            return OperationFailure.Conflict("credential-not-applicable");
        }

        var issued = await credentials.IssueCredentialAsync(command.MemberId, command.Purpose, callerId, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "credential.issued",
                Action = "issue-credential",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("credential", issued.CredentialId.ToString()),
                Details = new Dictionary<string, string>
                {
                    ["purpose"] = command.Purpose.ToWireValue(),
                    ["targetAccountId"] = command.MemberId.ToString(),
                },
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return issued;
    }
}
