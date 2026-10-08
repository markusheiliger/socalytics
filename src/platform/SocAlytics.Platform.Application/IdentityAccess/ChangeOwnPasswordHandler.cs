using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class ChangeOwnPasswordHandler(
    IUnitOfWork unitOfWork,
    IAccountCredentialService credentials,
    ISessionStore sessions,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<bool>> HandleAsync(ChangeOwnPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var resource = new AuditResource("member", command.AccountId.ToString());
        var result = await credentials.ChangePasswordAsync(command.AccountId, command.CurrentPassword, command.NewPassword, cancellationToken);

        if (result.Outcome != PasswordChangeOutcome.Succeeded)
        {
            var violation = result.Outcome == PasswordChangeOutcome.WrongCurrentPassword
                ? new FieldViolation("currentPassword", "invalid")
                : new FieldViolation("newPassword", "password-policy");
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = "account.password-changed",
                    Action = "change-password",
                    Outcome = AuditOutcome.Failed,
                    Resource = resource,
                    ReasonCode = violation.Code,
                },
                cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Validation(violation);
        }

        await sessions.SetSecurityStampAsync(command.SessionId, result.SecurityStamp!, cancellationToken);
        await sessions.EndAllForAccountAsync(command.AccountId, "password-changed", command.SessionId, time.GetUtcNow(), cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "account.password-changed",
                Action = "change-password",
                Outcome = AuditOutcome.Succeeded,
                Resource = resource,
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }
}
