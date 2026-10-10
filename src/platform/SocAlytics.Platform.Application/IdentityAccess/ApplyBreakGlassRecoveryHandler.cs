using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

/// <summary><see cref="RefusalReason"/> is null when the directive was applied.</summary>
public sealed record BreakGlassRecoveryResult(string? RefusalReason)
{
    public bool Applied => RefusalReason is null;
}

public sealed class ApplyBreakGlassRecoveryHandler(
    IMemberAccountStore members,
    IAccountCredentialService credentials,
    ISessionStore sessions,
    IAuditTrail audit,
    IRequestContext requestContext,
    TimeProvider time)
{
    /// <summary>Requires an active unit of work; a refusal is audited and changes nothing else.</summary>
    /// <exception cref="UniqueViolationException">The recovery id was recorded concurrently.</exception>
    public async Task<BreakGlassRecoveryResult> ApplyWithinCurrentUnitOfWorkAsync(ApplyBreakGlassRecoveryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var recoveryId = command.RecoveryId;
        Guid? accountId = null;
        var reason = await EvaluateAsync();
        if (reason is not null)
        {
            await RecordAsync("break-glass-recovery.refused", AuditOutcome.Refused, accountId, recoveryId, reason, cancellationToken);
            return new BreakGlassRecoveryResult(reason);
        }

        var id = accountId!.Value;
        await credentials.SetTemporaryPasswordAsync(id, command.TemporaryCredential!, cancellationToken);
        await members.UnlockAccountAsync(id, cancellationToken);
        await members.RotateSecurityStampAsync(id, cancellationToken);
        await sessions.EndAllForAccountAsync(id, "break-glass-recovery", null, time.GetUtcNow(), cancellationToken);
        foreach (var credential in await credentials.RevokeOpenCredentialsForAccountAsync(id, "break-glass-recovery", cancellationToken))
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
                    ActorOverride = new AuditActorOverride(AuditActorKind.System, null),
                },
                cancellationToken);
        }

        await members.InsertRecoveryUseAsync(recoveryId!, id, time.GetUtcNow(), requestContext.CorrelationId, cancellationToken);
        await RecordAsync("break-glass-recovery.applied", AuditOutcome.Succeeded, id, recoveryId, null, cancellationToken);
        return new BreakGlassRecoveryResult(null);

        async Task<string?> EvaluateAsync()
        {
            if (string.IsNullOrWhiteSpace(command.AccountName)
                || string.IsNullOrWhiteSpace(recoveryId)
                || recoveryId.Length is < 8 or > 128
                || string.IsNullOrEmpty(command.TemporaryCredential))
            {
                return "directive-incomplete";
            }

            if (await members.RecoveryIdUsedAsync(recoveryId, cancellationToken))
            {
                return "recovery-id-used";
            }

            if (!AccountName.TryCreate(command.AccountName, out var name, out _)
                || await members.FindAccountIdByNameAsync(name, cancellationToken) is not { } found)
            {
                return "unknown-account";
            }

            accountId = found;
            var locked = await members.LockAccountAsync(found, cancellationToken);
            if (locked is null)
            {
                accountId = null;
                return "unknown-account";
            }

            if (locked.Status == MembershipStatus.Deactivated)
            {
                return "account-inactive";
            }

            return (await credentials.ValidatePasswordAsync(command.TemporaryCredential, cancellationToken)).Count > 0
                ? "password-policy"
                : null;
        }
    }

    private Task RecordAsync(string eventType, AuditOutcome outcome, Guid? accountId, string? recoveryId, string? reason, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new AuditEvent
            {
                EventType = eventType,
                Action = "break-glass-recovery",
                Outcome = outcome,
                Resource = new AuditResource("member", accountId?.ToString()),
                ReasonCode = reason,
                Details = string.IsNullOrWhiteSpace(recoveryId) ? null : new Dictionary<string, string> { ["recoveryId"] = recoveryId },
                ActorOverride = new AuditActorOverride(AuditActorKind.System, null),
            },
            cancellationToken);
}
