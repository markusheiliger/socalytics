using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

internal sealed class AccountCredentialService(
    UserManager<IdentityMemberAccount> users,
    IPasswordHasher<IdentityMemberAccount> hasher,
    IOptions<IdentityAccessOptions> accessOptions,
    TimeProvider time) : IAccountCredentialService
{
    // Same PBKDF2 format and iteration count as stored hashes; generated once per process.
    private static readonly Lazy<string> DummyHash =
        new(() => new PasswordHasher<IdentityMemberAccount>().HashPassword(new IdentityMemberAccount(), "dummy-password-for-timing"));

    public async Task<SignInVerification> VerifySignInAsync(string accountName, string password, CancellationToken cancellationToken)
    {
        var account = await users.FindByNameAsync(accountName ?? string.Empty);
        var storedHash = account?.PasswordHash;

        // Exactly one verification on every path, before any refusal decision.
        var verified = hasher.VerifyHashedPassword(account ?? new IdentityMemberAccount(), storedHash ?? DummyHash.Value, password ?? string.Empty)
            != PasswordVerificationResult.Failed;

        if (account is null)
        {
            return new SignInVerification(SignInOutcome.UnknownAccount, null, null, false, false);
        }

        SignInVerification Refuse(SignInOutcome outcome, bool lockoutTriggered = false) =>
            new(outcome, account.Id, account.SecurityStamp, account.PasswordChangeRequired, lockoutTriggered);

        var now = time.GetUtcNow();
        if (account.LockoutEnd is { } lockoutEnd && lockoutEnd > now)
        {
            return Refuse(SignInOutcome.LockedOut);
        }

        if (account.MembershipStatus != "active")
        {
            return Refuse(SignInOutcome.InactiveMembership);
        }

        if (storedHash is null)
        {
            return Refuse(SignInOutcome.NoPassword);
        }

        if (!verified)
        {
            var lockout = accessOptions.Value.Lockout;
            var triggered = false;
            account.AccessFailedCount++;
            if (account.AccessFailedCount >= lockout.MaxFailedAccessAttempts)
            {
                account.LockoutEnd = now + lockout.LockoutDuration;
                account.AccessFailedCount = 0;
                triggered = true;
            }

            await users.UpdateAsync(account);
            return Refuse(SignInOutcome.WrongPassword, triggered);
        }

        if (account.AccessFailedCount != 0 || account.LockoutEnd is not null)
        {
            account.AccessFailedCount = 0;
            account.LockoutEnd = null;
            await users.UpdateAsync(account);
        }

        return Refuse(SignInOutcome.Succeeded);
    }

    public async Task<IReadOnlyList<FieldViolation>> ValidatePasswordAsync(string password, CancellationToken cancellationToken)
    {
        var probe = new IdentityMemberAccount();
        var violations = new List<FieldViolation>();
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, probe, password);
            foreach (var error in result.Errors)
            {
                var code = error.Code == "PasswordTooShort" ? "too-short" : "invalid";
                if (!violations.Any(v => v.Code == code))
                {
                    violations.Add(new FieldViolation("password", code));
                }
            }
        }

        return violations;
    }

    public async Task<Guid> CreateAccountAsync(
        AccountName name,
        string? password,
        bool passwordChangeRequired,
        Guid? createdByAccountId,
        CancellationToken cancellationToken)
    {
        var account = new IdentityMemberAccount
        {
            AccountName = name.Value,
            PasswordChangeRequired = passwordChangeRequired,
            CreatedByAccountId = createdByAccountId,
        };

        IdentityResult result;
        try
        {
            result = password is null
                ? await users.CreateAsync(account)
                : await users.CreateAsync(account, password);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new UniqueViolationException(ex.ConstraintName ?? "member_account", ex);
        }

        if (result.Succeeded)
        {
            return account.Id;
        }

        if (result.Errors.Any(e => e.Code is "DuplicateUserName"))
        {
            throw new UniqueViolationException("normalized_account_name");
        }

        throw new InvalidOperationException("The account could not be created: " + string.Join(", ", result.Errors.Select(e => e.Code)));
    }
}
