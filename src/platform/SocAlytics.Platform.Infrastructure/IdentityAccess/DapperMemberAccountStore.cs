using Dapper;
using Microsoft.AspNetCore.Identity;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

internal sealed class DapperMemberAccountStore(IDbSession session, TimeProvider time) :
    IUserStore<IdentityMemberAccount>,
    IUserPasswordStore<IdentityMemberAccount>,
    IUserSecurityStampStore<IdentityMemberAccount>,
    IUserLockoutStore<IdentityMemberAccount>,
    IUserTwoFactorStore<IdentityMemberAccount>
{
    private const string Columns =
        "id AS Id, account_name AS AccountName, normalized_account_name AS NormalizedAccountName, " +
        "password_hash AS PasswordHash, security_stamp AS SecurityStamp, lockout_enabled AS LockoutEnabled, " +
        "lockout_end AS LockoutEnd, access_failed_count AS AccessFailedCount, two_factor_enabled AS TwoFactorEnabled, " +
        "password_change_required AS PasswordChangeRequired, membership_status AS MembershipStatus, " +
        "membership_changed_at AS MembershipChangedAt, created_at AS CreatedAt, " +
        "created_by_account_id AS CreatedByAccountId, version AS Version";

    public void Dispose()
    {
    }

    public Task<string> GetUserIdAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id.ToString());

    public Task<string?> GetUserNameAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.AccountName);

    public Task SetUserNameAsync(IdentityMemberAccount user, string? userName, CancellationToken cancellationToken)
    {
        user.AccountName = userName ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.NormalizedAccountName);

    public Task SetNormalizedUserNameAsync(IdentityMemberAccount user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NormalizedAccountName = normalizedName ?? string.Empty;
        return Task.CompletedTask;
    }

    public async Task<IdentityResult> CreateAsync(IdentityMemberAccount user, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var now = time.GetUtcNow();
        user.MembershipStatus = "active";
        user.MembershipChangedAt = now;
        user.CreatedAt = now;
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.member_account (id, account_name, normalized_account_name, password_hash, security_stamp, " +
            "lockout_enabled, lockout_end, access_failed_count, two_factor_enabled, password_change_required, membership_status, " +
            "membership_changed_at, created_at, created_by_account_id) VALUES (@Id, @AccountName, @NormalizedAccountName, @PasswordHash, " +
            "@SecurityStamp, @LockoutEnabled, @LockoutEnd, @AccessFailedCount, @TwoFactorEnabled, @PasswordChangeRequired, @MembershipStatus, " +
            "@MembershipChangedAt, @CreatedAt, @CreatedByAccountId)",
            user,
            transaction,
            cancellationToken: cancellationToken));
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(IdentityMemberAccount user, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.member_account SET password_hash = @PasswordHash, security_stamp = @SecurityStamp, " +
            "lockout_enabled = @LockoutEnabled, lockout_end = @LockoutEnd, access_failed_count = @AccessFailedCount, " +
            "two_factor_enabled = @TwoFactorEnabled, password_change_required = @PasswordChangeRequired WHERE id = @Id",
            user,
            transaction,
            cancellationToken: cancellationToken));
        return IdentityResult.Success;
    }

    public Task<IdentityResult> DeleteAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Failed(new IdentityError
        {
            Code = "AccountsAreNeverDeleted",
            Description = "Member accounts are never deleted; deactivate them instead.",
        }));

    public Task<IdentityMemberAccount?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(userId, out var id)
            ? QueryAsync("id = @value", id, cancellationToken)
            : Task.FromResult<IdentityMemberAccount?>(null);

    public Task<IdentityMemberAccount?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        QueryAsync("normalized_account_name = @value", normalizedUserName, cancellationToken);

    private async Task<IdentityMemberAccount?> QueryAsync(string predicate, object value, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<IdentityMemberAccount>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.member_account WHERE {predicate}",
            new { value },
            session.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task SetPasswordHashAsync(IdentityMemberAccount user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(user.PasswordHash);

    public Task<bool> HasPasswordAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(user.PasswordHash is not null);

    public Task SetSecurityStampAsync(IdentityMemberAccount user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.SecurityStamp);

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(IdentityMemberAccount user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(++user.AccessFailedCount);

    public Task ResetAccessFailedCountAsync(IdentityMemberAccount user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(user.AccessFailedCount);

    public Task<bool> GetLockoutEnabledAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(user.LockoutEnabled);

    public Task SetLockoutEnabledAsync(IdentityMemberAccount user, bool enabled, CancellationToken cancellationToken)
    {
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task SetTwoFactorEnabledAsync(IdentityMemberAccount user, bool enabled, CancellationToken cancellationToken)
    {
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(IdentityMemberAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(user.TwoFactorEnabled);
}
