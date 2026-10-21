using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

public sealed class ListAvailableAgentsHandler(
    IUnitOfWork unitOfWork,
    IMemberAccountStore accounts,
    IRequestContext requestContext,
    IAgentEnablementStore enablements)
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

        var records = await enablements.ListAsync(cancellationToken);
        IReadOnlyList<AvailableAgentView> agents =
        [
            .. records
                .Where(r => r.AgentVersion is not null && AgentCatalogIds.IsSpecialist(r.AgentId))
                .Select(r => new AvailableAgentView(
                    r.AgentId, AgentCatalogIds.GroupSpecialist, AgentCatalogIds.SpecialistDomains[r.AgentId],
                    AgentCatalogIds.DisplayNames[r.AgentId], r.AgentVersion!)),
        ];
        return OperationResult<IReadOnlyList<AvailableAgentView>>.Success(agents);
    }
}
