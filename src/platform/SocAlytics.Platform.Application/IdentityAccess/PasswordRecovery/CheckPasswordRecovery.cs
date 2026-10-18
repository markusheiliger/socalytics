using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.Email;
using SocAlytics.Platform.Application.Notifications;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;

public sealed record CheckPasswordRecoveryCommand(string? Token);

public sealed record PasswordRecoveryCheckResult(string AccountName);

/// <summary>A recovery that passed every usability check, with the member it belongs to.</summary>
internal sealed record UsablePasswordRecovery(StoredPasswordRecovery Recovery, string AccountName, EmailAddress Address);

/// <summary>The one usability check shared by the check and the completion; every failure is the same neutral validation failure.</summary>
internal sealed class PasswordRecoveryTokenValidator(
    IPasswordRecoveryStore recoveries,
    IMemberAccountStore accounts,
    IMemberEmailAddressStore addresses,
    TimeProvider time)
{
    public const string InvalidCode = "password-recovery-invalid";

    private const string IssuedState = "issued";

    public static OperationFailure Invalid() => OperationFailure.Validation(InvalidCode);

    /// <summary>Must run inside a unit of work; a null recovery means unknown, and a recovery that was marked <c>expired</c> or <c>invalidated</c> must be committed by the caller.</summary>
    public async Task<(UsablePasswordRecovery? Usable, StoredPasswordRecovery? Recovery)> ValidateAsync(
        string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
        {
            return (null, null);
        }

        var recovery = await recoveries.FindByTokenHashForUpdateAsync(ConfirmationTokens.Hash(token), cancellationToken);
        if (recovery is not { State: IssuedState })
        {
            return (null, recovery);
        }

        if (recovery.ExpiresAt is not { } expiresAt || expiresAt <= time.GetUtcNow())
        {
            await recoveries.MarkExpiredAsync(recovery.Id, cancellationToken);
            return (null, recovery);
        }

        var member = await accounts.GetMemberAsync(recovery.AccountId, cancellationToken);
        var binding = await accounts.GetSessionBindingAsync(recovery.AccountId, cancellationToken);
        var stored = await addresses.FindAsync(recovery.AccountId, cancellationToken);
        if (member is not { MembershipStatus: MembershipStatus.Active, PasswordSet: true }
            || binding is null
            || !string.Equals(binding.SecurityStamp, recovery.SecurityStamp, StringComparison.Ordinal)
            || stored is not { State: EmailAddressStates.Verified }
            || !EmailAddress.TryCreate(stored.Address, out var address, out _)
            || address.Hash != recovery.AddressHash)
        {
            await recoveries.MarkInvalidatedAsync(recovery.Id, cancellationToken);
            return (null, recovery);
        }

        return (new UsablePasswordRecovery(recovery, member.AccountName, address), recovery);
    }
}

public sealed class CheckPasswordRecoveryHandler(
    IUnitOfWork unitOfWork,
    IPasswordRecoveryStore recoveries,
    IMemberAccountStore accounts,
    IMemberEmailAddressStore addresses,
    TimeProvider time)
{
    public async Task<OperationResult<PasswordRecoveryCheckResult>> HandleAsync(
        CheckPasswordRecoveryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var validator = new PasswordRecoveryTokenValidator(recoveries, accounts, addresses, time);
        var (usable, _) = await validator.ValidateAsync(command.Token, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return usable is null
            ? PasswordRecoveryTokenValidator.Invalid()
            : new PasswordRecoveryCheckResult(usable.AccountName);
    }
}
