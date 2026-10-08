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

public interface IAccountCredentialService
{
    /// <summary>Must run inside a unit of work; always performs exactly one password hash verification.</summary>
    Task<SignInVerification> VerifySignInAsync(string accountName, string password, CancellationToken cancellationToken);

    Task<IReadOnlyList<FieldViolation>> ValidatePasswordAsync(string password, CancellationToken cancellationToken);

    /// <exception cref="Abstractions.Persistence.UniqueViolationException">The account name is taken.</exception>
    Task<Guid> CreateAccountAsync(
        AccountName name,
        string? password,
        bool passwordChangeRequired,
        Guid? createdByAccountId,
        CancellationToken cancellationToken);
}
