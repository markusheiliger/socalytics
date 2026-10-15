using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.Email;

public sealed record SetEmailAddressCommand(Guid AccountId, Guid ActorAccountId, string? Address, long? ExpectedVersion, bool ClubAdminAction = false);

public sealed record RemoveEmailAddressCommand(Guid AccountId, long ExpectedVersion, bool ClubAdminAction = false);

public sealed record ResendEmailConfirmationCommand(Guid AccountId);

public sealed record ConfirmEmailCommand(string? Token);

/// <param name="RetryAfter">Set when the resend limit is reached; the request then queued nothing.</param>
public sealed record ResendEmailConfirmationResult(TimeSpan? RetryAfter);

public sealed class GetEmailAddressHandler(IUnitOfWork unitOfWork, IMemberEmailAddressStore addresses)
{
    public async Task<OperationResult<StoredMemberEmailAddress>> HandleAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var stored = await addresses.FindAsync(accountId, cancellationToken);
        return stored is null ? OperationFailure.NotFound() : stored;
    }
}

internal static class MemberEmailAccess
{
    /// <summary>Checks that the caller administers the club and the target member exists; null means allowed.</summary>
    public static async Task<OperationFailure?> CheckAsync(
        IAccessAuthorizer authorizer, IMemberAccountStore accounts, Guid memberId, CancellationToken cancellationToken)
    {
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource("member", memberId.ToString()), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        return await accounts.GetAccessSnapshotAsync(memberId, cancellationToken) is null ? OperationFailure.NotFound() : null;
    }
}

public sealed class GetMemberEmailHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberAccountStore accounts,
    IMemberEmailAddressStore addresses)
{
    public async Task<OperationResult<StoredMemberEmailAddress>> HandleAsync(Guid memberId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (await MemberEmailAccess.CheckAsync(authorizer, accounts, memberId, cancellationToken) is { } failure)
        {
            return failure;
        }

        var stored = await addresses.FindAsync(memberId, cancellationToken);
        return stored is null ? OperationFailure.NotFound() : stored;
    }
}

public sealed class SetEmailAddressHandler(
    IUnitOfWork unitOfWork,
    IMemberEmailAddressStore addresses,
    IEmailConfirmationStore confirmations,
    IEmailOutbox outbox,
    IAuditTrail audit,
    IAccessAuthorizer authorizer,
    IMemberAccountStore accounts,
    TimeProvider time)
{
    public async Task<OperationResult<StoredMemberEmailAddress>> HandleAsync(SetEmailAddressCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!EmailAddress.TryCreate(command.Address, out var address, out var errorCode))
        {
            return OperationFailure.Validation("email-address-invalid", new FieldViolation("address", errorCode));
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (command.ClubAdminAction
            && await MemberEmailAccess.CheckAsync(authorizer, accounts, command.AccountId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var existing = await addresses.FindAsync(command.AccountId, cancellationToken);
        var now = time.GetUtcNow();
        if (existing is null)
        {
            if (command.ExpectedVersion is not null
                || !await addresses.InsertAsync(command.AccountId, address.Value, address.Normalized, command.ActorAccountId, now, cancellationToken))
            {
                return OperationFailure.VersionMismatch(null);
            }
        }
        else
        {
            if (command.ExpectedVersion is not { } expected)
            {
                return OperationFailure.VersionRequired();
            }

            var written = await addresses.ReplaceAsync(
                command.AccountId, address.Value, address.Normalized, command.ActorAccountId, now, expected, cancellationToken);
            if (written.Outcome != VersionedWriteOutcome.Applied)
            {
                return written.Outcome == VersionedWriteOutcome.NotFound
                    ? OperationFailure.VersionMismatch(null)
                    : OperationFailure.VersionMismatch(written.Version);
            }
        }

        await confirmations.SupersedeOpenAsync(command.AccountId, cancellationToken);
        var confirmationId = Guid.NewGuid();
        await confirmations.CreatePendingAsync(confirmationId, command.AccountId, address.Hash, cancellationToken);
        await outbox.QueueAsync(NotificationTypes.EmailConfirmationCode, command.AccountId, confirmationId, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = existing is null ? "identity.email-address-set" : "identity.email-address-changed",
                Action = existing is null ? "set-email-address" : "change-email-address",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("member", command.AccountId.ToString()),
                Details = new Dictionary<string, string> { ["maskedAddress"] = address.Masked },
            },
            cancellationToken);
        var stored = await addresses.FindAsync(command.AccountId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return stored!;
    }
}

public sealed class RemoveEmailAddressHandler(
    IUnitOfWork unitOfWork,
    IMemberEmailAddressStore addresses,
    IEmailConfirmationStore confirmations,
    IAuditTrail audit,
    IAccessAuthorizer authorizer,
    IMemberAccountStore accounts)
{
    public async Task<OperationResult<bool>> HandleAsync(RemoveEmailAddressCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (command.ClubAdminAction
            && await MemberEmailAccess.CheckAsync(authorizer, accounts, command.AccountId, cancellationToken) is { } denied)
        {
            return denied;
        }

        var existing = await addresses.FindAsync(command.AccountId, cancellationToken);
        if (existing is null)
        {
            return OperationFailure.NotFound();
        }

        var written = await addresses.DeleteAsync(command.AccountId, command.ExpectedVersion, cancellationToken);
        if (written.Outcome != VersionedWriteOutcome.Applied)
        {
            return written.Outcome == VersionedWriteOutcome.NotFound
                ? OperationFailure.NotFound()
                : OperationFailure.VersionMismatch(written.Version);
        }

        await confirmations.SupersedeOpenAsync(command.AccountId, cancellationToken);
        var details = EmailAddress.TryCreate(existing.Address, out var address, out _)
            ? new Dictionary<string, string> { ["maskedAddress"] = address.Masked }
            : null;
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "identity.email-address-removed",
                Action = "remove-email-address",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("member", command.AccountId.ToString()),
                Details = details,
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }
}

public sealed class ResendEmailConfirmationHandler(
    IUnitOfWork unitOfWork,
    IMemberEmailAddressStore addresses,
    IEmailConfirmationStore confirmations,
    IEmailMessageStore messages,
    IEmailOutbox outbox,
    IOptions<EmailConfirmationOptions> options,
    TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public async Task<OperationResult<ResendEmailConfirmationResult>> HandleAsync(ResendEmailConfirmationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var stored = await addresses.FindAsync(command.AccountId, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.NotFound();
        }

        if (stored.State == EmailAddressStates.Verified)
        {
            return OperationFailure.Conflict("email-already-verified");
        }

        var now = time.GetUtcNow();
        var since = now - Window;
        var count = await messages.CountCreatedSinceAsync(NotificationTypes.EmailConfirmationCode, command.AccountId, since, cancellationToken);
        if (count >= options.Value.MaxPerHour)
        {
            var oldest = await messages.OldestCreatedSinceAsync(NotificationTypes.EmailConfirmationCode, command.AccountId, since, cancellationToken);
            var wait = (oldest ?? now) + Window - now;
            return new ResendEmailConfirmationResult(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1));
        }

        if (!EmailAddress.TryCreate(stored.Address, out var address, out _))
        {
            return OperationFailure.NotFound();
        }

        await confirmations.SupersedeOpenAsync(command.AccountId, cancellationToken);
        var confirmationId = Guid.NewGuid();
        await confirmations.CreatePendingAsync(confirmationId, command.AccountId, address.Hash, cancellationToken);
        await outbox.QueueAsync(NotificationTypes.EmailConfirmationCode, command.AccountId, confirmationId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new ResendEmailConfirmationResult(null);
    }
}

public sealed class ConfirmEmailHandler(
    IUnitOfWork unitOfWork,
    IMemberEmailAddressStore addresses,
    IEmailConfirmationStore confirmations,
    IMemberAccountStore accounts,
    IAuditTrail audit,
    TimeProvider time)
{
    public const string InvalidCode = "email-confirmation-invalid";

    private static OperationFailure Invalid() => OperationFailure.Validation(InvalidCode);

    public async Task<OperationResult<bool>> HandleAsync(ConfirmEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Token) || command.Token.Length > 256)
        {
            return Invalid();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var confirmation = await confirmations.FindByTokenHashAsync(ConfirmationTokens.Hash(command.Token), cancellationToken);
        if (confirmation is not { State: EmailConfirmationStates.Issued })
        {
            return Invalid();
        }

        var now = time.GetUtcNow();
        if (confirmation.ExpiresAt is not { } expiresAt || expiresAt <= now)
        {
            await confirmations.MarkExpiredAsync(confirmation.Id, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return Invalid();
        }

        var stored = await addresses.FindAsync(confirmation.AccountId, cancellationToken);
        var snapshot = await accounts.GetAccessSnapshotAsync(confirmation.AccountId, cancellationToken);
        if (stored is null
            || snapshot is not { Status: MembershipStatus.Active }
            || !EmailAddress.TryCreate(stored.Address, out var address, out _)
            || address.Hash != confirmation.AddressHash
            || !await confirmations.MarkUsedAsync(confirmation.Id, now, cancellationToken))
        {
            return Invalid();
        }

        var written = await addresses.MarkVerifiedAsync(stored.AccountId, now, stored.Version, cancellationToken);
        if (written.Outcome != VersionedWriteOutcome.Applied)
        {
            return Invalid();
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "identity.email-address-verified",
                Action = "verify-email-address",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("member", stored.AccountId.ToString()),
                Details = new Dictionary<string, string> { ["maskedAddress"] = address.Masked },
                ActorOverride = new AuditActorOverride(AuditActorKind.Member, stored.AccountId),
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }
}
