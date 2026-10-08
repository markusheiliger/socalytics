using Microsoft.AspNetCore.Identity;
using Npgsql;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

internal sealed class AccountCredentialService(UserManager<IdentityMemberAccount> users) : IAccountCredentialService
{
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
