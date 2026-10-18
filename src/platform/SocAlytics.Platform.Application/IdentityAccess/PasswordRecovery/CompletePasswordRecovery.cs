using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.Email;

namespace SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;

public sealed record CompletePasswordRecoveryCommand(string? Token, string? NewPassword);

public sealed class CompletePasswordRecoveryHandler(
    IUnitOfWork unitOfWork,
    IPasswordRecoveryStore recoveries,
    IMemberAccountStore accounts,
    IMemberEmailAddressStore addresses,
    IAccountCredentialService credentials,
    IAuditTrail audit,
    TimeProvider time)
{
    private const string CompletedEventType = "password-recovery.completed";
    private const string CompleteAction = "complete-password-recovery";

    public async Task<OperationResult<bool>> HandleAsync(CompletePasswordRecoveryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var validator = new PasswordRecoveryTokenValidator(recoveries, accounts, addresses, time);
        var (usable, recovery) = await validator.ValidateAsync(command.Token, cancellationToken);
        if (usable is null)
        {
            await RecordFailedAsync(recovery?.AccountId, "recovery-invalid", null, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return PasswordRecoveryTokenValidator.Invalid();
        }

        var violation = string.IsNullOrEmpty(command.NewPassword)
            ? new FieldViolation("newPassword", "required")
            : (await credentials.ValidatePasswordAsync(command.NewPassword, cancellationToken)).Count > 0
                ? new FieldViolation("newPassword", "password-policy")
                : null;
        if (violation is null)
        {
            var outcome = await credentials.ResetPasswordByRecoveryAsync(usable.Recovery.AccountId, command.NewPassword!, cancellationToken);
            violation = outcome switch
            {
                CredentialRedemptionOutcome.Succeeded => null,
                CredentialRedemptionOutcome.PolicyViolation => new FieldViolation("newPassword", "password-policy"),
                _ => new FieldViolation("token", "recovery-invalid"),
            };
        }

        if (violation is { Field: "token" })
        {
            await recoveries.MarkInvalidatedAsync(usable.Recovery.Id, cancellationToken);
            await RecordFailedAsync(usable.Recovery.AccountId, violation.Code, usable.Address.Masked, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return PasswordRecoveryTokenValidator.Invalid();
        }

        if (violation is null && !await recoveries.MarkUsedAsync(usable.Recovery.Id, time.GetUtcNow(), cancellationToken))
        {
            return PasswordRecoveryTokenValidator.Invalid();
        }

        if (violation is not null)
        {
            await RecordFailedAsync(usable.Recovery.AccountId, violation.Code, usable.Address.Masked, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Validation(violation);
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = CompletedEventType,
                Action = CompleteAction,
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("member", usable.Recovery.AccountId.ToString()),
                Details = new Dictionary<string, string> { ["maskedAddress"] = usable.Address.Masked },
                ActorOverride = new AuditActorOverride(AuditActorKind.Member, usable.Recovery.AccountId),
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }

    private Task RecordFailedAsync(Guid? accountId, string reason, string? maskedAddress, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new AuditEvent
            {
                EventType = CompletedEventType,
                Action = CompleteAction,
                Outcome = AuditOutcome.Failed,
                Resource = new AuditResource("member", accountId?.ToString()),
                ReasonCode = reason,
                Details = maskedAddress is null ? null : new Dictionary<string, string> { ["maskedAddress"] = maskedAddress },
            },
            cancellationToken);
}
