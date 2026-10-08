using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class ValidateSessionHandler(IUnitOfWork unitOfWork, ISessionStore sessions, TimeProvider time)
{
    public async Task<OperationResult<ValidatedSession>> HandleAsync(ValidateSessionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var now = time.GetUtcNow();
        var record = await sessions.FindByTokenHashAsync(query.TokenHash, cancellationToken);
        if (record is null || !IsValid(record, now))
        {
            return OperationFailure.Unauthenticated();
        }

        // Idle expiry was checked above, before sliding.
        var idleExpiresAt = await sessions.SlideAsync(record.SessionId, now, cancellationToken) ?? record.IdleExpiresAt;

        await scope.CommitAsync(cancellationToken);
        return new ValidatedSession(record.SessionId, record.AccountId, record.PasswordChangeRequired, idleExpiresAt, record.AbsoluteExpiresAt);
    }

    private static bool IsValid(SessionRecord record, DateTimeOffset now) =>
        record.EndedAt is null
        && record.MembershipStatus == "active"
        && now < record.IdleExpiresAt
        && now < record.AbsoluteExpiresAt
        && string.Equals(record.SessionSecurityStamp, record.AccountSecurityStamp, StringComparison.Ordinal);
}
