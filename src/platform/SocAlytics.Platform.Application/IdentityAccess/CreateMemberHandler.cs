using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class CreateMemberHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAccountCredentialService credentials,
    IMemberAccountStore members,
    IRequestContext requestContext,
    IAuditTrail audit,
    Microsoft.Extensions.Options.IOptions<IdentityAccessOptions> options)
{
    public async Task<OperationResult<MemberCreation>> HandleAsync(CreateMemberCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var purpose = CredentialPurpose.SetPassword;
        switch (command.CredentialKind)
        {
            case null or "set-password":
                break;
            case "identity-link":
                purpose = CredentialPurpose.IdentityLink;
                if (command.Provider is null || !options.Value.ExternalProviders.ContainsKey(command.Provider))
                {
                    return OperationFailure.Validation(new FieldViolation("credential.provider", "invalid-value"));
                }

                break;
            default:
                return OperationFailure.Validation(new FieldViolation("credential.kind", "invalid-value"));
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, new AuditResource("member", null), cancellationToken);
        if (!decision.IsGranted || requestContext.MemberAccountId is not { } callerId)
        {
            return OperationFailure.Forbidden();
        }

        if (!AccountName.TryCreate(command.AccountName, out var name, out var error))
        {
            return OperationFailure.Validation(new FieldViolation("accountName", error));
        }

        if (await members.FindAccountIdByNameAsync(name, cancellationToken) is not null)
        {
            return OperationFailure.Conflict("account-name-taken");
        }

        Guid accountId;
        try
        {
            accountId = await credentials.CreateAccountAsync(name, null, false, callerId, cancellationToken);
        }
        catch (UniqueViolationException)
        {
            return OperationFailure.Conflict("account-name-taken");
        }

        var issued = await credentials.IssueCredentialAsync(accountId, purpose, callerId, cancellationToken, command.Provider);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "member.created",
                Action = "create",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("member", accountId.ToString()),
            },
            cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "credential.issued",
                Action = "issue-credential",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("credential", issued.CredentialId.ToString()),
                Details = CredentialDetails(purpose, accountId, command.Provider),
            },
            cancellationToken);

        var member = await members.GetMemberAsync(accountId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new MemberCreation(member!, issued, purpose);
    }

    private static Dictionary<string, string> CredentialDetails(CredentialPurpose purpose, Guid accountId, string? provider)
    {
        var details = new Dictionary<string, string>
        {
            ["purpose"] = purpose.ToWireValue(),
            ["targetAccountId"] = accountId.ToString(),
        };
        if (purpose == CredentialPurpose.IdentityLink)
        {
            details["provider"] = provider!;
        }

        return details;
    }
}
