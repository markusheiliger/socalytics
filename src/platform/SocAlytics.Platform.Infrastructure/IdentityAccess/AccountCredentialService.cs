using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

internal sealed class AccountCredentialService(
    UserManager<IdentityMemberAccount> users,
    IPasswordHasher<IdentityMemberAccount> hasher,
    IOptions<IdentityAccessOptions> accessOptions,
    TimeProvider time,
    IDbSession dbSession,
    IRequestContext requestContext) : IAccountCredentialService
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

        var now = time.GetUtcNow();
        var lockout = accessOptions.Value.Lockout;
        var outcome = account is null ? SignInOutcome.UnknownAccount
            : account.LockoutEnd is { } lockoutEnd && lockoutEnd > now ? SignInOutcome.LockedOut
            : account.MembershipStatus != "active" ? SignInOutcome.InactiveMembership
            : storedHash is null ? SignInOutcome.NoPassword
            : !verified ? SignInOutcome.WrongPassword
            : SignInOutcome.Succeeded;

        if (outcome != SignInOutcome.Succeeded)
        {
            var countsFailure = outcome == SignInOutcome.WrongPassword;
            var triggered = countsFailure && account!.AccessFailedCount + 1 >= lockout.MaxFailedAccessAttempts;
            await RecordRefusalAsync(account, outcome, countsFailure, now, lockout, cancellationToken);
            return account is null
                ? new SignInVerification(outcome, null, null, false, false)
                : new SignInVerification(outcome, account.Id, account.SecurityStamp, account.PasswordChangeRequired, triggered);
        }

        if (account!.AccessFailedCount != 0 || account.LockoutEnd is not null)
        {
            account.AccessFailedCount = 0;
            account.LockoutEnd = null;
            await users.UpdateAsync(account);
        }

        return new SignInVerification(SignInOutcome.Succeeded, account.Id, account.SecurityStamp, account.PasswordChangeRequired, false);
    }

    // One statement on every refusal path: the UPDATE matches no row unless @CountsFailure, and the audit row is always written.
    private const string RefusalSql =
        """
        WITH failed AS (
            UPDATE socalytics.member_account
            SET access_failed_count = CASE WHEN access_failed_count + 1 >= @MaxFailed THEN 0 ELSE access_failed_count + 1 END,
                lockout_end = CASE WHEN access_failed_count + 1 >= @MaxFailed THEN @LockoutEnd ELSE lockout_end END
            WHERE id = @AccountId AND @CountsFailure
            RETURNING id)
        INSERT INTO socalytics.security_audit_event
            (id, occurred_at, event_type, action, outcome, actor_kind, actor_account_id, session_id,
             resource_type, resource_id, team_id, reason_code, details, correlation_id)
        VALUES
            (@Id, @OccurredAt, 'session.sign-in', 'sign-in', 'failed', @ActorKind, @ActorAccountId, NULL,
             @ResourceType, @ResourceId, NULL, @ReasonCode, '{}'::jsonb, @CorrelationId)
        """;

    private async Task RecordRefusalAsync(
        IdentityMemberAccount? account,
        SignInOutcome outcome,
        bool countsFailure,
        DateTimeOffset now,
        IdentityAccessOptions.LockoutOptions lockout,
        CancellationToken cancellationToken)
    {
        var transaction = dbSession.RequireTransaction();
        var connection = await dbSession.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            RefusalSql,
            new
            {
                AccountId = account?.Id ?? Guid.Empty,
                CountsFailure = countsFailure,
                MaxFailed = lockout.MaxFailedAccessAttempts,
                LockoutEnd = now + lockout.LockoutDuration,
                Id = Guid.CreateVersion7(),
                OccurredAt = now.UtcDateTime,
                ActorKind = account is null ? "anonymous" : "member",
                ActorAccountId = account?.Id,
                ResourceType = account is null ? "session" : "member",
                ResourceId = account?.Id.ToString(),
                ReasonCode = outcome switch
                {
                    SignInOutcome.UnknownAccount => "unknown-account",
                    SignInOutcome.NoPassword => "no-password",
                    SignInOutcome.WrongPassword => "wrong-password",
                    SignInOutcome.LockedOut => "locked-out",
                    _ => "inactive-membership",
                },
                requestContext.CorrelationId,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(
        Guid accountId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var account = await users.FindByIdAsync(accountId.ToString());
        var verified = hasher.VerifyHashedPassword(
                account ?? new IdentityMemberAccount(),
                account?.PasswordHash ?? DummyHash.Value,
                currentPassword ?? string.Empty) != PasswordVerificationResult.Failed;
        if (account is null || account.PasswordHash is null || !verified)
        {
            return new PasswordChangeResult(PasswordChangeOutcome.WrongCurrentPassword, null);
        }

        if ((await ValidatePasswordAsync(newPassword, cancellationToken)).Count > 0)
        {
            return new PasswordChangeResult(PasswordChangeOutcome.PolicyViolation, null);
        }

        account.PasswordChangeRequired = false;
        var result = await users.ChangePasswordAsync(account, currentPassword!, newPassword);
        return result.Succeeded
            ? new PasswordChangeResult(PasswordChangeOutcome.Succeeded, account.SecurityStamp)
            : new PasswordChangeResult(PasswordChangeOutcome.PolicyViolation, null);
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
