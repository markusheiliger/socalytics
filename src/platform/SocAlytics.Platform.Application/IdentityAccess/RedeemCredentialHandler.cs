using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class RedeemCredentialHandler(
    IUnitOfWork unitOfWork,
    IAccountCredentialService credentials,
    IAuditTrail audit)
{
    public async Task<OperationResult<bool>> HandleAsync(RedeemCredentialCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var violations = new List<FieldViolation>();
        if (string.IsNullOrEmpty(command.AccountName))
        {
            violations.Add(new FieldViolation("accountName", "required"));
        }

        if (string.IsNullOrEmpty(command.Credential))
        {
            violations.Add(new FieldViolation("credential", "required"));
        }

        if (string.IsNullOrEmpty(command.NewPassword))
        {
            violations.Add(new FieldViolation("newPassword", "required"));
        }
        else
        {
            if ((await credentials.ValidatePasswordAsync(command.NewPassword, cancellationToken)).Count > 0)
            {
                violations.Add(new FieldViolation("newPassword", "password-policy"));
            }
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        // A name that cannot be an account name is indistinguishable from an unknown account.
        if (!AccountName.TryCreate(command.AccountName, out var name, out _))
        {
            await RecordFailureAsync(cancellationToken);
            return OperationFailure.CredentialInvalid();
        }

        var outcome = await TryRedeemAsync(name, command, cancellationToken);
        if (outcome == CredentialRedemptionOutcome.Succeeded)
        {
            return true;
        }

        await RecordFailureAsync(cancellationToken);
        return outcome == CredentialRedemptionOutcome.PolicyViolation
            ? OperationFailure.Validation(new FieldViolation("newPassword", "password-policy"))
            : OperationFailure.CredentialInvalid();
    }

    private async Task<CredentialRedemptionOutcome> TryRedeemAsync(
        AccountName name,
        RedeemCredentialCommand command,
        CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var result = await credentials.RedeemCredentialAsync(name, command.Credential!, command.NewPassword!, cancellationToken);
        if (result.Outcome != CredentialRedemptionOutcome.Succeeded)
        {
            return result.Outcome;
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "credential.redeemed",
                Action = "redeem-credential",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("member", result.AccountId!.Value.ToString()),
                ActorOverride = new AuditActorOverride(AuditActorKind.Member, result.AccountId),
                Details = new Dictionary<string, string> { ["purpose"] = result.Purpose!.Value.ToWireValue() },
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return CredentialRedemptionOutcome.Succeeded;
    }

    private Task RecordFailureAsync(CancellationToken cancellationToken) =>
        audit.RecordIndependentAsync(
            new AuditEvent
            {
                EventType = "credential.redeemed",
                Action = "redeem-credential",
                Outcome = AuditOutcome.Failed,
                Resource = new AuditResource("credential", null),
                ReasonCode = "credential-invalid",
            },
            cancellationToken);
}
