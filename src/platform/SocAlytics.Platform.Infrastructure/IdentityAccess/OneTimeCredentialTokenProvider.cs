using Dapper;
using Microsoft.AspNetCore.Identity;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

/// <summary>Validates one-time credentials against <c>socalytics.one_time_credential</c>; issuance and consumption belong to <see cref="AccountCredentialService"/>.</summary>
internal sealed class OneTimeCredentialTokenProvider(IDbSession dbSession, TimeProvider time) : IUserTwoFactorTokenProvider<IdentityMemberAccount>
{
    public const string SetPasswordPurpose = "set-password";
    public const string PasswordResetPurpose = "password-reset";

    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<IdentityMemberAccount> manager, IdentityMemberAccount user) =>
        Task.FromResult(false);

    public Task<string> GenerateAsync(string purpose, UserManager<IdentityMemberAccount> manager, IdentityMemberAccount user) =>
        throw new NotSupportedException("One-time credentials are issued through IAccountCredentialService.");

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<IdentityMemberAccount> manager, IdentityMemberAccount user)
    {
        if (purpose is not (SetPasswordPurpose or PasswordResetPurpose) || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var connection = await dbSession.GetConnectionAsync(CancellationToken.None);
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM socalytics.one_time_credential " +
                "WHERE credential_hash = @Hash AND member_account_id = @AccountId AND purpose = @Purpose " +
                "AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at > @Now",
                new
                {
                    Hash = SecretHashing.Sha256(token),
                    AccountId = user.Id,
                    Purpose = purpose,
                    Now = time.GetUtcNow().UtcDateTime,
                },
                dbSession.Transaction));
        return count > 0;
    }
}
