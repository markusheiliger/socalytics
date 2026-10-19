using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

public sealed class ListAvailableAgentsHandler(
    IUnitOfWork unitOfWork,
    IMemberAccountStore accounts,
    IRequestContext requestContext,
    IOptions<AgentOrchestrationOptions> options)
{
    public async Task<OperationResult<IReadOnlyList<AvailableAgentView>>> HandleAsync(CancellationToken cancellationToken)
    {
        if (requestContext.MemberAccountId is not { } accountId)
        {
            return OperationFailure.Unauthenticated();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var snapshot = await accounts.GetAccessSnapshotAsync(accountId, cancellationToken);
        if (snapshot is null || snapshot.Status != MembershipStatus.Active)
        {
            return OperationFailure.Forbidden();
        }

        if (snapshot.PasswordChangeRequired)
        {
            return OperationFailure.PasswordChangeRequired();
        }

        if (!snapshot.ClubRoles.Contains(ClubRole.ClubAdmin) && snapshot.TeamRoles.Count == 0)
        {
            return OperationFailure.Forbidden();
        }

        var enabled = options.Value.Agents.Enabled;
        IReadOnlyList<AvailableAgentView> agents =
        [
            .. AgentCatalogIds.Specialists
                .Where(id => enabled.Contains(id, StringComparer.Ordinal))
                .Select(id => new AvailableAgentView(id, "specialist", AgentCatalogIds.SpecialistDomains[id])),
        ];
        return OperationResult<IReadOnlyList<AvailableAgentView>>.Success(agents);
    }
}
