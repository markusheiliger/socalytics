using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class SignOutHandler(IUnitOfWork unitOfWork, ISessionStore sessions, IAuditTrail audit, TimeProvider time)
{
    public async Task<OperationResult<bool>> HandleAsync(SignOutCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        await sessions.EndAsync(command.SessionId, "sign-out", time.GetUtcNow(), cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "session.sign-out",
                Action = "sign-out",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("session", command.SessionId.ToString()),
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return true;
    }
}
