using System.Security.Cryptography;
using System.Text;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class SignInHandler(
    IUnitOfWork unitOfWork,
    IAccountCredentialService credentials,
    ISessionStore sessions,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<SignedInSession>> HandleAsync(SignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var now = time.GetUtcNow();
        var verification = await credentials.VerifySignInAsync(command.AccountName, command.Password, cancellationToken);

        if (command.PresentedTokenHash is { } presented)
        {
            var presentedSession = await sessions.FindByTokenHashAsync(presented, cancellationToken);
            if (presentedSession is { EndedAt: null })
            {
                await sessions.EndAsync(presentedSession.SessionId, "replaced", now, cancellationToken);
            }
        }

        if (verification.Outcome == SignInOutcome.Succeeded)
        {
            var rawToken = CreateToken();
            var accountId = verification.AccountId!.Value;
            var created = await sessions.CreateAsync(
                accountId,
                SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)),
                verification.SecurityStamp!,
                now,
                cancellationToken);
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = "session.sign-in",
                    Action = "sign-in",
                    Outcome = AuditOutcome.Succeeded,
                    Resource = new AuditResource("session", created.SessionId.ToString()),
                    ActorOverride = new AuditActorOverride(AuditActorKind.Member, accountId),
                },
                cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return new SignedInSession(
                created.SessionId,
                rawToken,
                created.IdleExpiresAt,
                created.AbsoluteExpiresAt,
                verification.PasswordChangeRequired,
                accountId);
        }

        // The account is named only when it exists; an unknown name is never recorded.
        var accountResource = verification.AccountId is { } id
            ? new AuditResource("member", id.ToString())
            : new AuditResource("session", null);
        var actor = verification.AccountId is { } actorId
            ? new AuditActorOverride(AuditActorKind.Member, actorId)
            : new AuditActorOverride(AuditActorKind.Anonymous, null);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "session.sign-in",
                Action = "sign-in",
                Outcome = AuditOutcome.Failed,
                Resource = accountResource,
                ReasonCode = ReasonCodeFor(verification.Outcome),
                ActorOverride = actor,
            },
            cancellationToken);
        if (verification.LockoutTriggered)
        {
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = "account.locked-out",
                    Action = "lock-out",
                    Outcome = AuditOutcome.Succeeded,
                    Resource = accountResource,
                    ActorOverride = new AuditActorOverride(AuditActorKind.System, null),
                },
                cancellationToken);
        }

        await scope.CommitAsync(cancellationToken);
        return OperationFailure.SignInFailed();
    }

    private static string ReasonCodeFor(SignInOutcome outcome) => outcome switch
    {
        SignInOutcome.UnknownAccount => "unknown-account",
        SignInOutcome.NoPassword => "no-password",
        SignInOutcome.WrongPassword => "wrong-password",
        SignInOutcome.LockedOut => "locked-out",
        SignInOutcome.InactiveMembership => "inactive-membership",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
