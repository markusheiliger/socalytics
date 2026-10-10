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
    IAuditTrail audit)
{
    public async Task<OperationResult<MemberCreation>> HandleAsync(CreateMemberCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

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

        var issued = await credentials.IssueCredentialAsync(accountId, CredentialPurpose.SetPassword, callerId, cancellationToken);
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
                Details = new Dictionary<string, string>
                {
                    ["purpose"] = CredentialPurpose.SetPassword.ToWireValue(),
                    ["targetAccountId"] = accountId.ToString(),
                },
            },
            cancellationToken);

        var member = await members.GetMemberAsync(accountId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new MemberCreation(member!, issued);
    }
}
