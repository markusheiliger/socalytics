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
    public const string IdentityLinkPurpose = "identity-link";
    private const string IdentityLinkPrefix = IdentityLinkPurpose + ":";

    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<IdentityMemberAccount> manager, IdentityMemberAccount user) =>
        Task.FromResult(false);

    public Task<string> GenerateAsync(string purpose, UserManager<IdentityMemberAccount> manager, IdentityMemberAccount user) =>
        throw new NotSupportedException("One-time credentials are issued through IAccountCredentialService.");

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<IdentityMemberAccount> manager, IdentityMemberAccount user)
    {
        // A link credential is validated as "identity-link:<provider>" so that the provider is checked as well.
        string? providerId = null;
        if (purpose.StartsWith(IdentityLinkPrefix, StringComparison.Ordinal) && purpose.Length > IdentityLinkPrefix.Length)
        {
            providerId = purpose[IdentityLinkPrefix.Length..];
            purpose = IdentityLinkPurpose;
        }

        if (purpose is not (SetPasswordPurpose or PasswordResetPurpose or IdentityLinkPurpose)
            || (purpose == IdentityLinkPurpose) != (providerId is not null)
            || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var connection = await dbSession.GetConnectionAsync(CancellationToken.None);
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM socalytics.one_time_credential " +
                "WHERE credential_hash = @Hash AND member_account_id = @AccountId AND purpose = @Purpose " +
                "AND provider_id IS NOT DISTINCT FROM @ProviderId " +
                "AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at > @Now",
                new
                {
                    Hash = SecretHashing.Sha256(token),
                    AccountId = user.Id,
                    Purpose = purpose,
                    ProviderId = providerId,
                    Now = time.GetUtcNow().UtcDateTime,
                },
                dbSession.Transaction));
        return count > 0;
    }
}
