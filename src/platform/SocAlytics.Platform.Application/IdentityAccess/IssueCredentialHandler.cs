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
    IAuditTrail audit,
    Microsoft.Extensions.Options.IOptions<IdentityAccessOptions> options)
{
    public async Task<OperationResult<IssuedCredential>> HandleAsync(IssueCredentialCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Purpose == CredentialPurpose.IdentityLink
            && (command.ProviderId is null || !options.Value.ExternalProviders.ContainsKey(command.ProviderId)))
        {
            return OperationFailure.Validation(new FieldViolation("provider", "invalid-value"));
        }

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
        var applicable = command.Purpose switch
        {
            CredentialPurpose.SetPassword => !member!.PasswordSet,
            CredentialPurpose.PasswordReset => member!.PasswordSet,
            _ => true,
        };
        if (!applicable)
        {
            return OperationFailure.Conflict("credential-not-applicable");
        }

        var issued = await credentials.IssueCredentialAsync(command.MemberId, command.Purpose, callerId, cancellationToken, command.ProviderId);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "credential.issued",
                Action = "issue-credential",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("credential", issued.CredentialId.ToString()),
                Details = Details(command),
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return issued;
    }

    private static Dictionary<string, string> Details(IssueCredentialCommand command)
    {
        var details = new Dictionary<string, string>
        {
            ["purpose"] = command.Purpose.ToWireValue(),
            ["targetAccountId"] = command.MemberId.ToString(),
        };
        if (command.Purpose == CredentialPurpose.IdentityLink)
        {
            details["provider"] = command.ProviderId!;
        }

        return details;
    }
}
