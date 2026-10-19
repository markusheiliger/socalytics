using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public enum SignInOutcome
{
    Succeeded,
    UnknownAccount,
    NoPassword,
    WrongPassword,
    LockedOut,
    InactiveMembership,
}

/// <param name="LockoutTriggered">True when this attempt reached the failure threshold and started a lockout.</param>
public sealed record SignInVerification(
    SignInOutcome Outcome,
    Guid? AccountId,
    string? SecurityStamp,
    bool PasswordChangeRequired,
    bool LockoutTriggered);

public enum PasswordChangeOutcome
{
    Succeeded,
    WrongCurrentPassword,
    PolicyViolation,
}

public sealed record PasswordChangeResult(PasswordChangeOutcome Outcome, string? SecurityStamp);

public sealed record IssuedCredential(Guid CredentialId, string RawCredential, DateTimeOffset ExpiresAt);

public sealed record ConsumedLinkCredential(Guid AccountId, Guid IssuedBy);

public sealed record RevokedCredential(Guid CredentialId, Guid AccountId, CredentialPurpose Purpose, string Reason);

public enum CredentialRedemptionOutcome
{
    Succeeded,
    PolicyViolation,
    Invalid,
}

public sealed record CredentialRedemptionResult(CredentialRedemptionOutcome Outcome, Guid? AccountId, CredentialPurpose? Purpose);

public interface IAccountCredentialService
{
    /// <summary>Must run inside a unit of work; revokes the account's earlier open credentials as <c>superseded</c> and audits each.</summary>
    Task<IssuedCredential> IssueCredentialAsync(
        Guid accountId, CredentialPurpose purpose, Guid issuedBy, CancellationToken cancellationToken, string? providerId = null);

    /// <summary>Must run inside a unit of work; checks an unused link credential of an active member for the provider without consuming it and returns its id.</summary>
    Task<Guid?> ValidateLinkCredentialAsync(Guid accountId, string rawCredential, string providerId, CancellationToken cancellationToken);

    /// <summary>Must run inside a unit of work; consumes the link credential once; null when it is no longer valid for the provider.</summary>
    Task<ConsumedLinkCredential?> ConsumeLinkCredentialAsync(Guid credentialId, string providerId, CancellationToken cancellationToken);

    /// <summary>Must run inside a unit of work; a policy violation is reported before the credential is examined, every other mismatch is <see cref="CredentialRedemptionOutcome.Invalid"/>.</summary>
    Task<CredentialRedemptionResult> RedeemCredentialAsync(AccountName name, string rawCredential, string newPassword, CancellationToken cancellationToken);

    /// <summary>
    /// Must run inside a unit of work; validates the policy (<see cref="CredentialRedemptionOutcome.PolicyViolation"/> changes nothing),
    /// replaces the password, clears <c>password_change_required</c>, keeps the failed-attempt count and lockout end, and ends every live
    /// session as <c>password-reset</c>; <see cref="CredentialRedemptionOutcome.Invalid"/> when the account does not exist.
    /// </summary>
    Task<CredentialRedemptionOutcome> ResetPasswordByRecoveryAsync(Guid accountId, string newPassword, CancellationToken cancellationToken);

    /// <summary>Must run inside a unit of work; the caller records one <c>credential.revoked</c> event per returned credential.</summary>
    Task<IReadOnlyList<RevokedCredential>> RevokeOpenCredentialsIssuedByAsync(Guid issuerId, string reason, CancellationToken cancellationToken);

    /// <summary>Must run inside a unit of work; the caller records one <c>credential.revoked</c> event per returned credential.</summary>
    Task<IReadOnlyList<RevokedCredential>> RevokeOpenCredentialsForAccountAsync(Guid accountId, string reason, CancellationToken cancellationToken);

    /// <summary>Must run inside a unit of work; verifies the current password, applies the new one, clears the change requirement, and rotates the security stamp.</summary>
    Task<PasswordChangeResult> ChangePasswordAsync(Guid accountId, string currentPassword, string newPassword, CancellationToken cancellationToken);

    /// <summary>Must run inside a unit of work; always performs exactly one password hash verification.</summary>
    Task<SignInVerification> VerifySignInAsync(string accountName, string password, CancellationToken cancellationToken);

    /// <summary>Must run inside a unit of work; replaces the password and sets <c>password_change_required</c>, without touching sessions or lockout.</summary>
    Task SetTemporaryPasswordAsync(Guid accountId, string password, CancellationToken cancellationToken);

    Task<IReadOnlyList<FieldViolation>> ValidatePasswordAsync(string password, CancellationToken cancellationToken);

    /// <exception cref="Abstractions.Persistence.UniqueViolationException">The account name is taken.</exception>
    Task<Guid> CreateAccountAsync(
        AccountName name,
        string? password,
        bool passwordChangeRequired,
        Guid? createdByAccountId,
        CancellationToken cancellationToken);
}
