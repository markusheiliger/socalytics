using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.Email;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;

public static class PasswordRecoveryAuditEvents
{
    public const string Requested = "password-recovery.requested";
    public const string Issued = "password-recovery.issued";
    public const string Completed = "password-recovery.completed";

    public const string ReasonQueued = "queued";
    public const string ReasonNotEligible = "not-eligible";
    public const string ReasonRateLimited = "rate-limited";
    public const string ReasonNoEligibleAccount = "no-eligible-account";
    public const string ReasonRecoveryInvalid = "recovery-invalid";
    public const string ReasonPasswordPolicy = "password-policy";
}

public sealed record RequestPasswordRecoveryCommand(string? Address);

public sealed class RequestPasswordRecoveryHandler(
    IUnitOfWork unitOfWork,
    IPasswordRecoveryStore recoveries,
    IEmailOutbox outbox,
    IEmailMessageStore messages,
    INotificationRecipientStore recipients,
    IOptions<PasswordRecoveryOptions> options,
    IAuditTrail audit,
    TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public async Task<OperationResult<bool>> HandleAsync(RequestPasswordRecoveryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!EmailAddress.TryCreate(command.Address, out var address, out var errorCode))
        {
            return OperationFailure.Validation(
                new FieldViolation("address", errorCode == "required" ? "required" : "invalid"));
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var candidates = await recoveries.ListCandidatesAsync(address.Normalized, cancellationToken);
        var details = new Dictionary<string, string> { ["maskedAddress"] = address.Masked };

        if (candidates.Count == 0)
        {
            await RecordAsync(null, PasswordRecoveryAuditEvents.ReasonNoEligibleAccount, details, cancellationToken);
        }

        foreach (var candidate in candidates)
        {
            var eligible = candidate.MembershipStatus == MembershipStatus.Active.ToWireValue()
                && candidate.HasPassword
                && candidate.AddressState == EmailAddressStates.Verified
                && candidate.AddressHash == address.Hash;
            if (eligible)
            {
                // Serializes concurrent requests per member so the hourly limit cannot be raced.
                await recipients.LockRecipientAsync(NotificationTypes.PasswordRecoveryCode, candidate.AccountId, cancellationToken);
                var count = await messages.CountCreatedSinceAsync(
                    NotificationTypes.PasswordRecoveryCode, candidate.AccountId, time.GetUtcNow() - Window, cancellationToken);
                if (count >= options.Value.MaxMessagesPerHour)
                {
                    await RecordAsync(candidate.AccountId, PasswordRecoveryAuditEvents.ReasonRateLimited, details, cancellationToken);
                    continue;
                }

                await recoveries.SupersedeOpenAsync(candidate.AccountId, cancellationToken);
                var recoveryId = Guid.NewGuid();
                await recoveries.CreatePendingAsync(
                    recoveryId, candidate.AccountId, candidate.AddressHash, candidate.SecurityStamp, time.GetUtcNow(), cancellationToken);
                await outbox.QueuePasswordRecoveryAsync(candidate.AccountId, candidate.AddressHash, recoveryId, cancellationToken);
            }

            await RecordAsync(
                candidate.AccountId,
                eligible ? PasswordRecoveryAuditEvents.ReasonQueued : PasswordRecoveryAuditEvents.ReasonNotEligible,
                details,
                cancellationToken);
        }

        await scope.CommitAsync(cancellationToken);
        return true;
    }

    private Task RecordAsync(Guid? accountId, string reason, IReadOnlyDictionary<string, string> details, CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new AuditEvent
            {
                EventType = PasswordRecoveryAuditEvents.Requested,
                Action = "request-password-recovery",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("member", accountId?.ToString()),
                ReasonCode = reason,
                Details = details,
            },
            cancellationToken);
}
