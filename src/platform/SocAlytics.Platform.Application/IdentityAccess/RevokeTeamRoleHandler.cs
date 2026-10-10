using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class RevokeTeamRoleHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IMemberAccountStore members,
    IAuditTrail audit)
{
    public async Task<OperationResult<MemberDetails>> HandleAsync(RevokeTeamRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var resource = new AuditResource("member", command.MemberId.ToString());
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, resource, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var account = await members.LockAccountAsync(command.MemberId, cancellationToken);
        if (account is null || !await members.TeamExistsAsync(command.TeamId, cancellationToken))
        {
            return OperationFailure.NotFound();
        }

        var removed = await members.RemoveTeamRoleAsync(command.MemberId, command.TeamId, cancellationToken);
        var details = new Dictionary<string, string>();
        if (removed is { } previous)
        {
            details["role"] = previous.ToWireValue();
            details["previousRole"] = previous.ToWireValue();
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "team-role.revoked",
                Action = "revoke-team-role",
                Outcome = removed is null ? AuditOutcome.Unchanged : AuditOutcome.Succeeded,
                Resource = resource,
                TeamId = command.TeamId,
                Details = details,
            },
            cancellationToken);

        var member = await members.GetMemberAsync(command.MemberId, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return member!;
    }
}
