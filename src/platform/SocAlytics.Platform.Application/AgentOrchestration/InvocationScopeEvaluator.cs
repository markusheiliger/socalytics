using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.AgentOrchestration;

/// <summary>Decides whether an invocation's stored owner may still execute on the team, independent of the current request.</summary>
public interface IInvocationScopeEvaluator
{
    /// <summary>True only for an active owner without a pending password change who is Club Admin or Coach of the team.</summary>
    Task<bool> HasExecutionScopeAsync(Guid ownerMemberId, Guid teamId, CancellationToken cancellationToken);
}

public sealed class InvocationScopeEvaluator(IMemberAccountStore accounts) : IInvocationScopeEvaluator
{
    public async Task<bool> HasExecutionScopeAsync(Guid ownerMemberId, Guid teamId, CancellationToken cancellationToken)
    {
        var snapshot = await accounts.GetAccessSnapshotAsync(ownerMemberId, cancellationToken);
        if (snapshot is null || snapshot.Status != MembershipStatus.Active || snapshot.PasswordChangeRequired)
        {
            return false;
        }

        return snapshot.ClubRoles.Contains(ClubRole.ClubAdmin)
            || (snapshot.TeamRoles.TryGetValue(teamId, out var role) && role == TeamRole.Coach);
    }
}
